import Foundation

/// 自检：异常情况下不误报（队列查询失败、结果未知、通知合并）、配置文件损坏时不被覆盖。
enum SelfTestMonitor {
    static func run(_ check: (Bool, String) -> Void) {
        // —— 队列查询失败：沿用上一份任务列表，不当成任务结束 ——
        let host = HostConfig(alias: "x", kind: .slurm)
        let before = [RemoteJob(id: "101", name: "relax", state: .running, source: .slurm)]
        func listed(_ rc: Int, _ lines: [String]) -> Bool {
            MonitorEngine.jobsListed(host: host, parsed: RemoteScript.parse((["@@SI:BEGIN", "@@SI:SEC squeue"] + lines + ["@@SI:RC \(rc)"]).joined(separator: "\n")))
        }
        let failed = listed(1, ["slurm_load_jobs error: Socket timed out on send/recv operation"])
        check(!failed && MonitorEngine.jobsAfterPoll(built: [], previous: before, listed: failed).map(\.id) == ["101"],
              "squeue 出错：沿用上一份任务列表，不产生结束提醒")
        let empty = listed(0, [])
        check(empty && MonitorEngine.jobsAfterPoll(built: [], previous: before, listed: empty).isEmpty,
              "squeue 成功且为空：任务确实离开了队列")

        // —— 离开队列的 Slurm 作业：只有 COMPLETED 才算成功 ——
        func outcome(_ raw: String?) -> (state: String, kind: IslandEvent.Kind) {
            MonitorEngine.slurmOutcome(raw.map { (JobState(slurm: $0), $0, nil, "") })
        }
        check(outcome(nil) == (HistoryEntry.endedUnknown, .info), "没有 sacct：历史记成“已结束”，提醒为结果未知")
        check(outcome("RUNNING") == (HistoryEntry.endedUnknown, .info) && outcome("COMPLETING") == (HistoryEntry.endedUnknown, .info),
              "sacct 仍显示运行中：不当成完成")
        check(outcome("COMPLETED") == ("COMPLETED", .finished) && outcome("OUT_OF_MEMORY") == ("OUT_OF_MEMORY", .failed)
              && outcome("CANCELLED by 123") == ("CANCELLED by 123", .failed) && outcome("SPECIAL_EXIT") == ("SPECIAL_EXIT", .info),
              "sacct 结束状态：完成 / 失败 / 不认识的状态")

        // —— 数组整体结束：sacct 里还有运行中的元素时不算成功 ——
        let arr = MonitorEngine.arrayCounts(Parsers.sacct(["500_1|scan|COMPLETED|00:10:00|0:0", "500_2|scan|RUNNING|00:09:00|0:0"]).values.map(\.state))
        check(arr == (1, 0, 1) && MonitorEngine.arrayOutcome(failed: arr.failed, accurate: arr.unknown == 0) == (HistoryEntry.endedUnknown, .info),
              "数组：还有元素显示运行中时，记成结果未知")
        check(MonitorEngine.arrayOutcome(failed: 0, accurate: true) == ("COMPLETED", .finished)
              && MonitorEngine.arrayOutcome(failed: 1, accurate: false) == ("FAILED", .failed), "数组：全部确认完成才算成功，有失败就算失败")
        let partial = MonitorEngine.arrayCounts([.completed], expectedTotal: 2)
        check(partial == (1, 0, 1)
              && MonitorEngine.arrayOutcome(failed: partial.failed, accurate: partial.unknown == 0) == (HistoryEntry.endedUnknown, .info),
              "数组：已知 2 个元素、只返回 1 个完成记录时不报成功")
        let complete = MonitorEngine.arrayCounts([.completed, .completed], expectedTotal: 2)
        check(complete == (2, 0, 0)
              && MonitorEngine.arrayOutcome(failed: complete.failed, accurate: complete.unknown == 0) == ("COMPLETED", .finished),
              "数组：返回全部元素的完成记录才报成功")

        // —— 完整的处理流程（ingest：解析 → 任务列表 → 事件），用样例输出走几轮 ——
        let lang = Lang.setting   // 新建的 ConfigStore 会把语言设成默认值，测完改回来
        for (ok, name) in ingestScenarios() { check(ok, name) }
        Lang.setting = lang

        // —— 多条通知合并：按种类计数 ——
        func events(_ kinds: [IslandEvent.Kind]) -> [IslandEvent] {
            kinds.enumerated().map { IslandEvent(kind: $1, title: "t\($0)", subtitle: "", hostName: "h") }
        }
        let unknown = MonitorEngine.merged(events([.info, .info, .info]), hostName: "h", hostID: nil)
        check(unknown.kind == .info && unknown.title == "3 个结果未知", "合并：3 条结果未知不会变成“开始运行”")
        let mixed = MonitorEngine.merged(events([.started, .started, .warning]), hostName: "h", hostID: nil)
        check(mixed.kind == .warning && mixed.title == "2 个开始运行，1 条警告", "合并：开始运行和磁盘警告分开计数")
        let all = MonitorEngine.merged(events([.finished, .failed, .info, .started]), hostName: "h", hostID: nil)
        check(all.kind == .failed && all.title == "1 个完成，1 个失败，1 个结果未知，1 个开始运行", "合并：有失败时按失败提醒")

        // —— 关注与提醒设置 ——
        let hid = UUID()
        var st = AppSettings()
        st.starredJobs = [StarredJob(hostID: hid, jobID: "101", name: "relax")]
        func ev(_ k: IslandEvent.Kind, _ job: String?) -> IslandEvent {
            IslandEvent(kind: k, title: "t", subtitle: "", hostName: "h", hostID: hid, jobID: job)
        }
        check(st.allowsAlert(ev(.finished, "102")) && st.allowsAlert(ev(.started, "101")), "提醒设置：默认提醒全部任务")
        st.alertScope = .starred
        check(st.allowsAlert(ev(.finished, "101")) && !st.allowsAlert(ev(.finished, "102")) && !st.allowsAlert(ev(.info, "102")),
              "提醒设置：只提醒关注的任务")
        check(st.allowsAlert(ev(.warning, nil)), "提醒设置：磁盘等服务器提醒不受关注范围影响")
        st.alertFailed = false; st.alertUnknown = false; st.alertStarted = false
        check(!st.allowsAlert(ev(.failed, "101")) && !st.allowsAlert(ev(.info, "101")) && !st.allowsAlert(ev(.started, "101"))
              && st.allowsAlert(ev(.finished, "101")), "提醒设置：开始 / 失败 / 结果未知分别开关")
        check(MonitorEngine.alertable([ev(.finished, "4242"), ev(.finished, "4343")], settings: st, watchedPIDs: ["4242"]).map(\.jobID) == ["4242"],
              "提醒设置：手动监听的进程在“只提醒关注的任务”时照常提醒")
        let jobs = ["1", "2", "3"].map { RemoteJob(id: $0, name: $0, state: .running, source: .slurm) }
        check(MonitorEngine.starredFirst(jobs) { $0.id == "3" }.map(\.id) == ["3", "1", "2"], "关注：关注的任务排在前面，其余顺序不变")
        if let data = try? JSONEncoder().encode(st), let back = try? JSONDecoder().decode(AppSettings.self, from: data),
           let old = try? JSONDecoder().decode(AppSettings.self, from: Data("{}".utf8)) {
            check(back.starredJobs == st.starredJobs && back.alertScope == .starred && !back.alertFailed && back.alertFinished,
                  "配置：关注和提醒设置可以保存、读回")
            check(old.starredJobs.isEmpty && old.alertScope == .all && old.alertStarted && old.alertFinished && old.alertFailed && old.alertUnknown,
                  "配置：旧配置文件默认提醒全部任务的全部事件")
        } else { check(false, "配置：关注和提醒设置编码失败") }

        // —— 勿扰时段 ——
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        func at(_ h: Int, _ m: Int) -> Date { cal.date(from: DateComponents(year: 2026, month: 1, day: 1, hour: h, minute: m))! }
        var q = AppSettings()
        check(!q.isQuiet(at: at(23, 30), calendar: cal), "勿扰：默认关闭")
        q.quietHours = true
        check(q.isQuiet(at: at(23, 0), calendar: cal) && q.isQuiet(at: at(2, 0), calendar: cal) && q.isQuiet(at: at(7, 59), calendar: cal)
              && !q.isQuiet(at: at(8, 0), calendar: cal) && !q.isQuiet(at: at(22, 59), calendar: cal), "勿扰：跨午夜 23:00–08:00")
        q.quietStart = 12 * 60; q.quietEnd = 14 * 60
        check(q.isQuiet(at: at(13, 0), calendar: cal) && !q.isQuiet(at: at(14, 0), calendar: cal) && !q.isQuiet(at: at(11, 59), calendar: cal),
              "勿扰：白天 12:00–14:00")
        q.quietEnd = 12 * 60
        check(!q.isQuiet(at: at(12, 0), calendar: cal), "勿扰：起止相同时不算勿扰")
        check(q.quietLabel == "12:00–12:00" && AppSettings.clock(23 * 60 + 30) == "23:30", "勿扰：时间文字")

        // —— 设置的导入导出 ——
        var cur = AppSettings()
        var a1 = HostConfig(alias: "a1"); a1.displayName = "A"
        cur.hosts = [a1]
        cur.islandScreen = "Studio Display"
        var imp = AppSettings()
        let jump = HostConfig(alias: "jump")
        var b1 = HostConfig(alias: "b1", kind: .slurm); b1.jumpHostID = jump.id
        var orphan = HostConfig(alias: "c1"); orphan.jumpHostID = UUID()
        var dup = HostConfig(alias: "a1")   // 和当前的 a1 连接目标相同
        dup.displayName = "A copy"
        imp.hosts = [jump, b1, orphan, dup]
        imp.defaultInterval = 300
        imp.starredJobs = [StarredJob(hostID: b1.id, jobID: "1"), StarredJob(hostID: UUID(), jobID: "2")]
        let data = (try? ConfigTransfer.exportData(imp)) ?? Data()
        let back = try? ConfigTransfer.read(data)
        check(back?.hosts.count == 4 && back?.defaultInterval == 300, "导入导出：导出的文件可以读回")
        check((try? ConfigTransfer.read(Data(#"{"name":"x"}"#.utf8))) == nil && (try? ConfigTransfer.read(Data("not json".utf8))) == nil,
              "导入导出：不是设置文件时拒绝")
        let replaced = ConfigTransfer.apply(current: cur, imported: imp, mode: .replace)
        check(replaced.hosts.count == 4 && replaced.defaultInterval == 300 && replaced.islandScreen == "Studio Display",
              "导入导出：替换全部设置，保留本机的显示器选择")
        check(replaced.hosts.first { $0.alias == "b1" }?.jumpHostID == jump.id && replaced.hosts.first { $0.alias == "c1" }?.jumpHostID == nil
              && replaced.starredJobs.map(\.jobID) == ["1"], "导入导出：去掉指向不存在服务器的跳板机和关注")
        let added = ConfigTransfer.apply(current: cur, imported: imp, mode: .addHosts)
        check(added.hosts.map(\.alias) == ["a1", "jump", "b1", "c1"] && added.defaultInterval == cur.defaultInterval,
              "导入导出：只添加新的服务器（连接目标相同的跳过），其它设置不动")

        // Windows / Linux 版导出的文件（多了 displayMode、sshBackend、petX 等跨平台版专有的字段）
        let fromCross = #"{"hosts":[{"id":"0A1B2C3D-0000-4000-8000-000000000001","alias":"hpc","kind":"slurm","auth":"key"}],"displayMode":"pet","sshBackend":"managed","petX":120,"quietHours":true,"quietStart":1320,"quietEnd":420,"alertScope":"starred","starredJobs":[{"hostID":"0A1B2C3D-0000-4000-8000-000000000001","jobID":"9","name":"relax","since":782700000}]}"#
        let crossRead = try? ConfigTransfer.read(Data(fromCross.utf8))
        check(crossRead?.hosts.first?.kind == .slurm && crossRead?.quietLabel == "22:00–07:00" && crossRead?.alertScope == .starred
              && crossRead?.starredJobs.first?.jobID == "9", "导入导出：读取 Windows / Linux 版导出的设置")

        // —— 配置文件：首次启动 / 正常 / 损坏（先备份） ——
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("si-config-test-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent("config.json")
        let missing = ConfigStore.load(from: file)
        check(missing.settings == nil && missing.problem == nil, "配置：首次启动没有文件，不算出错")
        try? Data("{}".utf8).write(to: file)
        check(ConfigStore.load(from: file).settings != nil, "配置：缺字段时补默认值")
        let broken = "{\"hosts\": [ {\"alias\": \"a\""
        try? Data(broken.utf8).write(to: file)
        let bad = ConfigStore.load(from: file)
        let backups = ((try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []).filter { $0.hasPrefix("config.broken-") }
        check(bad.settings == nil && bad.problem != nil && !bad.blockSave && backups.count == 1
              && (try? String(contentsOf: dir.appendingPathComponent(backups[0]), encoding: .utf8)) == broken,
              "配置：读取失败时先备份原文件并说明原因")

        // —— Agent 钩子：只追加 / 删除我们自己的那几条，用户原有的钩子和设置不动 ——
        let hookFile = dir.appendingPathComponent("settings.json")
        let original = #"{"theme":"dark","hooks":{"Stop":[{"hooks":[{"type":"command","command":"python3 other.py"}]}],"PreToolUse":[{"matcher":"*","hooks":[{"type":"command","command":"x"}]}]}}"#
        try? Data(original.utf8).write(to: hookFile)
        let installErr = AgentHooks.setClaude(installed: true, at: hookFile, executable: "/Apps/SI")
        let installed = (try? JSONSerialization.jsonObject(with: Data(contentsOf: hookFile))) as? [String: Any]
        let hookMap = installed?["hooks"] as? [String: Any]
        check(installErr == nil && AgentHooks.claudeInstalled(at: hookFile) && (hookMap?["Stop"] as? [Any])?.count == 2
              && (hookMap?["PreToolUse"] as? [Any])?.count == 2 && installed?["theme"] as? String == "dark",
              "Agent 钩子：安装时保留原有的钩子和设置")
        AgentHooks.setClaude(installed: false, at: hookFile)
        let restored = (try? JSONSerialization.jsonObject(with: Data(contentsOf: hookFile))) as? NSDictionary
        let originalObj = (try? JSONSerialization.jsonObject(with: Data(original.utf8))) as? NSDictionary
        check(restored != nil && restored == originalObj && !AgentHooks.claudeInstalled(at: hookFile), "Agent 钩子：卸载后和原来一样")
        let mixedHooks = #"{"hooks":{"Stop":[{"matcher":"*","hooks":[{"type":"command","command":"/Apps/SI --agent-event claude"},{"type":"command","command":"echo keep-me"}]}]}}"#
        try? Data(mixedHooks.utf8).write(to: hookFile)
        AgentHooks.setClaude(installed: true, at: hookFile, executable: "/Apps/SI")
        AgentHooks.setClaude(installed: false, at: hookFile)
        let kept = (try? String(contentsOf: hookFile, encoding: .utf8)) ?? ""
        check(kept.contains("keep-me") && kept.contains("matcher") && !kept.contains("--agent-event"), "Agent 钩子：同组的其他命令和匹配条件不删除")

        // 用临时会话重现中间消息、错轮次和内部辅助会话导致的完成误报。
        MainActor.assumeIsolated {
            let monitor = AgentReader()
            var alerts = 0
            monitor.onAlert = { _ in alerts += 1 }
            let root = dir.appendingPathComponent("root.jsonl")
            let child = dir.appendingPathComponent("child.jsonl")
            func append(_ file: URL, _ type: String, _ payload: [String: Any], at: Date = Date()) {
                let obj: [String: Any] = ["type": type, "timestamp": ISO8601DateFormatter.fractional.string(from: at), "payload": payload]
                var data = try! JSONSerialization.data(withJSONObject: obj)
                data.append(0x0A)
                if !FileManager.default.fileExists(atPath: file.path) { try! data.write(to: file) }
                else {
                    let h = try! FileHandle(forWritingTo: file)
                    defer { try? h.close() }
                    try! h.seekToEnd()
                    try! h.write(contentsOf: data)
                }
            }
            append(root, "session_meta", ["source": "vscode", "cwd": "/demo"])
            append(root, "event_msg", ["type": "task_started", "turn_id": "one"])
            monitor.readCodex(root, alert: false)
            append(root, "response_item", ["role": "assistant", "channel": "commentary"])
            append(root, "event_msg", ["type": "task_complete", "turn_id": "old"])
            monitor.readCodex(root, alert: true)
            check(alerts == 0 && monitor.list.first?.state == .working, "Codex：中间消息和其它轮次完成不误报")
            append(root, "event_msg", ["type": "task_complete", "turn_id": "one"])
            monitor.readCodex(root, alert: true)
            monitor.readCodex(root, alert: true)
            check(alerts == 1 && monitor.list.first?.state == .done, "Codex：当前轮次完成只提醒一次")
            append(root, "event_msg", ["type": "task_started", "turn_id": "two"])
            append(root, "event_msg", ["type": "turn_aborted", "turn_id": "two"])
            monitor.readCodex(root, alert: true)
            check(alerts == 1, "Codex：中断不报回答完毕")
            append(child, "session_meta", ["source": ["subagent": ["other": "guardian"]], "parent_thread_id": "parent"])
            append(child, "response_item", ["text": String(repeating: "x", count: 450_000)])
            append(child, "event_msg", ["type": "task_started", "turn_id": "child"])
            append(child, "event_msg", ["type": "task_complete", "turn_id": "child"])
            monitor.readCodex(child, alert: true)
            check(alerts == 1 && monitor.sessions.count == 1, "Codex：从长文件尾部读取也过滤内部辅助会话")
            append(root, "event_msg", ["type": "task_started", "turn_id": "past"], at: Date().addingTimeInterval(-3600))
            append(root, "event_msg", ["type": "task_complete", "turn_id": "past"], at: Date().addingTimeInterval(-3500))
            monitor.readCodex(root, alert: true)
            check(alerts == 1, "Codex：不补报历史完成事件")

            append(root, "event_msg", ["type": "task_started", "turn_id": "questions"])
            let args = #"{"questions":[{"title":"Pick a mode"},{"title":"Pick a duration"}]}"#
            append(root, "response_item", ["type": "function_call", "name": "request_user_input_async", "call_id": "ask", "arguments": args])
            append(root, "response_item", ["type": "function_call_output", "call_id": "ask", "output": #"{"accepted":true}"#])
            monitor.readCodex(root, alert: true)
            check(alerts == 2 && monitor.list.first?.state == .attention, "Codex：异步提问会提醒，受理回执不清除等待状态")
            @MainActor func answer(_ index: Int) {
                let id = String(data: try! JSONSerialization.data(withJSONObject: ["request_user_input_async", "ask", index]), encoding: .utf8)!
                let replies = String(data: try! JSONSerialization.data(withJSONObject: [["questionItemId": id, "answer": "A"]]), encoding: .utf8)!
                append(root, "response_item", ["type": "message", "role": "user", "content": [["type": "input_text", "text": "<send_user_message_question_reply>\n\(replies)\n</send_user_message_question_reply>"]]])
                monitor.readCodex(root, alert: true)
            }
            answer(0)
            check(monitor.list.first?.state == .attention && alerts == 2, "Codex：部分答复仍保留其余待答问题")
            answer(1)
            check(monitor.list.first?.state == .working && alerts == 2, "Codex：全部答复后恢复工作，不报回答完毕")
            append(root, "response_item", ["type": "function_call", "name": "functions.request_user_input", "call_id": "sync", "arguments": args])
            monitor.readCodex(root, alert: true)
            append(root, "response_item", ["type": "function_call_output", "call_id": "sync", "output": #"{"answers":{}}"#])
            monitor.readCodex(root, alert: true)
            check(alerts == 3 && monitor.list.first?.state == .working, "Codex：同步提问和返回结果配对，不重复提醒")

            let tools = AgentReader()
            var needsInput = 0
            tools.onAlert = { if $0.state == .attention { needsInput += 1 } }
            tools.apply(["tool": "claude", "session": "c", "event": "PreToolUse", "toolName": "AskUserQuestion"], alert: true)
            check(tools.list.first?.state == .attention && needsInput == 1, "Claude：AskUserQuestion 进入等待回答并提醒")
            tools.apply(["tool": "claude", "session": "c", "event": "PostToolUse", "toolName": "AskUserQuestion"], alert: true)
            tools.apply(["tool": "claude", "session": "c", "event": "Notification", "kind": "elicitation_response"], alert: true)
            check(tools.list.first?.state == .working && needsInput == 1, "Claude：问题答复或 MCP 答复后恢复，不再次报需要确认")
            let t0 = Date().timeIntervalSince1970 + 10
            tools.apply(["tool": "claude", "session": "late", "event": "PermissionRequest", "toolName": "Bash", "at": t0], alert: true)
            tools.apply(["tool": "claude", "session": "late", "event": "PostToolUse", "toolName": "Bash", "at": t0 + 3], alert: true)
            tools.apply(["tool": "claude", "session": "late", "event": "Notification", "kind": "permission_prompt", "at": t0 + 1], alert: true)
            check(tools.sessions["claude|late"]?.state == .working && needsInput == 2, "Claude：晚到的 permission_prompt 通知不会把已批准的会话改回需要确认")
            tools.apply(["tool": "opencode", "session": "o", "event": "question.asked", "message": "Choose a mode"], alert: true)
            check(needsInput == 3, "OpenCode：question.asked 会提醒")
            tools.apply(["tool": "opencode", "session": "o", "event": "question.replied"], alert: true)
            tools.apply(["tool": "opencode", "session": "o", "event": "permission.asked"], alert: true)
            tools.apply(["tool": "opencode", "session": "o", "event": "permission.replied"], alert: true)
            check(needsInput == 4 && tools.sessions["opencode|o"]?.state == .working, "OpenCode：问题及权限答复均解除等待")
            tools.apply(["tool": "opencode", "session": "o", "event": "session.status", "status": "idle"], alert: false)
            check(tools.sessions["opencode|o"]?.state == .done, "OpenCode：idle 状态不会误判为工作中")

            tools.apply(["tool": "claude", "session": "late", "event": "Stop", "at": t0 + 5], alert: false)
            tools.apply(["tool": "claude", "session": "late", "event": "PostToolUse", "at": t0 + 4,
                         "plan": ["set": [["新清单", "completed"]]]], alert: false)
            tools.apply(["tool": "claude", "session": "late", "event": "PostToolUse", "at": t0 + 2,
                         "plan": ["set": [["旧清单", "pending"]]]], alert: false)
            check(tools.sessions["claude|late"]?.state == .done && tools.sessions["claude|late"]?.plan.done == 1,
                  "Agent 清单：晚到的清单不改变完成状态，旧清单不覆盖新清单")

            let events = dir.appendingPathComponent("events.jsonl")
            let codexDir = dir.appendingPathComponent("sessions/2000/01/01")
            try? FileManager.default.createDirectory(at: codexDir, withIntermediateDirectories: true)
            let reader = AgentReader(eventsURL: events, codexRoot: dir.appendingPathComponent("sessions"))
            var replayAlerts = 0
            reader.onAlert = { _ in replayAlerts += 1 }
            let base = Date().timeIntervalSince1970 - 10
            func hook(_ event: String, _ at: Double, _ plan: [String: Any]? = nil) {
                var o: [String: Any] = ["tool": "claude", "session": "reconnect", "event": event, "at": at]
                if let plan { o["plan"] = plan }
                var data = try! JSONSerialization.data(withJSONObject: o)
                data.append(0x0A)
                if !FileManager.default.fileExists(atPath: events.path) { FileManager.default.createFile(atPath: events.path, contents: nil) }
                let h = try! FileHandle(forWritingTo: events)
                _ = try? h.seekToEnd(); try? h.write(contentsOf: data); try? h.close()
            }
            hook("UserPromptSubmit", base)
            hook("PostToolUse", base + 1, ["add": "A"])
            hook("PostToolUse", base + 2, ["add": "B"])
            reader.loadInitial()
            reader.loadInitial()
            check(reader.sessions["claude|reconnect"]?.plan.total == 2, "Agent 重连：增量清单不重复回放")
            hook("Stop", base + 3)
            reader.loadInitial()
            check(reader.sessions["claude|reconnect"]?.state == .done && replayAlerts == 0, "Agent 重连：断开期间的完成不补发提醒")
            let old = codexDir.appendingPathComponent("old.jsonl")
            append(old, "session_meta", ["source": "cli", "cwd": "/demo"])
            append(old, "event_msg", ["type": "task_started", "turn_id": "old-turn"])
            let discovery = AgentReader(eventsURL: events, codexRoot: dir.appendingPathComponent("sessions"))
            discovery.loadInitial()
            check(discovery.sessions["codex|old"]?.state == .working, "Codex：旧日期目录中的活跃会话仍能发现")
            let longFile = dir.appendingPathComponent("long.jsonl")
            let longReader = AgentReader(eventsURL: dir.appendingPathComponent("none"), codexRoot: dir.appendingPathComponent("empty"))
            var longAlerts = 0
            longReader.onAlert = { _ in longAlerts += 1 }
            append(longFile, "session_meta", ["source": "cli", "cwd": "/demo"], at: Date().addingTimeInterval(-1900))
            append(longFile, "event_msg", ["type": "task_started", "turn_id": "long"], at: Date().addingTimeInterval(-1900))
            longReader.readCodex(longFile, alert: false)
            longReader.poll()
            let retained = longReader.list.first?.state == .working
            append(longFile, "event_msg", ["type": "task_complete", "turn_id": "long"])
            longReader.readCodex(longFile, alert: true)
            check(retained && longAlerts == 1, "Codex：超过 30 分钟的轮次仍保留并提醒完成")
            longReader.apply(["tool": "claude", "session": "active", "event": "UserPromptSubmit", "at": Date().timeIntervalSince1970 - 1900], alert: false)
            longReader.apply(["tool": "claude", "session": "active", "event": "PostToolUse"], alert: false)
            longReader.poll()
            check(longReader.sessions["claude|active"]?.state == .working, "Agent：按最近活动而不是状态起始时间清理")

            let jobs: [[String: Any]] = (0..<8).map { ["id": String($0), "name": String(repeating: "任", count: 80), "star": $0 == 0,
                                                      "detail": String(repeating: "节", count: 120), "dir": String(repeating: "路", count: 120), "msg": String(repeating: "进", count: 120)] }
            let large: [String: Any] = ["t": "snap", "hosts": (0..<3).map { ["name": "demo-\($0)", "njobs": 8, "jobs": jobs] }]
            let fit = DisplayLink.fitSnapshot(large, limit: 1500)
            let fitHosts = fit["hosts"] as? [[String: Any]] ?? []
            let fitJobs = fitHosts.flatMap { $0["jobs"] as? [[String: Any]] ?? [] }
            let encoded = try! JSONSerialization.data(withJSONObject: fit, options: [.withoutEscapingSlashes])
            check(encoded.count + 1 <= 1500 && fitJobs.filter { $0["star"] as? Bool == true }.count == 3 && fitHosts.count == 3,
                  "副屏：UTF-8 总字节受限，优先保留关注任务和服务器")
        }
        let times = (8...20).map { String(format: "2026-11-01T%02d:00", $0) }
        var codes = Array(repeating: 1, count: times.count), probs = Array(repeating: 10, count: times.count)
        codes[1] = 61; probs[6] = 70; probs[8] = 80  // 9 点下过雨（已过去），14、16 点可能有雨
        let rain = DisplayExtras.rainAhead(times: times, codes: codes, probs: probs, now: "2026-11-01T11:20")
        check(rain?.from == 14 && rain?.prob == 80 && DisplayExtras.rainAhead(times: times, codes: codes, probs: probs, now: "2026-11-01T17:05") == nil,
              "副屏：今天接下来的下雨时间")

        // Agent 任务清单：整张清单、桌面 App 的增量操作、TaskCreate / TaskUpdate、Codex 的 plan。
        var plan = AgentPlan()
        plan.apply(AgentPlan.op(toolName: "mcp__ccd_turn__update_plan", input: ["items": [["content": "读代码", "status": "in_progress"],
                                                                                         ["content": "改界面", "status": "pending"],
                                                                                         ["content": "截图", "status": "pending"]]])!)
        plan.apply(AgentPlan.op(toolName: "mcp__ccd_turn__update_plan", input: ["advance": true])!)
        check(plan.done == 1 && plan.total == 3 && plan.now == "改界面", "Agent 清单：桌面 App 的 advance")
        plan.apply(AgentPlan.op(toolName: "mcp__ccd_turn__update_plan", input: ["done": [2], "start": 3])!)
        check(plan.done == 2 && plan.now == "截图", "Agent 清单：done / start")
        var tasks = AgentPlan()
        for t in ["A", "B"] { tasks.apply(AgentPlan.op(toolName: "TaskCreate", input: ["subject": t])!) }
        tasks.apply(AgentPlan.op(toolName: "TaskUpdate", input: ["taskId": "1", "status": "completed"])!)
        tasks.apply(AgentPlan.op(toolName: "TaskUpdate", input: ["taskId": "2", "status": "deleted"])!)
        check(tasks.done == 1 && tasks.total == 1, "Agent 清单：TaskCreate / TaskUpdate")
        var todo = AgentPlan()
        todo.apply(AgentPlan.op(toolName: "TodoWrite", input: ["todos": [["content": "x", "status": "completed"], ["content": "y", "status": "in_progress"]]])!)
        var codexPlan = AgentPlan()
        codexPlan.apply(AgentPlan.op(toolName: "update_plan", input: ["plan": [["step": "s1", "status": "completed"], ["step": "s2", "status": "pending"]]])!)
        check(todo.now == "y" && codexPlan.done == 1 && codexPlan.now == "s2" && AgentPlan.op(toolName: "Bash", input: [:]) == nil,
              "Agent 清单：TodoWrite、Codex plan，其他工具不算")

        // Claude 的 5 小时窗口（用 CC Switch 的调用记录估算）。
        let h = 3600.0, base = Date(timeIntervalSince1970: 1_793_491_200)  // 整点
        let w = AIUsage.window([(base.addingTimeInterval(0.2 * h), 10), (base.addingTimeInterval(1 * h), 5),
                                (base.addingTimeInterval(6 * h), 7), (base.addingTimeInterval(7 * h), 3)], now: base.addingTimeInterval(8 * h))
        check(w?.tokens == 10 && w?.reset == base.addingTimeInterval(11 * h)
              && AIUsage.window([(base, 1)], now: base.addingTimeInterval(5 * h)) == nil, "AI 用量：Claude 的 5 小时窗口")
    }

    /// 一段样例输出：每段 (名字, 退出码, 行)；退出码为 nil 时这段整个缺失（输出被截断等）。
    private static func output(_ secs: [(String, Int32?, [String])]) -> RemoteScript.Parsed {
        var lines = ["@@SI:BEGIN"]
        for (name, rc, body) in secs {
            guard let rc else { continue }
            lines += ["@@SI:SEC \(name)"] + body + ["@@SI:RC \(rc)"]
        }
        return RemoteScript.parse((lines + ["@@SI:END"]).joined(separator: "\n"))
    }

    /// 用一个不连网的引擎（主机设为停用，不会真的轮询）走几轮 ingest，返回 (是否通过, 名字)。
    private static func ingestScenarios() -> [(Bool, String)] {
        final class Results: @unchecked Sendable { var items: [(Bool, String)] = [] }
        let results = Results()
        let sem = DispatchSemaphore(value: 0)
        Task { @MainActor in
            let store = ConfigStore(persist: false)
            store.settings.soundEnabled = false
            store.settings.islandAlerts = false
            let engine = MonitorEngine(store: store)
            var events: [IslandEvent] = []
            engine.onEvent = { events.append($0) }
            @MainActor func host(_ kind: HostKind, _ edit: (inout HostConfig) -> Void = { _ in }) -> HostConfig {
                var h = HostConfig(alias: "x", kind: kind)
                h.enabled = false
                edit(&h)
                store.settings.hosts.append(h)
                return h
            }
            @MainActor func ingest(_ h: HostConfig, _ secs: [(String, Int32?, [String])]) async {
                await engine.ingest(host: store.host(h.id) ?? h, parsed: output(secs), latency: 0.1)
            }
            let py = "4242 100 50.0 1000 python train.py"

            // 直连：进程列表出错的那一轮不报结束，下一轮读全了才报
            let d = host(.direct)
            await ingest(d, [("procs", 0, [py])])
            await ingest(d, [("procs", 1, [])])
            results.items.append((events.isEmpty && engine.snapshots[d.id]?.jobs.map(\.id) == ["4242"], "流程：进程列表出错时不报结束，列表沿用上一轮"))
            await ingest(d, [("procs", 0, [])])
            results.items.append((events.count == 1 && events.first?.kind == .finished, "流程：之后读全了、进程确实不在了才报结束"))

            // Slurm：squeue 出错的那一轮不报结束
            events = []
            let s = host(.slurm)
            await ingest(s, [("squeue", 0, ["101|relax|RUNNING|1:00:00|2:00:00|1|cn01|cpu"])])
            await ingest(s, [("squeue", 1, ["slurm_load_jobs error: Socket timed out on send/recv operation"])])
            results.items.append((events.isEmpty && engine.snapshots[s.id]?.jobs.map(\.id) == ["101"], "流程：squeue 出错时不报结束"))

            // 关闭"监控任务"、只读进度文件：没有队列 / 进程段也照常更新
            let p = host(.direct) { $0.watchJobs = false }
            await ingest(p, [("progress", 0, ["@@SI:FILE /home/u/.serverisland/progress/a.json",
                                               #"{"name":"train","progress":0.4,"message":"step 4/10"}"#])])
            results.items.append((engine.snapshots[p.id]?.jobs.filter { $0.source == .progressFile }.count == 1, "流程：关闭监控任务时进度文件照常显示"))

            // 手动监听：watch 段缺失时不报结束、不取消监听；确实不在了才报
            events = []
            let w = host(.direct) { $0.watched = [WatchedProcess(pid: "999", name: "sim")] }
            await ingest(w, [("procs", 0, []), ("watch", 0, ["999 100 1.0 100 ./sim"])])
            await ingest(w, [("procs", 0, []), ("watch", nil, [])])
            results.items.append((events.isEmpty && store.host(w.id)?.watched.count == 1, "流程：监听段缺失时不报结束、不取消监听"))
            await ingest(w, [("procs", 0, []), ("watch", 0, [])])
            results.items.append((events.count == 1 && store.host(w.id)?.watched.isEmpty == true, "流程：监听的进程确实不在了才报结束"))

            // 数组总数随运行中任务保存，重启后用它检查 accounting 的覆盖范围。
            let a = host(.slurm)
            var array = RemoteJob(id: "500", name: "scan", state: .running, source: .slurm)
            array.array = ArrayInfo(baseID: "500", total: 2)
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("si-array-test-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: dir) }
            let saved = HistoryStore(directory: dir)
            saved.setActive(a.id, [array])
            results.items.append((HistoryStore(directory: dir).activeJobs(for: a.id).first?.arrayTotal == 2,
                                  "数组历史：重启后保留已知总数"))
            array.array?.total = 3
            saved.setActive(a.id, [array])
            results.items.append((HistoryStore(directory: dir).activeJobs(for: a.id).first?.arrayTotal == 3,
                                  "数组历史：总数变化时立即保存"))

            engine.history.setActive(a.id, [RemoteJob(id: "500", name: "scan", state: .running, source: .slurm,
                                                     array: ArrayInfo(baseID: "500", total: 2))])
            let gone = engine.history.activeJobs(for: a.id)
            events = []
            for (states, expected, label) in [
                (["RUNNING"], HistoryEntry.endedUnknown, "仍运行"),
                (["SPECIAL_EXIT"], HistoryEntry.endedUnknown, "未知状态"),
                (["COMPLETED"], HistoryEntry.endedUnknown, "记录不全"),
                ([String](), HistoryEntry.endedUnknown, "没有记录"),
                (["COMPLETED", "COMPLETED"], "COMPLETED", "全部完成"),
                (["FAILED"], "FAILED", "明确失败"),
            ] {
                let finals = Parsers.sacct(states.enumerated().map { "500_\($0.offset + 1)|scan|\($0.element)|00:01:00|0:0" })
                engine.recordEndedWhileAway(host: a, gone: gone, finals: finals)
                results.items.append((engine.history.entries(for: a.id).first?.state == expected && events.isEmpty,
                                      "数组补记：\(label)时状态正确且不弹提醒"))
            }
            // 老版本 active-jobs.json 没有 arrayTotal，仍能读取，但不能据此确认数组整体成功。
            do {
                let legacy = Data(#"{"id":"500","name":"scan","slurm":true,"seenAt":"2026-01-01T00:00:00Z"}"#.utf8)
                var old = try JSONDecoder.iso.decode(HistoryStore.ActiveJob.self, from: legacy)
                old.seenAt = Date()
                engine.recordEndedWhileAway(host: a, gone: [old],
                                            finals: Parsers.sacct(["500_1|scan|COMPLETED|00:01:00|0:0"]))
                results.items.append((old.arrayTotal == nil && engine.history.entries(for: a.id).first?.state == HistoryEntry.endedUnknown,
                                      "数组补记：旧记录没有总数时保守记为结果未知"))
            } catch { results.items.append((false, "数组补记：旧记录兼容失败：\(error)")) }

            // 关注：只提醒关注的任务时，没关注的任务结束不提醒；关注的任务结束后自动取消关注
            events = []
            let f = host(.direct)
            let two = ["4242 100 50.0 1000 python train.py", "4343 100 50.0 1000 python eval.py"]
            await ingest(f, [("procs", 0, two)])
            if let j = engine.snapshots[f.id]?.jobs.first(where: { $0.id == "4242" }) { engine.toggleStar(hostID: f.id, job: j) }
            store.settings.alertScope = .starred
            await ingest(f, [("procs", 0, [])])
            results.items.append((events.count == 1 && events.first?.jobID == "4242", "关注：只提醒关注的任务时，没关注的任务结束不提醒"))
            results.items.append((!store.settings.starredJobs.contains { $0.hostID == f.id }, "关注：任务结束后自动取消关注"))
            // 列表没读全时不取消关注
            await ingest(f, [("procs", 0, two)])
            if let j = engine.snapshots[f.id]?.jobs.first(where: { $0.id == "4343" }) { engine.toggleStar(hostID: f.id, job: j) }
            await ingest(f, [("procs", 1, [])])
            results.items.append((engine.isStarred(f.id, "4343"), "关注：进程列表出错时不取消关注"))
            // 种类开关：关掉“完成”后，关注的任务结束也不提醒
            events = []
            store.settings.alertFinished = false
            await ingest(f, [("procs", 0, [])])
            results.items.append((events.isEmpty, "提醒设置：关掉“完成”后不提醒结束"))
            store.settings.alertFinished = true
            store.settings.alertScope = .all
            // 数组：关注的是整个数组（主编号）
            let sa = host(.slurm)
            await ingest(sa, [("squeue", 0, ["700_1|scan|RUNNING|0:10|1:00:00|1|cn01|cpu|700|1", "700_[2-5]|scan|PENDING|0:00|1:00:00|1|(Priority)|cpu|700|[2-5]"])])
            if let g = engine.snapshots[sa.id]?.jobs.first { engine.toggleStar(hostID: sa.id, job: g) }
            results.items.append((store.settings.starredJobs.last?.jobID == "700" && engine.isStarred(sa.id, "700"), "关注：数组作业按主编号关注"))
            sem.signal()
        }
        while sem.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
        return results.items
    }
}
