import Foundation

/// AI 编程工具（Claude Code、Codex、OpenCode）的会话状态，给 USB 副屏的 Agents 页面和提醒用。
/// - Claude Code / OpenCode：装了钩子 / 插件后，每个事件会运行 `ServerIsland --agent-event <工具>`，
///   往 `agent-events.jsonl` 追加一行（`AgentEvents.record`）；App 读这个文件。
/// - Codex：它的 notify 只能设一个程序（常被别的工具占用），所以不装东西，直接读 ~/.codex/sessions 里最近的会话记录。
/// 只读这些工具的记录，不改它们的行为；钩子都是异步的，不会拖慢它们，也从不替用户批准或拒绝权限请求。
enum AgentEvents {
    static var fileURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ServerIsland/agent-events.jsonl")
    }

    /// 命令行入口：钩子把事件 JSON 从标准输入（OpenCode 插件放在参数里）传进来。整理成一行写进事件文件。
    /// 不往标准输出打印任何东西（对 Claude 的 PermissionRequest 来说，有输出就可能被当成决定）。
    static func record(tool: String, argument: String?) {
        let raw: Data = argument.map { Data($0.utf8) } ?? FileHandle.standardInput.readDataToEndOfFile()
        guard let obj = (try? JSONSerialization.jsonObject(with: raw)) as? [String: Any] else { return }
        var line: [String: Any] = ["tool": tool, "at": Date().timeIntervalSince1970]
        line["session"] = obj["session_id"] ?? obj["sessionID"] ?? obj["session"] ?? ""
        line["cwd"] = obj["cwd"] ?? obj["directory"] ?? ""
        line["event"] = obj["hook_event_name"] ?? obj["type"] ?? ""
        if let t = obj["notification_type"] as? String { line["kind"] = t }
        if let m = obj["message"] as? String { line["message"] = String(m.prefix(120)) }
        if let tn = obj["tool_name"] as? String { line["toolName"] = tn }
        if let status = obj["status"] as? String { line["status"] = status }
        else if let status = obj["status"] as? [String: Any] { line["status"] = status["type"] }
        let input = obj["tool_input"] as? [String: Any] ?? obj
        if let q = (input["questions"] as? [[String: Any]])?.first,
           let question = (q["question"] ?? q["header"]) as? String { line["message"] = String(question.prefix(120)) }
        // 只留提问的开头当会话标题（本机文件，不外传）。
        if let p = obj["prompt"] as? String {
            line["prompt"] = String(p.split(whereSeparator: \.isNewline).first.map(String.init)?.prefix(60) ?? "")
        }
        guard var data = try? JSONSerialization.data(withJSONObject: line) else { return }
        data.append(0x0A)
        let url = fileURL
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        // O_APPEND：多个钩子同时写也不会交错（单次写入很小）。
        let fd = open(url.path, O_WRONLY | O_APPEND | O_CREAT, 0o600)
        guard fd >= 0 else { return }
        _ = data.withUnsafeBytes { write(fd, $0.baseAddress, data.count) }
        close(fd)
        DistributedNotificationCenter.default().postNotificationName(.init("ServerIsland.agentEvent"), object: nil,
                                                                     userInfo: nil, deliverImmediately: true)
    }
}

/// 一个会话的状态。
struct AgentSession: Equatable {
    enum State: String { case working, attention, done, error }
    var tool: String
    var id: String
    var cwd: String
    var title: String
    var state: State
    var message: String
    var changedAt: Date
}

@MainActor
final class AgentMonitor {
    private(set) var sessions: [String: AgentSession] = [:]
    /// 状态变化（需要你确认 / 回答完了）时回调，用来发提醒。
    var onAlert: ((AgentSession) -> Void)?
    var onChange: (() -> Void)?

