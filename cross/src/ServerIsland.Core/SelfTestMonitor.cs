using System.Text.Json.Nodes;
namespace ServerIsland.Core;

/// 自检：异常情况下不误报（队列查询失败、结果未知、通知合并）、配置文件损坏时不被覆盖（和 Mac 版 SelfTestMonitor.swift 对应）。
public static class SelfTestMonitor
{
    public static void Run(Action<bool, string> check)
    {
        // —— 队列查询失败：沿用上一份任务列表，不当成任务结束 ——
        var host = new HostConfig("x", kind: HostKind.Slurm);
        List<RemoteJob> before = [new RemoteJob("101", "relax", JobState.Running, JobSource.Slurm)];
        bool Listed(int rc, params string[] lines) =>
            MonitorEngine.JobsListed(host, RemoteScript.Parse(string.Join("\n", new[] { "@@SI:BEGIN", "@@SI:SEC squeue" }.Concat(lines).Append($"@@SI:RC {rc}"))));
        var failed = Listed(1, "slurm_load_jobs error: Socket timed out on send/recv operation");
        check(!failed && MonitorEngine.JobsAfterPoll([], before, failed).Select(j => j.Id).SequenceEqual(["101"]),
              "squeue 出错：沿用上一份任务列表，不产生结束提醒");
        var empty = Listed(0);
        check(empty && MonitorEngine.JobsAfterPoll([], before, empty).Count == 0, "squeue 成功且为空：任务确实离开了队列");

        // —— 离开队列的 Slurm 作业：只有 COMPLETED 才算成功 ——
        (string, EventKind) Outcome(string? raw) =>
            MonitorEngine.SlurmOutcome(raw == null ? null : new SacctFinal(JobStates.FromSlurm(raw), raw, null, ""));
        check(Outcome(null) == (HistoryEntry.EndedUnknown, EventKind.Info), "没有 sacct：历史记成“已结束”，提醒为结果未知");
        check(Outcome("RUNNING") == (HistoryEntry.EndedUnknown, EventKind.Info) && Outcome("COMPLETING") == (HistoryEntry.EndedUnknown, EventKind.Info),
              "sacct 仍显示运行中：不当成完成");
        check(Outcome("COMPLETED") == ("COMPLETED", EventKind.Finished) && Outcome("OUT_OF_MEMORY") == ("OUT_OF_MEMORY", EventKind.Failed)
              && Outcome("CANCELLED by 123") == ("CANCELLED by 123", EventKind.Failed) && Outcome("SPECIAL_EXIT") == ("SPECIAL_EXIT", EventKind.Info),
              "sacct 结束状态：完成 / 失败 / 不认识的状态");

        // —— 数组整体结束：sacct 里还有运行中的元素时不算成功 ——
        var arr = MonitorEngine.ArrayCounts(Parsers.Sacct(["500_1|scan|COMPLETED|00:10:00|0:0", "500_2|scan|RUNNING|00:09:00|0:0"]).Values.Select(f => f.State));
        check(arr == (1, 0, 1) && MonitorEngine.ArrayOutcome(arr.Failed, arr.Unknown == 0) == (HistoryEntry.EndedUnknown, EventKind.Info),
              "数组：还有元素显示运行中时，记成结果未知");
        check(MonitorEngine.ArrayOutcome(0, true) == ("COMPLETED", EventKind.Finished) && MonitorEngine.ArrayOutcome(1, false) == ("FAILED", EventKind.Failed),
              "数组：全部确认完成才算成功，有失败就算失败");
        var partial = MonitorEngine.ArrayCounts([JobState.Completed], 2);
        check(partial == (1, 0, 1) && MonitorEngine.ArrayOutcome(partial.Failed, partial.Unknown == 0) == (HistoryEntry.EndedUnknown, EventKind.Info),
              "数组：已知 2 个元素、只返回 1 个完成记录时不报成功");
        var complete = MonitorEngine.ArrayCounts([JobState.Completed, JobState.Completed], 2);
        check(complete == (2, 0, 0) && MonitorEngine.ArrayOutcome(complete.Failed, complete.Unknown == 0) == ("COMPLETED", EventKind.Finished),
              "数组：返回全部元素的完成记录才报成功");

        // —— 完整的处理流程（Ingest：解析 → 任务列表 → 事件），用样例输出走几轮 ——
        var lang = Lang.Setting;   // 新建的 ConfigStore 会把语言设成默认值，测完改回来
        IngestScenarios(check).GetAwaiter().GetResult();
        Lang.Setting = lang;

        // —— 多条通知合并：按种类计数 ——
        List<IslandEvent> Events(params EventKind[] kinds) => kinds.Select((k, i) => new IslandEvent(k, $"t{i}", "", "h")).ToList();
        var unknown = MonitorEngine.Merged(Events(EventKind.Info, EventKind.Info, EventKind.Info), "h", null);
        check(unknown.Kind == EventKind.Info && unknown.Title == "3 个结果未知", "合并：3 条结果未知不会变成“开始运行”");
        var mixed = MonitorEngine.Merged(Events(EventKind.Started, EventKind.Started, EventKind.Warning), "h", null);
        check(mixed.Kind == EventKind.Warning && mixed.Title == "2 个开始运行，1 条警告", "合并：开始运行和磁盘警告分开计数");
        var all = MonitorEngine.Merged(Events(EventKind.Finished, EventKind.Failed, EventKind.Info, EventKind.Started), "h", null);
        check(all.Kind == EventKind.Failed && all.Title == "1 个完成，1 个失败，1 个结果未知，1 个开始运行", "合并：有失败时按失败提醒");

        // —— 关注与提醒设置 ——
        var hid = Guid.NewGuid();
        var st = new AppSettings { StarredJobs = [new StarredJob { HostID = hid, JobID = "101", Name = "relax" }] };
        IslandEvent Ev(EventKind k, string? job) => new(k, "t", "", "h", hid, job);
        check(st.AllowsAlert(Ev(EventKind.Finished, "102")) && st.AllowsAlert(Ev(EventKind.Started, "101")), "提醒设置：默认提醒全部任务");
        st.AlertScope = AlertScope.Starred;
        check(st.AllowsAlert(Ev(EventKind.Finished, "101")) && !st.AllowsAlert(Ev(EventKind.Finished, "102")) && !st.AllowsAlert(Ev(EventKind.Info, "102")),
              "提醒设置：只提醒关注的任务");
        check(st.AllowsAlert(Ev(EventKind.Warning, null)), "提醒设置：磁盘等服务器提醒不受关注范围影响");
        st.AlertFailed = false; st.AlertUnknown = false; st.AlertStarted = false;
        check(!st.AllowsAlert(Ev(EventKind.Failed, "101")) && !st.AllowsAlert(Ev(EventKind.Info, "101")) && !st.AllowsAlert(Ev(EventKind.Started, "101"))
              && st.AllowsAlert(Ev(EventKind.Finished, "101")), "提醒设置：开始 / 失败 / 结果未知分别开关");
        check(MonitorEngine.Alertable([Ev(EventKind.Finished, "4242"), Ev(EventKind.Finished, "4343")], st, ["4242"]).Select(e => e.JobID).SequenceEqual(["4242"]),
              "提醒设置：手动监听的进程在“只提醒关注的任务”时照常提醒");
        var three = new[] { "1", "2", "3" }.Select(x => new RemoteJob { Id = x, Name = x, State = JobState.Running, Source = JobSource.Slurm });
        check(MonitorEngine.StarredFirst(three, x => x.Id == "3").Select(x => x.Id).SequenceEqual(["3", "1", "2"]), "关注：关注的任务排在前面，其余顺序不变");
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(st, Json.Options), Json.Options)!;
        check(back.StarredJobs.Count == 1 && back.StarredJobs[0].HostID == hid && back.StarredJobs[0].JobID == "101"
              && back.AlertScope == AlertScope.Starred && !back.AlertFailed && back.AlertFinished, "配置：关注和提醒设置可以保存、读回");
        var oldCfg = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}", Json.Options)!;
        check(oldCfg.StarredJobs.Count == 0 && oldCfg.AlertScope == AlertScope.All && oldCfg.AlertStarted && oldCfg.AlertFinished && oldCfg.AlertFailed && oldCfg.AlertUnknown,
              "配置：旧配置文件默认提醒全部任务的全部事件");
        // Mac 版写的配置（键名、枚举值、大写 UUID、距 2001 年的秒数）能直接读
        var mac = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"alertScope":"starred","alertStarted":false,"starredJobs":[{"hostID":"0A1B2C3D-0000-4000-8000-000000000001","jobID":"8812101","name":"relax","since":782700000}]}""", Json.Options)!;
        check(mac.AlertScope == AlertScope.Starred && !mac.AlertStarted && mac.StarredJobs.Single().JobID == "8812101"
              && mac.StarredJobs[0].HostID == Guid.Parse("0A1B2C3D-0000-4000-8000-000000000001") && mac.StarredJobs[0].Since?.Year == 2025,
              "配置：读取 Mac 版写的关注列表");

        // —— 勿扰时段 ——
        static DateTime At(int h, int m) => new(2026, 1, 1, h, m, 0);
        var q = new AppSettings();
        check(!q.IsQuiet(At(23, 30)), "勿扰：默认关闭");
        q.QuietHours = true;
        check(q.IsQuiet(At(23, 0)) && q.IsQuiet(At(2, 0)) && q.IsQuiet(At(7, 59)) && !q.IsQuiet(At(8, 0)) && !q.IsQuiet(At(22, 59)), "勿扰：跨午夜 23:00–08:00");
        q.QuietStart = 12 * 60; q.QuietEnd = 14 * 60;
        check(q.IsQuiet(At(13, 0)) && !q.IsQuiet(At(14, 0)) && !q.IsQuiet(At(11, 59)), "勿扰：白天 12:00–14:00");
        q.QuietEnd = 12 * 60;
        check(!q.IsQuiet(At(12, 0)), "勿扰：起止相同时不算勿扰");
        check(q.QuietLabel == "12:00–12:00" && AppSettings.Clock(23 * 60 + 30) == "23:30", "勿扰：时间文字");

        // —— 设置的导入导出 ——
        var a1 = new HostConfig("a1", "A");
        var cur = new AppSettings { Hosts = [a1], IslandScreen = "DELL U2720Q", DisplayMode = DisplayMode.Pet };
        var jump = new HostConfig("jump");
        var b1 = new HostConfig("b1", null, HostKind.Slurm) { JumpHostID = jump.Id };
        var orphan = new HostConfig("c1") { JumpHostID = Guid.NewGuid() };
        var dup = new HostConfig("a1", "A copy");
        var imp = new AppSettings { Hosts = [jump, b1, orphan, dup], DefaultInterval = 300,
            StarredJobs = [new StarredJob { HostID = b1.Id, JobID = "1" }, new StarredJob { HostID = Guid.NewGuid(), JobID = "2" }] };
        var readBack = ConfigTransfer.Read(ConfigTransfer.ExportJson(imp));
        check(readBack.Hosts.Count == 4 && readBack.DefaultInterval == 300, "导入导出：导出的文件可以读回");
        bool Rejects(string json) { try { ConfigTransfer.Read(json); return false; } catch { return true; } }
        check(Rejects("""{"name":"x"}""") && Rejects("not json"), "导入导出：不是设置文件时拒绝");
        var replaced = ConfigTransfer.Apply(cur, imp, ConfigTransfer.Mode.Replace);
        check(replaced.Hosts.Count == 4 && replaced.DefaultInterval == 300 && replaced.IslandScreen == "DELL U2720Q" && replaced.DisplayMode == DisplayMode.Pet,
              "导入导出：替换全部设置，保留本机的显示器和显示方式");
        check(replaced.Hosts.First(h => h.Alias == "b1").JumpHostID == jump.Id && replaced.Hosts.First(h => h.Alias == "c1").JumpHostID == null
              && replaced.StarredJobs.Select(x => x.JobID).SequenceEqual(["1"]), "导入导出：去掉指向不存在服务器的跳板机和关注");
        var added = ConfigTransfer.Apply(cur, imp, ConfigTransfer.Mode.AddHosts);
        check(added.Hosts.Select(h => h.Alias).SequenceEqual(["a1", "jump", "b1", "c1"]) && added.DefaultInterval == cur.DefaultInterval,
              "导入导出：只添加新的服务器（连接目标相同的跳过），其它设置不动");
        // Mac 版导出的文件（大写 UUID、日期是距 2001 年的秒数、没有跨平台版专有的字段）
        var fromMac = ConfigTransfer.Read("""{"hosts":[{"alias":"hpc","id":"0A1B2C3D-0000-4000-8000-000000000001","kind":"slurm"}],"quietHours":true,"quietStart":1320,"quietEnd":420,"starredJobs":[{"hostID":"0A1B2C3D-0000-4000-8000-000000000001","jobID":"9","since":782700000}]}""");
        check(fromMac.Hosts.Single().Kind == HostKind.Slurm && fromMac.QuietHours && fromMac.QuietLabel == "22:00–07:00" && fromMac.StarredJobs.Single().JobID == "9",
              "导入导出：读取 Mac 版导出的设置");

        // —— 配置文件：首次启动 / 正常 / 损坏（先备份） ——
        var dir = Path.Combine(Path.GetTempPath(), "si-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "config.json");
            var missing = ConfigStore.Load(file);
            check(missing.Settings == null && missing.Problem == null, "配置：首次启动没有文件，不算出错");
            File.WriteAllText(file, "{}");
            check(ConfigStore.Load(file).Settings != null, "配置：缺字段时补默认值");
            const string broken = "{\"hosts\": [ {\"alias\": \"a\"";
            File.WriteAllText(file, broken);
            var bad = ConfigStore.Load(file);
            var backups = Directory.GetFiles(dir, "config.broken-*");
            check(bad.Settings == null && bad.Problem != null && !bad.BlockSave && backups.Length == 1 && File.ReadAllText(backups[0]) == broken,
                  "配置：读取失败时先备份原文件并说明原因");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Agents(check);
        DisplayMessages(check);
    }

    /// Agent 钩子和会话状态（和 Mac 版 SelfTestMonitor.swift 里同名的检查一一对应）。
    static void Agents(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "si-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // —— Agent 钩子：只追加 / 删除我们自己的那几条，用户原有的钩子和设置不动 ——
            var hookFile = Path.Combine(dir, "settings.json");
            const string original = """{"theme":"dark","hooks":{"Stop":[{"hooks":[{"type":"command","command":"python3 other.py"}]}],"PreToolUse":[{"matcher":"*","hooks":[{"type":"command","command":"x"}]}]}}""";
            File.WriteAllText(hookFile, original);
            var installErr = AgentHooks.SetClaude(true, hookFile, "/Apps/SI");
            var installed = JsonNode.Parse(File.ReadAllText(hookFile)) as JsonObject;
            var hookMap = installed?["hooks"] as JsonObject;
            check(installErr == null && AgentHooks.ClaudeInstalled(hookFile) && (hookMap?["Stop"] as JsonArray)?.Count == 2
                  && (hookMap?["PreToolUse"] as JsonArray)?.Count == 2 && installed?["theme"]?.GetValue<string>() == "dark",
                  "Agent 钩子：安装时保留原有的钩子和设置");
            AgentHooks.SetClaude(false, hookFile);
            check(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(hookFile)), JsonNode.Parse(original)) && !AgentHooks.ClaudeInstalled(hookFile),
                  "Agent 钩子：卸载后和原来一样");
            File.WriteAllText(hookFile, """{"hooks":{"Stop":[{"matcher":"*","hooks":[{"type":"command","command":"/Apps/SI --agent-event claude"},{"type":"command","command":"echo keep-me"}]}]}}""");
            AgentHooks.SetClaude(true, hookFile, "/Apps/SI");
            AgentHooks.SetClaude(false, hookFile);
            var kept = File.ReadAllText(hookFile);
            check(kept.Contains("keep-me") && kept.Contains("matcher") && !kept.Contains("--agent-event"), "Agent 钩子：同组的其他命令和匹配条件不删除");

            // 用临时会话重现中间消息、错轮次和内部辅助会话导致的完成误报。
            var monitor = new AgentMonitor();
            var alerts = 0;
            monitor.OnAlert = _ => alerts++;
            var root = Path.Combine(dir, "root.jsonl");
            var child = Path.Combine(dir, "child.jsonl");
            void Append(string file, string type, JsonObject payload, DateTime? at = null)
            {
                var obj = new JsonObject
                {
                    ["type"] = type, ["timestamp"] = (at ?? DateTime.Now).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["payload"] = payload,
                };
                File.AppendAllText(file, obj.ToJsonString() + "\n");
            }
            AgentState? First() => monitor.List.FirstOrDefault()?.State;
            Append(root, "session_meta", new() { ["source"] = "vscode", ["cwd"] = "/demo" });
            Append(root, "event_msg", new() { ["type"] = "task_started", ["turn_id"] = "one" });
            monitor.ReadCodex(root, false);
            Append(root, "response_item", new() { ["role"] = "assistant", ["channel"] = "commentary" });
            Append(root, "event_msg", new() { ["type"] = "task_complete", ["turn_id"] = "old" });
            monitor.ReadCodex(root, true);
            check(alerts == 0 && First() == AgentState.Working, "Codex：中间消息和其它轮次完成不误报");
            Append(root, "event_msg", new() { ["type"] = "task_complete", ["turn_id"] = "one" });
            monitor.ReadCodex(root, true);
            monitor.ReadCodex(root, true);
            check(alerts == 1 && First() == AgentState.Done, "Codex：当前轮次完成只提醒一次");
            Append(root, "event_msg", new() { ["type"] = "task_started", ["turn_id"] = "two" });
            Append(root, "event_msg", new() { ["type"] = "turn_aborted", ["turn_id"] = "two" });
            monitor.ReadCodex(root, true);
            check(alerts == 1, "Codex：中断不报回答完毕");
            Append(child, "session_meta", new() { ["source"] = new JsonObject { ["subagent"] = new JsonObject { ["other"] = "guardian" } }, ["parent_thread_id"] = "parent" });
            Append(child, "response_item", new() { ["text"] = new string('x', 450_000) });
            Append(child, "event_msg", new() { ["type"] = "task_started", ["turn_id"] = "child" });
            Append(child, "event_msg", new() { ["type"] = "task_complete", ["turn_id"] = "child" });
            monitor.ReadCodex(child, true);
            check(alerts == 1 && monitor.Sessions.Count == 1, "Codex：从长文件尾部读取也过滤内部辅助会话");
            Append(root, "event_msg", new() { ["type"] = "task_started", ["turn_id"] = "past" }, DateTime.Now.AddSeconds(-3600));
            Append(root, "event_msg", new() { ["type"] = "task_complete", ["turn_id"] = "past" }, DateTime.Now.AddSeconds(-3500));
            monitor.ReadCodex(root, true);
            check(alerts == 1, "Codex：不补报历史完成事件");

            Append(root, "event_msg", new() { ["type"] = "task_started", ["turn_id"] = "questions" });
            const string args = """{"questions":[{"title":"Pick a mode"},{"title":"Pick a duration"}]}""";
            Append(root, "response_item", new() { ["type"] = "function_call", ["name"] = "request_user_input_async", ["call_id"] = "ask", ["arguments"] = args });
            Append(root, "response_item", new() { ["type"] = "function_call_output", ["call_id"] = "ask", ["output"] = """{"accepted":true}""" });
            monitor.ReadCodex(root, true);
            check(alerts == 2 && First() == AgentState.Attention, "Codex：异步提问会提醒，受理回执不清除等待状态");
            void Answer(int index)
            {
                var id = new JsonArray("request_user_input_async", "ask", index).ToJsonString();
                var replies = new JsonArray(new JsonObject { ["questionItemId"] = id, ["answer"] = "A" }).ToJsonString();
                Append(root, "response_item", new()
                {
                    ["type"] = "message", ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = $"<send_user_message_question_reply>\n{replies}\n</send_user_message_question_reply>" }),
                });
                monitor.ReadCodex(root, true);
            }
            Answer(0);
            check(First() == AgentState.Attention && alerts == 2, "Codex：部分答复仍保留其余待答问题");
            Answer(1);
            check(First() == AgentState.Working && alerts == 2, "Codex：全部答复后恢复工作，不报回答完毕");
            Append(root, "response_item", new() { ["type"] = "function_call", ["name"] = "functions.request_user_input", ["call_id"] = "sync", ["arguments"] = args });
            monitor.ReadCodex(root, true);
            Append(root, "response_item", new() { ["type"] = "function_call_output", ["call_id"] = "sync", ["output"] = """{"answers":{}}""" });
            monitor.ReadCodex(root, true);
            check(alerts == 3 && First() == AgentState.Working, "Codex：同步提问和返回结果配对，不重复提醒");

            var tools = new AgentMonitor();
            var needsInput = 0;
            tools.OnAlert = s => { if (s.State == AgentState.Attention) needsInput++; };
            JsonObject Ev(string tool, string session, string ev, params (string K, JsonNode V)[] extra)
            {
                var o = new JsonObject { ["tool"] = tool, ["session"] = session, ["event"] = ev };
                foreach (var (k, v) in extra) o[k] = v;
                return o;
            }
            tools.Apply(Ev("claude", "c", "PreToolUse", ("toolName", "AskUserQuestion")), true);
            check(tools.List.FirstOrDefault()?.State == AgentState.Attention && needsInput == 1, "Claude：AskUserQuestion 进入等待回答并提醒");
            tools.Apply(Ev("claude", "c", "PostToolUse", ("toolName", "AskUserQuestion")), true);
            tools.Apply(Ev("claude", "c", "Notification", ("kind", "elicitation_response")), true);
            check(tools.List.FirstOrDefault()?.State == AgentState.Working && needsInput == 1, "Claude：问题答复或 MCP 答复后恢复，不再次报需要确认");
            var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 10;
            tools.Apply(Ev("claude", "late", "PermissionRequest", ("toolName", "Bash"), ("at", t0)), true);
            tools.Apply(Ev("claude", "late", "PostToolUse", ("toolName", "Bash"), ("at", t0 + 3)), true);
            tools.Apply(Ev("claude", "late", "Notification", ("kind", "permission_prompt"), ("at", t0 + 1)), true);
            check(tools.Sessions.GetValueOrDefault("claude|late")?.State == AgentState.Working && needsInput == 2,
                  "Claude：晚到的 permission_prompt 通知不会把已批准的会话改回需要确认");
            tools.Apply(Ev("opencode", "o", "question.asked", ("message", "Choose a mode")), true);
            check(needsInput == 3, "OpenCode：question.asked 会提醒");
            tools.Apply(Ev("opencode", "o", "question.replied"), true);
            tools.Apply(Ev("opencode", "o", "permission.asked"), true);
            tools.Apply(Ev("opencode", "o", "permission.replied"), true);
            check(needsInput == 4 && tools.Sessions.GetValueOrDefault("opencode|o")?.State == AgentState.Working, "OpenCode：问题及权限答复均解除等待");
            tools.Apply(Ev("opencode", "o", "session.status", ("status", "idle")), false);
            check(tools.Sessions.GetValueOrDefault("opencode|o")?.State == AgentState.Done, "OpenCode：idle 状态不会误判为工作中");

            tools.Apply(Ev("claude", "late", "Stop", ("at", t0 + 5)), false);
            tools.Apply(Ev("claude", "late", "PostToolUse", ("at", t0 + 4), ("plan", new JsonObject { ["set"] = new JsonArray(new JsonArray("新清单", "completed")) })), false);
            tools.Apply(Ev("claude", "late", "PostToolUse", ("at", t0 + 2), ("plan", new JsonObject { ["set"] = new JsonArray(new JsonArray("旧清单", "pending")) })), false);
            var late = tools.Sessions["claude|late"];
            check(late.State == AgentState.Done && late.Plan.Done == 1, "Agent 清单：晚到的清单不改变完成状态，旧清单不覆盖新清单");

            var events = Path.Combine(dir, "events.jsonl");
            var codexDir = Path.Combine(dir, "sessions", "2000", "01", "01");
            Directory.CreateDirectory(codexDir);
            var reader = new AgentMonitor(events, Path.Combine(dir, "sessions"));
            var replayAlerts = 0;
            reader.OnAlert = _ => replayAlerts++;
            var baseAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - 10;
            void Hook(string ev, double at, JsonObject? plan = null)
            {
                var o = Ev("claude", "reconnect", ev, ("at", at));
                if (plan != null) o["plan"] = plan;
                File.AppendAllText(events, o.ToJsonString() + "\n");
            }
            Hook("UserPromptSubmit", baseAt);
            Hook("PostToolUse", baseAt + 1, new JsonObject { ["add"] = "A" });
            Hook("PostToolUse", baseAt + 2, new JsonObject { ["add"] = "B" });
            reader.LoadInitial();
            reader.LoadInitial();
            check(reader.Sessions["claude|reconnect"].Plan.Total == 2, "Agent 重连：增量清单不重复回放");
            Hook("Stop", baseAt + 3);
            reader.LoadInitial();
            check(reader.Sessions["claude|reconnect"].State == AgentState.Done && replayAlerts == 0, "Agent 重连：断开期间的完成不补发提醒");
            var old = Path.Combine(codexDir, "old.jsonl");
            Append(old, "session_meta", new JsonObject { ["source"] = "cli", ["cwd"] = "/demo" });
            Append(old, "event_msg", new JsonObject { ["type"] = "task_started", ["turn_id"] = "old-turn" });
            var discovery = new AgentMonitor(events, Path.Combine(dir, "sessions"));
            discovery.LoadInitial();
            check(discovery.Sessions.GetValueOrDefault("codex|old")?.State == AgentState.Working, "Codex：旧日期目录中的活跃会话仍能发现");
            var longFile = Path.Combine(dir, "long.jsonl");
            var longReader = new AgentMonitor(Path.Combine(dir, "none"), Path.Combine(dir, "empty"));
            var longAlerts = 0;
            longReader.OnAlert = _ => longAlerts++;
            Append(longFile, "session_meta", new JsonObject { ["source"] = "cli", ["cwd"] = "/demo" }, DateTime.Now.AddSeconds(-1900));
            Append(longFile, "event_msg", new JsonObject { ["type"] = "task_started", ["turn_id"] = "long" }, DateTime.Now.AddSeconds(-1900));
            longReader.ReadCodex(longFile, false);
            longReader.Poll();
            var retained = longReader.List.FirstOrDefault()?.State == AgentState.Working;
            Append(longFile, "event_msg", new JsonObject { ["type"] = "task_complete", ["turn_id"] = "long" });
            longReader.ReadCodex(longFile, true);
            check(retained && longAlerts == 1, "Codex：超过 30 分钟的轮次仍保留并提醒完成");
            longReader.Apply(Ev("claude", "active", "UserPromptSubmit", ("at", baseAt - 1900)), false);
            longReader.Apply(Ev("claude", "active", "PostToolUse"), false);
            longReader.Poll();
            check(longReader.Sessions.GetValueOrDefault("claude|active")?.State == AgentState.Working, "Agent：按最近活动而不是状态起始时间清理");
            var hosts = new JsonArray();
            for (var i = 0; i < 3; i++)
            {
                var jobs = new JsonArray();
                for (var j = 0; j < 8; j++) jobs.Add(new JsonObject { ["id"] = j.ToString(), ["name"] = new string('任', 80), ["star"] = j == 0,
                    ["detail"] = new string('节', 120), ["dir"] = new string('路', 120), ["msg"] = new string('进', 120) });
                hosts.Add(new JsonObject { ["name"] = $"demo-{i}", ["njobs"] = 8, ["jobs"] = jobs });
            }
            var fit = DisplayLink.FitSnapshot(new JsonObject { ["t"] = "snap", ["hosts"] = hosts }, 1500);
            var fitHosts = (JsonArray)fit["hosts"]!;
            var fitJobs = fitHosts.OfType<JsonObject>().SelectMany(h => (JsonArray)h["jobs"]!).OfType<JsonObject>();
            var wire = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            check(System.Text.Encoding.UTF8.GetByteCount(fit.ToJsonString(wire)) + 1 <= 1500 && fitHosts.Count == 3 && fitJobs.Count(j => j["star"]?.GetValue<bool>() == true) == 3,
                  "副屏：UTF-8 总字节受限，优先保留关注任务和服务器");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// 发给副屏的消息（跨平台版特有：协议字段和 Mac 版一致）。
    static void DisplayMessages(Action<bool, string> check)
    {
        var settings = new AppSettings();
        settings.Deadlines.Add(new Deadline { Title = new string('论', 40), Date = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Local) });
        settings.Deadlines.Add(new Deadline { Title = "早", Date = new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Local) });
        var cal = DisplayExtras.CalendarMessage(settings);
        var dl = cal["deadlines"] as JsonArray;
        check(cal["t"]?.GetValue<string>() == "cal" && dl?.Count == 2 && dl[0]?["title"]?.GetValue<string>() == "早"
              && dl[1]?["title"]?.GetValue<string>()?.Length == 30 && dl[1]?["at"]?.GetValue<long>() == new DateTimeOffset(settings.Deadlines[0].Date!.Value).ToUnixTimeSeconds(),
              "副屏：截止日期按时间排序、标题截到 30 字、时间是 UTC 秒");
        var json = System.Text.Json.JsonSerializer.Serialize(settings, Json.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, Json.Options)!;
        var fromMac = ConfigTransfer.Read("""{"hosts":[],"usbDisplay":true,"displayLockSync":false,"weatherCity":"北京","deadlines":[{"id":"0A1B2C3D-0000-4000-8000-000000000002","title":"投稿","date":915148800}]}""");
        check(back.Deadlines.Count == 2 && json.Contains("\"deadlines\"") && fromMac.UsbDisplay && !fromMac.DisplayLockSync && fromMac.WeatherCity == "北京"
              && fromMac.Deadlines.Single().Title == "投稿" && fromMac.Deadlines.Single().Date?.ToUniversalTime().Year == 2030,
              "副屏：设置项和 Mac 版的配置文件互通");
        check(DisplayExtras.Describe(0) != "-" && DisplayExtras.Describe(63) == DisplayExtras.Describe(81) && DisplayExtras.Describe(1000) == "-",
              "副屏：WMO 天气代码");
        {
            var times = Enumerable.Range(8, 13).Select(h => $"2026-11-01T{h:00}:00").ToList();
            var codes = Enumerable.Repeat(1, times.Count).ToList();
            var probs = Enumerable.Repeat(10, times.Count).ToList();
            codes[1] = 61; probs[6] = 70; probs[8] = 80;  // 9 点下过雨（已过去），14、16 点可能有雨
            var rain = DisplayExtras.RainAhead(times, codes, probs, "2026-11-01T11:20");
            check(rain is { From: 14, Prob: 80 } && DisplayExtras.RainAhead(times, codes, probs, "2026-11-01T17:05") == null,
                  "副屏：今天接下来的下雨时间");
        }
        {
            // Agent 任务清单：整张清单、桌面 App 的增量操作、TaskCreate / TaskUpdate、Codex 的 plan。
            static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;
            var plan = AgentPlan.Empty.Apply(AgentPlan.Op("mcp__ccd_turn__update_plan", J("""{"items":[{"content":"读代码","status":"in_progress"},{"content":"改界面","status":"pending"},{"content":"截图","status":"pending"}]}"""))!);
            plan = plan.Apply(AgentPlan.Op("mcp__ccd_turn__update_plan", J("""{"advance":true}"""))!);
            check(plan.Done == 1 && plan.Total == 3 && plan.Now == "改界面", "Agent 清单：桌面 App 的 advance");
            plan = plan.Apply(AgentPlan.Op("mcp__ccd_turn__update_plan", J("""{"done":[2],"start":3}"""))!);
            check(plan.Done == 2 && plan.Now == "截图", "Agent 清单：done / start");
            var tasks = AgentPlan.Empty;
            foreach (var t in new[] { "A", "B" }) tasks = tasks.Apply(AgentPlan.Op("TaskCreate", new JsonObject { ["subject"] = t })!);
            tasks = tasks.Apply(AgentPlan.Op("TaskUpdate", J("""{"taskId":"1","status":"completed"}"""))!);
            tasks = tasks.Apply(AgentPlan.Op("TaskUpdate", J("""{"taskId":"2","status":"deleted"}"""))!);
            check(tasks.Done == 1 && tasks.Total == 1, "Agent 清单：TaskCreate / TaskUpdate");
            var todo = AgentPlan.Empty.Apply(AgentPlan.Op("TodoWrite", J("""{"todos":[{"content":"x","status":"completed"},{"content":"y","status":"in_progress"}]}"""))!);
            var codexPlan = AgentPlan.Empty.Apply(AgentPlan.Op("update_plan", J("""{"plan":[{"step":"s1","status":"completed"},{"step":"s2","status":"pending"}]}"""))!);
            check(todo.Now == "y" && codexPlan.Done == 1 && codexPlan.Now == "s2" && AgentPlan.Op("Bash", []) == null,
                  "Agent 清单：TodoWrite、Codex plan，其他工具不算");

            // Claude 的 5 小时窗口（用 CC Switch 的调用记录估算）。
            var b = DateTimeOffset.FromUnixTimeSeconds(1_793_491_200).UtcDateTime;  // 整点
            var w = AIUsage.Window([(b.AddHours(0.2), 10), (b.AddHours(1), 5), (b.AddHours(6), 7), (b.AddHours(7), 3)], b.AddHours(8));
            check(w is { Tokens: 10 } && w.Value.Reset == b.AddHours(11) && AIUsage.Window([(b, 1)], b.AddHours(5)) == null, "AI 用量：Claude 的 5 小时窗口");
        }
        var hook = AgentHooks.HookCommand("/opt/SI");
        check(hook.Contains("--agent-event claude") && (OperatingSystem.IsWindows() ? hook.StartsWith("\"") : hook.StartsWith("'")),
              "Agent 钩子：命令按系统加引号");
    }

    /// 一段样例输出：每段 (名字, 退出码, 行)；退出码为 null 时这段整个缺失（输出被截断等）。
    static RemoteScript.Parsed Output(params (string Name, int? Rc, string[] Lines)[] secs)
    {
        var lines = new List<string> { "@@SI:BEGIN" };
        foreach (var (name, rc, body) in secs)
        {
            if (rc == null) continue;
            lines.Add($"@@SI:SEC {name}");
            lines.AddRange(body);
            lines.Add($"@@SI:RC {rc}");
        }
        lines.Add("@@SI:END");
        return RemoteScript.Parse(string.Join("\n", lines));
    }

    /// 用一个不连网的引擎（主机设为停用，不会真的轮询）走几轮 Ingest。
    static async Task IngestScenarios(Action<bool, string> check)
    {
        var store = new ConfigStore(persist: false);
        store.Settings.SoundEnabled = false;
        store.Settings.IslandAlerts = false;
        var engine = new MonitorEngine(store, history: new HistoryStore(persist: false));
        var events = new List<IslandEvent>();
        engine.EventEmitted += events.Add;
        HostConfig Host(HostKind kind, Action<HostConfig>? edit = null)
        {
            var h = new HostConfig("x", kind: kind) { Enabled = false };
            edit?.Invoke(h);
            store.Settings.Hosts.Add(h);
            return h;
        }
        Task Ingest(HostConfig h, params (string, int?, string[])[] secs) => engine.Ingest(store.Host(h.Id) ?? h, Output(secs), 0.1);
        const string py = "4242 100 50.0 1000 python train.py";

        // 直连：进程列表出错的那一轮不报结束，下一轮读全了才报
        var d = Host(HostKind.Direct);
        await Ingest(d, ("procs", 0, [py]));
        await Ingest(d, ("procs", 1, []));
        check(events.Count == 0 && engine.Snapshots[d.Id].Jobs.Select(j => j.Id).SequenceEqual(["4242"]), "流程：进程列表出错时不报结束，列表沿用上一轮");
        await Ingest(d, ("procs", 0, []));
        check(events.Count == 1 && events[0].Kind == EventKind.Finished, "流程：之后读全了、进程确实不在了才报结束");

        // Slurm：squeue 出错的那一轮不报结束
        events.Clear();
        var s = Host(HostKind.Slurm);
        await Ingest(s, ("squeue", 0, ["101|relax|RUNNING|1:00:00|2:00:00|1|cn01|cpu"]));
        await Ingest(s, ("squeue", 1, ["slurm_load_jobs error: Socket timed out on send/recv operation"]));
        check(events.Count == 0 && engine.Snapshots[s.Id].Jobs.Select(j => j.Id).SequenceEqual(["101"]), "流程：squeue 出错时不报结束");

        // 关闭"监控任务"、只读进度文件：没有队列 / 进程段也照常更新
        var p = Host(HostKind.Direct, h => h.WatchJobs = false);
        await Ingest(p, ("progress", 0, ["@@SI:FILE /home/u/.serverisland/progress/a.json", """{"name":"train","progress":0.4,"message":"step 4/10"}"""]));
        check(engine.Snapshots[p.Id].Jobs.Count(j => j.Source == JobSource.ProgressFile) == 1, "流程：关闭监控任务时进度文件照常显示");

        // 手动监听：watch 段缺失时不报结束、不取消监听；确实不在了才报
        events.Clear();
        var w = Host(HostKind.Direct, h => h.Watched = [new WatchedProcess { Pid = "999", Name = "sim" }]);
        await Ingest(w, ("procs", 0, []), ("watch", 0, ["999 100 1.0 100 ./sim"]));
        await Ingest(w, ("procs", 0, []), ("watch", null, []));
        check(events.Count == 0 && store.Host(w.Id)?.Watched.Count == 1, "流程：监听段缺失时不报结束、不取消监听");
        await Ingest(w, ("procs", 0, []), ("watch", 0, []));
        check(events.Count == 1 && store.Host(w.Id)?.Watched.Count == 0, "流程：监听的进程确实不在了才报结束");

        // 数组总数随运行中任务保存，重启后用它检查 accounting 的覆盖范围。
        var a = Host(HostKind.Slurm);
        var array = new RemoteJob("500", "scan", JobState.Running, JobSource.Slurm) { Array = new ArrayInfo { BaseID = "500", Total = 2 } };
        var dir = Path.Combine(Path.GetTempPath(), "si-array-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = new HistoryStore(directory: dir);
            saved.SetActive(a.Id, [array]);
            check(new HistoryStore(directory: dir).ActiveJobs(a.Id).FirstOrDefault()?.ArrayTotal == 2, "数组历史：重启后保留已知总数");
            array.Array.Total = 3;
            saved.SetActive(a.Id, [array]);
            check(new HistoryStore(directory: dir).ActiveJobs(a.Id).FirstOrDefault()?.ArrayTotal == 3, "数组历史：总数变化时立即保存");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        array.Array.Total = 2;
        engine.History.SetActive(a.Id, [array]);
        var gone = engine.History.ActiveJobs(a.Id);
        events.Clear();
        foreach (var (states, expected, label) in new (string[], string, string)[]
        {
            (["RUNNING"], HistoryEntry.EndedUnknown, "仍运行"),
            (["SPECIAL_EXIT"], HistoryEntry.EndedUnknown, "未知状态"),
            (["COMPLETED"], HistoryEntry.EndedUnknown, "记录不全"),
            ([], HistoryEntry.EndedUnknown, "没有记录"),
            (["COMPLETED", "COMPLETED"], "COMPLETED", "全部完成"),
            (["FAILED"], "FAILED", "明确失败"),
        })
        {
            var finals = Parsers.Sacct(states.Select((s, i) => $"500_{i + 1}|scan|{s}|00:01:00|0:0"));
            engine.RecordEndedWhileAway(a, gone, finals);
            check(engine.History.Entries(a.Id).FirstOrDefault()?.State == expected && events.Count == 0,
                  $"数组补记：{label}时状态正确且不弹提醒");
        }
        // 老版本 active-jobs.json 没有 arrayTotal，仍能读取，但不能据此确认数组整体成功。
        var old = System.Text.Json.JsonSerializer.Deserialize<HistoryStore.ActiveJob>(
            """{"id":"500","name":"scan","slurm":true,"seenAt":"2026-01-01T00:00:00Z"}""", Json.Options)!;
        old.SeenAt = DateTime.Now;
        engine.RecordEndedWhileAway(a, [old], Parsers.Sacct(["500_1|scan|COMPLETED|00:01:00|0:0"]));
        check(old.ArrayTotal == null && engine.History.Entries(a.Id).FirstOrDefault()?.State == HistoryEntry.EndedUnknown,
              "数组补记：旧记录没有总数时保守记为结果未知");

        // 关注：只提醒关注的任务时，没关注的任务结束不提醒；关注的任务结束后自动取消关注
        events.Clear();
        var f = Host(HostKind.Direct);
        string[] two = ["4242 100 50.0 1000 python train.py", "4343 100 50.0 1000 python eval.py"];
        await Ingest(f, ("procs", 0, two));
        if (engine.Snapshots[f.Id].Jobs.FirstOrDefault(j => j.Id == "4242") is { } j1) engine.ToggleStar(f.Id, j1);
        store.Settings.AlertScope = AlertScope.Starred;
        await Ingest(f, ("procs", 0, []));
        check(events.Count == 1 && events[0].JobID == "4242", "关注：只提醒关注的任务时，没关注的任务结束不提醒");
        check(!store.Settings.StarredJobs.Any(x => x.HostID == f.Id), "关注：任务结束后自动取消关注");
        await Ingest(f, ("procs", 0, two));
        if (engine.Snapshots[f.Id].Jobs.FirstOrDefault(j => j.Id == "4343") is { } j2) engine.ToggleStar(f.Id, j2);
        await Ingest(f, ("procs", 1, []));
        check(engine.IsStarred(f.Id, "4343"), "关注：进程列表出错时不取消关注");
        events.Clear();
        store.Settings.AlertFinished = false;
        await Ingest(f, ("procs", 0, []));
        check(events.Count == 0, "提醒设置：关掉“完成”后不提醒结束");
        store.Settings.AlertFinished = true;
        store.Settings.AlertScope = AlertScope.All;
        var sa = Host(HostKind.Slurm);
        await Ingest(sa, ("squeue", 0, ["700_1|scan|RUNNING|0:10|1:00:00|1|cn01|cpu|700|1", "700_[2-5]|scan|PENDING|0:00|1:00:00|1|(Priority)|cpu|700|[2-5]"]));
        if (engine.Snapshots[sa.Id].Jobs.FirstOrDefault() is { } g) engine.ToggleStar(sa.Id, g);
        check(store.Settings.StarredJobs.LastOrDefault()?.JobID == "700" && engine.IsStarred(sa.Id, "700"), "关注：数组作业按主编号关注");
    }
}