    private var offset: UInt64 = 0
    private var timer: Timer?
    private var observer: NSObjectProtocol?
    private var codexOffsets: [String: UInt64] = [:]
    private var codexUserSessions: [String: Bool] = [:]
    private var codexDirectories: [String: String] = [:]
    private var codexTurns: [String: String] = [:]
    private struct Question {
        var unanswered: Set<Int>
        let asynchronous: Bool
        let message: String
    }
    private var codexQuestions: [String: [String: Question]] = [:]

    /// 会话多久没动静就不再显示（已完成的）/ 不再认为在跑（工作中的，可能被强制退出了）。
    static let doneTTL: TimeInterval = 2 * 3600
    static let staleWorking: TimeInterval = 30 * 60

    func start() {
        // 启动时只看最近的事件，不把以前的提醒再发一遍。
        loadInitial()
        observer = DistributedNotificationCenter.default().addObserver(forName: .init("ServerIsland.agentEvent"), object: nil,
                                                                       queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
    }

    /// 发给副屏的列表：需要你的排最前，然后是在跑的，再后是刚完成的。
    var list: [AgentSession] {
        let order: [AgentSession.State: Int] = [.attention: 0, .working: 1, .error: 2, .done: 3]
        return sessions.values.sorted {
            (order[$0.state] ?? 9, -$0.changedAt.timeIntervalSince1970) < (order[$1.state] ?? 9, -$1.changedAt.timeIntervalSince1970)
        }
    }

    private func loadInitial() {
        let url = AgentEvents.fileURL
        // 文件太大就截掉前面一半（只是缓冲，不需要保留历史）。
        if let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
           let size = (attrs[.size] as? NSNumber)?.uint64Value, size > 2_000_000, let data = try? Data(contentsOf: url) {
            try? data.suffix(200_000).write(to: url)
        }
        offset = 0
        readEvents(alert: false)
        scanCodex(alert: false)
        prune()
    }

    private func poll() {
        let before = sessions
        readEvents(alert: true)
        scanCodex(alert: true)
        prune()
        if sessions != before { onChange?() }
    }

    private func readEvents(alert: Bool) {
        guard let h = try? FileHandle(forReadingFrom: AgentEvents.fileURL) else { return }
        defer { try? h.close() }
        let end = (try? h.seekToEnd()) ?? 0
        if end < offset { offset = 0 }
        try? h.seek(toOffset: offset)
        guard let data = try? h.readToEnd() else { return }
        // 只处理完整的行，剩下半行下次再读。
        guard let last = data.lastIndex(of: 0x0A) else { return }
        offset += UInt64(last - data.startIndex + 1)
        for line in data[data.startIndex...last].split(separator: 0x0A) {
            guard let o = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any] else { continue }
            apply(o, alert: alert)
        }
    }

    func apply(_ o: [String: Any], alert: Bool) {
        let tool = o["tool"] as? String ?? "?"
        let sid = o["session"] as? String ?? ""
        let key = tool + "|" + sid
        let at = Date(timeIntervalSince1970: o["at"] as? Double ?? Date().timeIntervalSince1970)
        let event = o["event"] as? String ?? ""
        // 第一次见到的会话先当作空闲：只有看到它开始干活，之后的"完成"才提醒。
        var s = sessions[key] ?? AgentSession(tool: tool, id: sid, cwd: "", title: "", state: .done, message: "", changedAt: at)
        // 钩子是异步跑的，写进文件的顺序可能和发生顺序不同：比上一次状态变化还早的事件（比如晚到的
        // permission_prompt 通知，而工具其实已经批准并跑完了）不能再把状态改回去。
        if sessions[key] != nil, at < s.changedAt, event != "SessionEnd", event != "session.deleted" { return }
        if let c = o["cwd"] as? String, !c.isEmpty { s.cwd = c }
        if let p = o["prompt"] as? String, !p.isEmpty { s.title = p }
        let old = s.state
        switch (tool, event) {
        case ("claude", "SessionEnd"), ("opencode", "session.deleted"):
            sessions[key] = nil
            return
        case ("claude", "UserPromptSubmit"), ("claude", "PostToolUse"), ("claude", "PostToolUseFailure"), ("claude", "SessionStart"),
             ("claude", "ElicitationResult"), ("opencode", "question.replied"), ("opencode", "question.rejected"), ("opencode", "permission.replied"):
            s.state = event == "SessionStart" ? .done : .working
            s.message = ""
        case ("opencode", "session.status"):
            let status = o["status"] as? String ?? ""
            if status == "idle" { s.state = .done }
            else if status == "busy" || status == "retry" { s.state = .working }
            else { return }
            s.message = ""
        case ("claude", "PreToolUse"):
            guard ["AskUserQuestion", "ExitPlanMode"].contains(o["toolName"] as? String ?? "") else { return }
            s.state = .attention
            s.message = o["message"] as? String ?? L("有问题等你回答", "Waiting for your answer")
        case ("claude", "Elicitation"), ("opencode", "question.asked"):
            s.state = .attention
            s.message = o["message"] as? String ?? L("有问题等你回答", "Waiting for your answer")
        case ("claude", "PermissionRequest"), ("opencode", "permission.asked"):
            s.state = .attention
            s.message = (o["toolName"] as? String).map { L("要用 \($0)，等你确认", "Wants to use \($0)") } ?? L("等你确认", "Needs your OK")
        case ("claude", "Notification"):
            let kind = o["kind"] as? String ?? ""
            if ["permission_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input"].contains(kind) {
                s.state = .attention
                s.message = o["message"] as? String ?? L("等你确认", "Needs your OK")
            } else if ["elicitation_complete", "elicitation_response"].contains(kind) {
                s.state = .working
                s.message = ""
            } else if kind == "idle_prompt" {
                s.state = .done
            }
        case ("claude", "Stop"), ("opencode", "session.idle"):
            s.state = .done
            s.message = ""
        case ("claude", "StopFailure"), ("opencode", "session.error"):
            s.state = .error
            s.message = L("出错了", "Error")
        default:
            return
        }
        if s.state != old || sessions[key] == nil { s.changedAt = at }
        sessions[key] = s
        // 提醒：变成"需要你确认"，或者从"工作中"变成"完成 / 出错"。
        if alert, s.state != old, s.state == .attention || (old == .working && (s.state == .done || s.state == .error)) {
            onAlert?(s)
        }
    }

    /// 只接收用户会话；子 agent 和内部辅助任务的完成不代表用户这一轮结束。
    nonisolated static func isCodexUserSession(_ metadata: [String: Any]) -> Bool {
        if let parent = metadata["parent_thread_id"] as? String, !parent.isEmpty { return false }
        guard let source = metadata["source"] as? String, !source.isEmpty else { return false }
        return source != "subagent"
    }

    /// Codex：读最近 6 小时有改动的用户会话，按 turn_id 匹配开始和结束。
    private func scanCodex(alert: Bool) {
        let root = URL(fileURLWithPath: NSHomeDirectory() + "/.codex/sessions")
        guard let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let cutoff = Date().addingTimeInterval(-6 * 3600)
        for case let url as URL in walker where url.pathExtension == "jsonl" {
            guard let m = try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate, m > cutoff else { continue }
            readCodex(url, alert: alert)
        }
    }

    func readCodex(_ url: URL, alert: Bool) {
        guard let h = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? h.close() }
        let path = url.path
        // 元数据在文件头；增量读取从尾部开始时也必须先检查，不能漏掉子任务标记。
        if codexUserSessions[path] == nil {
            guard let header = try? h.read(upToCount: 256_000), let newline = header.firstIndex(of: 0x0A),
                  let obj = (try? JSONSerialization.jsonObject(with: header.prefix(upTo: newline))) as? [String: Any],
                  obj["type"] as? String == "session_meta", let metadata = obj["payload"] as? [String: Any] else { return }
            codexUserSessions[path] = Self.isCodexUserSession(metadata)
            codexDirectories[path] = metadata["cwd"] as? String
        }
        guard codexUserSessions[path] == true else { return }
        let end = (try? h.seekToEnd()) ?? 0
        var start = codexOffsets[path] ?? (end > 400_000 ? end - 400_000 : 0)
        if end < start { start = 0 }
        try? h.seek(toOffset: start)
        guard let data = try? h.readToEnd(), let last = data.lastIndex(of: 0x0A) else { return }
        codexOffsets[path] = start + UInt64(last - data.startIndex + 1)
        let key = "codex|" + url.deletingPathExtension().lastPathComponent
        for line in data[data.startIndex...last].split(separator: 0x0A) {
            // 先粗筛，避免每行都解析大段 JSON。
            guard line.count < 200_000 else { continue }
            let text = String(decoding: line, as: UTF8.self)
            guard text.contains("\"session_meta\"") || text.contains("\"task_started\"") || text.contains("\"task_complete\"")
                    || text.contains("\"turn_aborted\"") || text.contains("\"user_message\"")
                    || text.contains("\"function_call\"") || text.contains("\"function_call_output\"")
                    || (text.contains("\"message\"") && text.contains("\"user\"")),
                  let o = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any] else { continue }
            let payload = o["payload"] as? [String: Any] ?? [:]
            let at = (o["timestamp"] as? String).flatMap { ISO8601DateFormatter.fractional.date(from: $0) } ?? Date()
            var s = sessions[key] ?? AgentSession(tool: "codex", id: key, cwd: codexDirectories[path] ?? "", title: "", state: .done, message: "", changedAt: at)
            let old = s.state
            var newQuestion = false
            switch (o["type"] as? String, payload["type"] as? String) {
            case ("session_meta", _):
                s.cwd = payload["cwd"] as? String ?? s.cwd
            case ("event_msg", "user_message"):
                if let m = payload["message"] as? String {
                    s.title = String(m.split(whereSeparator: \.isNewline).first.map(String.init)?.prefix(60) ?? "")
                }
            case ("event_msg", "task_started"):
                guard let turn = payload["turn_id"] as? String else { continue }
                codexTurns[key] = turn
                s.state = codexQuestions[key, default: [:]].isEmpty ? .working : .attention
            case ("event_msg", "task_complete"):
                guard let turn = payload["turn_id"] as? String, turn == codexTurns[key] else { continue }
                codexTurns[key] = nil
                s.state = codexQuestions[key, default: [:]].isEmpty ? .done : .attention
            case ("event_msg", "turn_aborted"):
                guard let turn = payload["turn_id"] as? String, turn == codexTurns[key] else { continue }
                codexTurns[key] = nil
                codexQuestions[key] = nil
                s.state = .done
            case ("response_item", "function_call"):
                let name = (payload["name"] as? String ?? "").split(separator: ".").last.map(String.init) ?? ""
                guard name == "request_user_input" || name == "request_user_input_async",
                      let call = payload["call_id"] as? String,
                      let arguments = payload["arguments"] as? String, let bytes = arguments.data(using: .utf8),
                      let args = (try? JSONSerialization.jsonObject(with: bytes)) as? [String: Any],
                      let questions = args["questions"] as? [[String: Any]], !questions.isEmpty else { continue }
                let message = String(((questions.first?["title"] ?? questions.first?["question"]) as? String ?? L("有问题等你回答", "Waiting for your answer")).prefix(120))
                newQuestion = codexQuestions[key]?[call] == nil
                codexQuestions[key, default: [:]][call] = Question(unanswered: Set(questions.indices), asynchronous: name.hasSuffix("_async"), message: message)
                s.state = .attention
                s.message = message
            case ("response_item", "function_call_output"):
                guard let call = payload["call_id"] as? String, let question = codexQuestions[key]?[call], !question.asynchronous else { continue }
                codexQuestions[key]?[call] = nil
                s.state = codexQuestions[key, default: [:]].isEmpty ? (codexTurns[key] == nil ? .done : .working) : .attention
                s.message = ""
            case ("response_item", "message") where payload["role"] as? String == "user":
                let body = (payload["content"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }.joined(separator: "\n")
                if let begin = body.range(of: "<send_user_message_question_reply>"),
                   let end = body.range(of: "</send_user_message_question_reply>"), begin.upperBound <= end.lowerBound {
                    let json = String(body[begin.upperBound..<end.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
                    if let data = json.data(using: .utf8), let replies = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] {
                        for reply in replies {
                            guard let raw = reply["questionItemId"] as? String, let data = raw.data(using: .utf8),
                                  let id = (try? JSONSerialization.jsonObject(with: data)) as? [Any], id.count >= 3,
                                  let call = id[1] as? String, let index = id[2] as? Int, var question = codexQuestions[key]?[call] else { continue }
                            question.unanswered.remove(index)
                            codexQuestions[key]?[call] = question.unanswered.isEmpty ? nil : question
                        }
                    }
                } else if !body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    codexQuestions[key] = nil
                    s.title = String(body.split(whereSeparator: \.isNewline).first.map(String.init)?.prefix(60) ?? "")
                }
                s.state = codexQuestions[key, default: [:]].isEmpty ? (codexTurns[key] == nil ? .done : .working) : .attention
                s.message = codexQuestions[key]?.values.first?.message ?? ""
            default:
                continue
            }
            if s.state != old || sessions[key] == nil || newQuestion { s.changedAt = at }
            sessions[key] = s
            if alert, newQuestion, abs(Date().timeIntervalSince(at)) < 30 { onAlert?(s) }
            // 中断不报“回答完了”，也不补报几小时前的历史完成事件。
            if alert, payload["type"] as? String == "task_complete", s.state == .done, old == .working,
               Date().timeIntervalSince(at) < 30 { onAlert?(s) }
        }
    }

    private func prune() {
        let now = Date()
        sessions = sessions.filter { _, s in
            switch s.state {
            case .working: return now.timeIntervalSince(s.changedAt) < Self.staleWorking
            case .attention: return now.timeIntervalSince(s.changedAt) < Self.doneTTL
            case .done, .error: return now.timeIntervalSince(s.changedAt) < Self.doneTTL
            }
        }
    }
}

extension ISO8601DateFormatter {
    static let fractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
}

/// 一键安装 / 卸载钩子。只追加、只删除我们自己的那几条（命令里带 `--agent-event`），不动用户原有的配置；
/// 改之前先备份。
enum AgentHooks {
    static var claudeSettings: URL { URL(fileURLWithPath: NSHomeDirectory() + "/.claude/settings.json") }
    static var opencodePlugin: URL { URL(fileURLWithPath: NSHomeDirectory() + "/.config/opencode/plugins/serverisland.js") }
    static let claudeEvents = ["SessionStart", "UserPromptSubmit", "PermissionRequest", "Notification", "PreToolUse", "PostToolUse",
                               "PostToolUseFailure", "Elicitation", "ElicitationResult", "Stop", "StopFailure", "SessionEnd"]

    /// 钩子里调用的程序：已安装的 App 本身。
    static var executable: String { Bundle.main.executablePath ?? "/Applications/ServerIsland.app/Contents/MacOS/ServerIsland" }

    static var claudeAvailable: Bool { FileManager.default.fileExists(atPath: NSHomeDirectory() + "/.claude") }
    static var opencodeAvailable: Bool { FileManager.default.fileExists(atPath: NSHomeDirectory() + "/.config/opencode") }
    static var codexAvailable: Bool { FileManager.default.fileExists(atPath: NSHomeDirectory() + "/.codex/sessions") }

    static func isOurs(_ group: Any) -> Bool {
        guard let g = group as? [String: Any], let hooks = g["hooks"] as? [[String: Any]] else { return false }
        return hooks.contains { ($0["command"] as? String)?.contains("--agent-event claude") == true }
    }

    static func claudeInstalled(at url: URL = claudeSettings) -> Bool {
        guard let data = try? Data(contentsOf: url),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let hooks = obj["hooks"] as? [String: Any] else { return false }
        return claudeEvents.allSatisfy { ev in (hooks[ev] as? [Any])?.contains(where: isOurs) == true }
    }

    static func claudePresent(at url: URL = claudeSettings) -> Bool {
        guard let data = try? Data(contentsOf: url), let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let hooks = obj["hooks"] as? [String: Any] else { return false }
        return hooks.values.contains { ($0 as? [Any])?.contains(where: isOurs) == true }
    }

    /// 安装或卸载 Claude Code 的钩子。返回 nil 表示成功，否则是错误说明。
    @discardableResult
    static func setClaude(installed: Bool, at url: URL = claudeSettings, executable exe: String = executable) -> String? {
        var obj: [String: Any] = [:]
        if let data = try? Data(contentsOf: url) {
            guard let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return L("settings.json 不是合法的 JSON，没有修改", "settings.json isn't valid JSON; left unchanged")
            }
            obj = o
            let backup = url.deletingLastPathComponent().appendingPathComponent("settings.json.serverisland-backup")
            try? FileManager.default.removeItem(at: backup)
            try? FileManager.default.copyItem(at: url, to: backup)
        }
        var hooks = obj["hooks"] as? [String: Any] ?? [:]
        for ev in claudeEvents {
            var groups = (hooks[ev] as? [Any] ?? []).filter { !isOurs($0) }
            if installed {
                let cmd = "'\(exe)' --agent-event claude"
                var group: [String: Any] = ["hooks": [["type": "command", "command": cmd, "async": true, "timeout": 10]]]
                if ev == "PreToolUse" { group["matcher"] = "AskUserQuestion|ExitPlanMode" }
                groups.append(group)
            }
            hooks[ev] = groups.isEmpty ? nil : groups
        }
        obj["hooks"] = hooks.isEmpty ? nil : hooks
        guard let out = try? JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else {
            return L("写入失败", "Couldn't write")
        }
        do { try out.write(to: url, options: .atomic) } catch { return error.localizedDescription }
        return nil
    }

    static var opencodeInstalled: Bool { FileManager.default.fileExists(atPath: opencodePlugin.path) }
    static var opencodeCurrent: Bool {
        (try? String(contentsOf: opencodePlugin, encoding: .utf8))?.contains("ServerIsland hooks v2") == true
    }

    @discardableResult
    static func setOpenCode(installed: Bool, exe: String = executable) -> String? {
        if !installed {
            try? FileManager.default.removeItem(at: opencodePlugin)
            return nil
        }
        // 插件把关心的事件原样交给 ServerIsland（参数里的 JSON），出错也不影响 OpenCode。
        let js = """
        // ServerIsland hooks v2：只通知状态，不回答问题、不批准权限。
        const EXE = \(String(data: (try? JSONSerialization.data(withJSONObject: [exe], options: [])) ?? Data("[\"\"]".utf8), encoding: .utf8)!)[0]
        const WATCH = new Set(["session.idle", "session.status", "session.error", "session.deleted", "permission.asked", "permission.replied", "question.asked", "question.replied", "question.rejected"])
        export const ServerIsland = async ({ $, directory }) => ({
          event: async ({ event }) => {
            if (!WATCH.has(event.type)) return
            const p = event.properties ?? {}
            const payload = JSON.stringify({ type: event.type, sessionID: p.sessionID ?? p.info?.id ?? "", directory,
              status: p.status?.type, message: p.questions?.[0]?.question ?? p.questions?.[0]?.header })
            try { await $`${EXE} --agent-event opencode ${payload}`.quiet() } catch {}
          },
        })
        """
        do {
            try FileManager.default.createDirectory(at: opencodePlugin.deletingLastPathComponent(), withIntermediateDirectories: true)
            try js.write(to: opencodePlugin, atomically: true, encoding: .utf8)
        } catch { return error.localizedDescription }
        return nil
    }
}
