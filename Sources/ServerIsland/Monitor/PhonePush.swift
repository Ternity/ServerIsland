import Foundation

/// 手机推送：任务结束、失败等提醒发到企业微信群机器人（Webhook），手机上的企业微信 / 微信就能收到。
///
/// - 只在电脑上做：不在手机上连服务器，手机不耗电；电脑睡眠时不推送。
/// - 过滤：按"推送哪些任务"（默认只推送关注的任务）、事件种类、勿扰时段；静音的服务器不推送。
/// - 先攒 10 秒：1～2 条分别发，更多合并成一条；每分钟最多 15 条（企业微信的上限是 20）。
/// - key 只存钥匙串；日志里打码。推送失败不影响查询，也不弹窗。
@MainActor
final class PhonePush: ObservableObject {
    nonisolated static let keychainAccount = "wecom-webhook"
    nonisolated static let endpoint = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key="
    nonisolated static let window: TimeInterval = 10
    nonisolated static let perMinute = 15
    /// 企业微信文本消息上限 2048 字节，留一点余量。
    nonisolated static let maxBytes = 2000

    struct Item: Equatable {
        var text: String
        /// 合并消息里的一行。
        var line: String
        var hostID: UUID?
        var failure: Bool
    }

    struct Status: Equatable {
        var date: Date
        var ok: Bool
        var message: String
    }

    @Published private(set) var status: Status?
    /// key 被企业微信拒绝（93000）：暂停推送，直到改 key 或测试成功。
    @Published private(set) var keyRejected = false
    @Published private(set) var hasKey = false

    private let store: ConfigStore
    private let demo: Bool
    private var cachedKey: String?
    private var pending: [Item] = []
    private var flushTask: Task<Void, Never>?
    private var sentAt: [Date] = []

    init(store: ConfigStore, demo: Bool) {
        self.store = store
        self.demo = demo
        hasKey = (!demo || Self.demoPush) && Keychain.hasSecret(Self.keychainAccount)
    }

    private var settings: PhonePushSettings { store.settings.phonePush }

    /// 演示模式默认不推送；`SERVERISLAND_PUSH_DEMO=1` 时用钥匙串里的 key 推送演示事件（开发时试效果）。
    nonisolated static var demoPush: Bool { ProcessInfo.processInfo.environment["SERVERISLAND_PUSH_DEMO"] == "1" }

    private var key: String? {
        if let cachedKey { return cachedKey }
        cachedKey = Keychain.secret(Self.keychainAccount)
        return cachedKey
    }

    // MARK: - key

    /// 从粘贴的内容里取出 key：可以是整个 Webhook 地址，也可以只是 key。认不出时返回 nil。
    nonisolated static func extractKey(_ input: String) -> String? {
        var s = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if let r = s.range(of: "key=") {
            s = String(s[r.upperBound...])
            if let amp = s.firstIndex(of: "&") { s = String(s[..<amp]) }
        }
        s = s.trimmingCharacters(in: .whitespacesAndNewlines)
        guard s.count >= 16, s.count <= 64,
              s.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }) else { return nil }
        return s
    }

    /// 日志、界面上只显示 key 的最后 4 位。
    nonisolated static func mask(_ key: String) -> String { "****" + String(key.suffix(4)) }

    var maskedKey: String? { hasKey ? key.map(Self.mask) : nil }

    @discardableResult
    func setKey(_ input: String) -> Bool {
        guard let k = Self.extractKey(input), Keychain.saveSecret(k, account: Self.keychainAccount, label: L("企业微信推送", "WeCom push")) else { return false }
        cachedKey = k
        hasKey = true
        keyRejected = false
        status = nil
        DebugLog.log("手机推送：已保存 key \(Self.mask(k))")
        return true
    }

    func removeKey() {
        Keychain.deleteSecret(Self.keychainAccount)
        cachedKey = nil
        hasKey = false
        keyRejected = false
        status = nil
        pending.removeAll()
        DebugLog.log("手机推送：已删除 key")
    }

    /// 预览图用：假装已经保存了 key、发过一条消息。
    func previewState(key: String?, status: Status?) {
        cachedKey = key
        hasKey = key != nil
        self.status = status
    }

    // MARK: - 收集事件

    /// 由 MonitorEngine.flush 调用，传入这一轮的全部任务事件（还没按刘海的提醒设置过滤）。
    func consider(_ batch: [IslandEvent], host: HostConfig, watchedPIDs: Set<String> = []) {
        guard settings.enabled, hasKey, !keyRejected, !demo || Self.demoPush else { return }
        guard !host.muted else { return }
        let all = store.settings
        let quiet = settings.followQuietHours && all.isQuiet()
        for e in batch {
            guard Self.wants(e, settings: all, watchedPIDs: watchedPIDs) else { continue }
            if quiet && !(settings.failuresDuringQuiet && e.kind == .failed) {
                DebugLog.log("手机推送：勿扰时段跳过 \(e.title)")
                continue
            }
            enqueue(Self.item(e, host: host, detailed: settings.detailed))
        }
    }

    /// 这条事件要不要推送（不含静音、勿扰）。
    nonisolated static func wants(_ e: IslandEvent, settings all: AppSettings, watchedPIDs: Set<String> = []) -> Bool {
        let s = all.phonePush
        guard let job = e.jobID, let host = e.hostID else {
            // 服务器级提醒：磁盘快满等。
            return e.kind == .warning && s.serverWarnings
        }
        switch e.kind {
        case .started: if !s.started { return false }
        case .finished: if !s.finished { return false }
        case .failed: if !s.failed { return false }
        case .info: if !s.unknown { return false }
        case .warning: break
        }
        // 手动监听的进程是用户点名要看的：任何范围都推送。
        if watchedPIDs.contains(job) { return true }
        switch s.scope {
        case .all: return true
        case .starred: return all.isStarred(host, job)
        case .island:
            // 刘海的种类开关和关注范围也要满足
            return all.allowsAlert(e)
        }
    }

    /// Agent 会话的提醒（AgentMonitor 发现"需要确认 / 回答完了 / 出错"时调用）。
    func agent(_ s: AgentSession) {
        guard settings.wantsAgents, hasKey, !keyRejected, !demo else { return }
        let attention = s.state == .attention
        if attention ? !settings.agentAttention : !settings.agentDone { return }
        if settings.followQuietHours && store.settings.isQuiet() { return }
        enqueue(Self.agentItem(s, detailed: settings.detailed))
    }

    private func enqueue(_ item: Item) {
        pending.append(item)
        DebugLog.log("手机推送：待发送 \(item.line)")
        if flushTask == nil {
            flushTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(Self.window))
                guard !Task.isCancelled else { return }
                await self?.flush()
            }
        }
    }

    private func flush() async {
        flushTask = nil
        guard !pending.isEmpty else { return }
        // 限速：这一分钟发满了，就等下一分钟合并发送。
        let now = Date()
        sentAt.removeAll { now.timeIntervalSince($0) > 60 }
        let room = Self.perMinute - sentAt.count
        if room <= 0 {
            DebugLog.log("手机推送：一分钟内已发 \(sentAt.count) 条，稍后合并发送")
            flushTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(30))
                guard !Task.isCancelled else { return }
                await self?.flush()
            }
            return
        }
        let items = pending
        pending.removeAll()
        let messages = items.count <= 2 && room >= items.count ? items.map(\.text) : [Self.merged(items, hosts: store.settings.hosts)]
        for m in messages { await send(m, retry: true) }
    }

    // MARK: - 发送

    /// 设置页的"发送测试消息"。
    func sendTest() async {
        await send(Self.testMessage(), retry: false, test: true)
    }

    private func send(_ text: String, retry: Bool, test: Bool = false) async {
        guard let key, let url = URL(string: Self.endpoint + key) else { return }
        sentAt.append(Date())
        let content = Self.truncate(text)
        var req = URLRequest(url: url, timeoutInterval: 15)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["msgtype": "text", "text": ["content": content]])
        let title = content.components(separatedBy: "\n").first ?? ""
        do {
            let (data, _) = try await URLSession.shared.data(for: req)
            let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            let code = obj?["errcode"] as? Int ?? -1
            let msg = obj?["errmsg"] as? String ?? String(decoding: data.prefix(200), as: UTF8.self)
            if code == 0 {
                keyRejected = false
                status = Status(date: Date(), ok: true, message: test ? L("测试消息已发送", "Test message sent") : title)
                DebugLog.log("手机推送：已发送 \(title)")
                return
            }
            let explain = Self.explain(code: code, message: msg)
            status = Status(date: Date(), ok: false, message: explain)
            DebugLog.log("手机推送失败（\(code)）：\(Self.scrub(msg, key: key)) · \(title)")
            if code == 93000 || code == 40001 || code == 40014 {
                keyRejected = true
            } else if code == 45009, retry {
                try? await Task.sleep(for: .seconds(60))
                await send(text, retry: false)
            }
        } catch {
            let m = Self.scrub(error.localizedDescription, key: key)
            status = Status(date: Date(), ok: false, message: L("网络错误：", "Network error: ") + m)
            DebugLog.log("手机推送失败（网络）：\(m) · \(title)" + (retry ? "，30 秒后重试" : ""))
            if retry {
                try? await Task.sleep(for: .seconds(30))
                await send(text, retry: false)
            }
        }
    }

    nonisolated static func explain(code: Int, message: String) -> String {
        switch code {
        case 93000, 40001, 40014: return L("key 无效：请重新复制机器人的 Webhook 地址", "Invalid key: copy the bot's webhook URL again")
        case 45009: return L("发送太频繁，已被企业微信限流", "Rate-limited by WeCom")
        case 40058, 44004: return L("消息内容有误（\(code)）", "Bad message (\(code))")
        default: return "\(code) \(message)"
        }
    }

    nonisolated static func scrub(_ s: String, key: String) -> String {
        key.isEmpty ? s : s.replacingOccurrences(of: key, with: mask(key))
    }

    nonisolated static func truncate(_ s: String, maxBytes: Int = maxBytes) -> String {
        guard s.utf8.count > maxBytes else { return s }
        var out = ""
        var bytes = 0
        for ch in s {
            let n = String(ch).utf8.count
            if bytes + n > maxBytes - 4 { break }
            out.append(ch)
            bytes += n
        }
        return out + "…"
    }

    // MARK: - 消息格式（纯文本：微信插件里也能显示）

    nonisolated static func emoji(_ kind: IslandEvent.Kind, hasFailures: Bool = false) -> String {
        switch kind {
        case .finished: return hasFailures ? "⚠️" : "✅"
        case .failed: return "❌"
        case .started: return "▶️"
        case .info: return "❔"
        case .warning: return "⚠️"
        }
    }

    nonisolated static func hostLine(_ host: HostConfig) -> String {
        let kind: String
        switch host.kind {
        case .slurm: kind = "Slurm"
        case .pbs: kind = "PBS"
        case .direct: kind = L("直连", "direct")
        }
        return L("服务器：", "Server: ") + host.displayName + L("（\(kind)）", " (\(kind))")
    }

    /// 目录只显示最后两层，避免路径里带出用户名：home / Users 后面那一层（用户名）和它之前的都去掉，主目录本身显示成 ~。
    nonisolated static func shortDir(_ path: String) -> String {
        var parts = path.split(separator: "/").map(String.init).filter { !$0.isEmpty }
        var underHome = false
        if let i = parts.lastIndex(where: { $0 == "home" || $0 == "Users" }), i + 1 < parts.count {
            parts.removeFirst(i + 2)
            underHome = true
        }
        if parts.isEmpty { return underHome ? "~" : path }
        if parts.count <= 2 { return (underHome ? "~/" : "/") + parts.joined(separator: "/") }
        return "…/" + parts.suffix(2).joined(separator: "/")
    }

    /// 时长：3 小时 12 分 / 42 分 / 1 天 2 小时。
    nonisolated static func duration(_ t: TimeInterval) -> String {
        let s = max(0, Int(t.rounded()))
        let d = s / 86400, dh = (s % 86400) / 3600, m = (s % 3600) / 60
        // 两天以内用小时（时间上限常写成 24 / 48 小时）
        if d >= 2 { return dh > 0 ? L("\(d) 天 \(dh) 小时", "\(d)d \(dh)h") : L("\(d) 天", "\(d)d") }
        let h = s / 3600
        if h > 0 { return m > 0 ? L("\(h) 小时 \(m) 分", "\(h)h \(m)m") : L("\(h) 小时", "\(h)h") }
        if m > 0 { return L("\(m) 分", "\(m)m") }
        return L("\(s) 秒", "\(s)s")
    }

    /// 钟点：今天只写 14:32，昨天写"昨天 14:32"，更早写日期。
    nonisolated static func clock(_ d: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        let t = f.string(from: d)
        if calendar.isDate(d, inSameDayAs: now) { return t }
        if let y = calendar.date(byAdding: .day, value: -1, to: now), calendar.isDate(d, inSameDayAs: y) { return L("昨天 ", "yesterday ") + t }
        f.dateFormat = "M-d HH:mm"
        return f.string(from: d)
    }

    nonisolated static func resources(_ p: PushInfo) -> String? {
        var parts: [String] = []
        if let n = p.nodes, n > 0 { parts.append(L("\(n) 节点", "\(n) node" + (n == 1 ? "" : "s"))) }
        if let c = p.cpus, c > 0 { parts.append(L("\(c) 核", "\(c) CPU" + (c == 1 ? "" : "s"))) }
        if let g = p.gpus, g > 0 { parts.append("\(g) GPU") }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// 一条事件的完整消息。
    nonisolated static func item(_ e: IslandEvent, host: HostConfig, detailed: Bool, now: Date? = nil) -> Item {
        let p = e.push
        let failures = !(p?.failedElements.isEmpty ?? true)
        let head = emoji(e.kind, hasFailures: failures) + " " + e.title
        var lines = [head, hostLine(host)]
        if detailed {
            lines += body(e, host: host, now: now ?? e.date)
        }
        // 合并消息里的一行：结果 + 名字 + 一两个关键数字
        var short = [emoji(e.kind, hasFailures: failures) + " " + e.title]
        if let p {
            if e.kind == .failed, let st = p.state, !e.title.contains(st) { short.append(st) }
            if let el = p.elapsed, e.kind != .started { short.append(duration(el)) }
            if let a = p.array { short.append(a.components(separatedBy: " / ").first ?? a) }
            if let c = p.cost { short.append(c) }
        } else if !e.subtitle.isEmpty {
            short.append(e.subtitle)
        }
        return Item(text: lines.joined(separator: "\n"), line: short.joined(separator: " · "), hostID: host.id, failure: e.kind == .failed)
    }

    nonisolated static func body(_ e: IslandEvent, host: HostConfig, now: Date) -> [String] {
        guard let p = e.push else {
            return e.subtitle.isEmpty ? [] : [e.subtitle]
        }
        var lines: [String] = []
        // 编号
        if let id = p.id {
            var l: String
            switch p.source {
            case .process: l = L("进程：", "Process: ") + id + (p.watched ? L("（手动监听）", " (watched)") : "")
            case .progressFile: l = L("进度条目：", "Progress entry: ") + id
            case .slurm: l = L("作业号：", "Job ID: ") + id
            }
            if let part = p.partition, !part.isEmpty { l += L(" · 分区 ", " · partition ") + part }
            lines.append(l)
        }
        if let r = resources(p) { lines.append(L("资源：", "Resources: ") + r) }
        if let n = p.nodeList { lines.append(L("节点：", "Nodes: ") + n) }
        // 失败原因
        if e.kind == .failed || e.kind == .info, p.state != nil || p.exitCode != nil {
            var parts: [String] = []
            if let st = p.state, !st.isEmpty { parts.append(st) }
            if let x = p.exitCode, !x.isEmpty, x != "0:0" { parts.append(L("退出码 ", "exit code ") + x) }
            if !parts.isEmpty { lines.append(L("状态：", "State: ") + parts.joined(separator: " · ")) }
        }
        // 用时、上限、起止时间
        if e.kind == .started {
            if let lim = p.limit { lines.append(L("时间上限：", "Time limit: ") + duration(lim)) }
        } else if let el = p.elapsed {
            var l = (p.elapsedAtLeast ? L("运行了至少 ", "Ran at least ") : L("用时：", "Run time: ")) + duration(el)
            if let lim = p.limit, lim > 0 {
                if el >= lim * 0.995 { l += L("（已到时间上限）", " (hit the time limit)") }
                else { l += L("（上限 \(duration(lim))，用了 \(Int((el / lim * 100).rounded()))%）", " (limit \(duration(lim)), \(Int((el / lim * 100).rounded()))% used)") }
            }
            lines.append(l)
            if !p.elapsedAtLeast {
                lines.append(L("时间：", "Time: ") + clock(now.addingTimeInterval(-el), now: now) + L(" 开始 → ", " start → ") + clock(now, now: now) + L(" 结束", " end"))
            }
        }
        if let a = p.array { lines.append(L("结果：", "Result: ") + a) }
        if !p.failedElements.isEmpty {
            let shown = p.failedElements.prefix(10).joined(separator: L("、", ", "))
            let more = p.failedElements.count > 10 ? L(" 等 \(p.failedElements.count) 个", " and \(p.failedElements.count - 10) more") : ""
            lines.append(L("失败的元素：", "Failed elements: ") + shown + more)
        }
        if let app = p.app { lines.append(app) }
        if let n = p.appNote { lines.append(n) }
        if let s = p.sample { lines.append(L("最后一次采样：", "Last sample: ") + s) }
        if let n = p.note, !n.isEmpty { lines.append(n) }
        if let d = p.dir, !d.isEmpty { lines.append(L("目录：", "Directory: ") + shortDir(d)) }
        if let c = p.cost { lines.append(L("费用：约 ", "Cost: about ") + c) }
        return lines
    }

    nonisolated static func agentItem(_ s: AgentSession, detailed: Bool) -> Item {
        let name: String
        switch s.tool {
        case "claude": name = "Claude Code"
        case "codex": name = "Codex"
        case "opencode": name = "OpenCode"
        default: name = s.tool
        }
        let head: String, detail: String
        switch s.state {
        case .attention: head = "🙋 " + L("\(name) 需要你确认", "\(name) needs you"); detail = s.message
        case .error: head = "❌ " + L("\(name) 出错了", "\(name) hit an error"); detail = s.title
        default: head = "💬 " + L("\(name) 回答完了", "\(name) is done"); detail = s.title
        }
        let project = s.cwd.isEmpty ? "" : (s.cwd as NSString).lastPathComponent
        var lines = [head]
        if !project.isEmpty { lines.append(L("项目：", "Project: ") + project) }
        if detailed, !detail.isEmpty { lines.append(String(detail.prefix(300))) }
        let line = head + (project.isEmpty ? "" : " · " + project)
        return Item(text: lines.joined(separator: "\n"), line: line, hostID: nil, failure: s.state == .error)
    }

    /// 合并成一条：同一台服务器时标题写服务器名，每行省略服务器；多台时每行带上服务器名。
    nonisolated static func merged(_ items: [PhonePush.Item], hosts: [HostConfig]) -> String {
        let ids = Set(items.map(\.hostID))
        let name: (UUID?) -> String? = { id in id.flatMap { i in hosts.first { $0.id == i }?.displayName } }
        var lines: [String]
        if ids.count == 1, let n = name(items[0].hostID) {
            lines = ["📦 " + L("\(n)：\(items.count) 条提醒", "\(n): \(items.count) updates")]
            lines += items.prefix(12).map(\.line)
        } else {
            lines = ["📦 " + L("ServerIsland：\(items.count) 条提醒", "ServerIsland: \(items.count) updates")]
            lines += items.prefix(12).map { i in i.line + (name(i.hostID).map { " · " + $0 } ?? "") }
        }
        if items.count > 12 { lines.append(L("……还有 \(items.count - 12) 条", "…and \(items.count - 12) more")) }
        return lines.joined(separator: "\n")
    }

    nonisolated static func testMessage() -> String {
        L("✅ ServerIsland 测试消息\n能看到这条，说明手机推送已经设置好了。\n之后任务结束、失败时，这里会收到提醒。",
          "✅ ServerIsland test message\nIf you can read this, phone push is set up.\nYou'll get alerts here when jobs finish or fail.")
    }

    // MARK: - 示例（--push-preview 和自检用）

    nonisolated static func samples(now: Date = Date()) -> [(IslandEvent, HostConfig)] {
        var slurm = HostConfig(alias: "cluster-a", displayName: L("集群A", "Cluster A"), kind: .slurm)
        slurm.id = UUID()
        var direct = HostConfig(alias: "ws", displayName: L("工作站", "Workstation"), kind: .direct)
        direct.id = UUID()
        var out: [(IslandEvent, HostConfig)] = []
        func ev(_ k: IslandEvent.Kind, _ t: String, _ h: HostConfig, _ job: String?, _ p: PushInfo?, sub: String = "") {
            var e = IslandEvent(kind: k, title: t, subtitle: sub, hostName: h.displayName, hostID: h.id, jobID: job, push: p)
            e.date = now
            out.append((e, h))
        }
        var p = PushInfo()
        p.id = "12345"; p.partition = "gpu"; p.nodes = 1; p.cpus = 32; p.gpus = 4
        p.elapsed = 3 * 3600 + 12 * 60; p.limit = 24 * 3600; p.dir = "/home/demo/nep/train-03"
        p.app = L("GNEP 训练：200/200 epoch · loss 1.23e-2", "GNEP training: 200/200 epoch · loss 1.23e-2"); p.cost = "¥38.40"
        ev(.finished, L("gnep-train 已完成", "gnep-train finished"), slurm, "12345", p)

        p = PushInfo()
        p.id = "12346"; p.partition = "cpu"; p.nodes = 2; p.cpus = 128; p.state = "TIMEOUT"; p.exitCode = "0:0"
        p.elapsed = 24 * 3600; p.limit = 24 * 3600; p.dir = "/home/demo/vasp/relax-Si"
        p.app = L("VASP 结构优化：87 离子步", "VASP relaxation: 87 ionic steps")
        ev(.failed, L("relax-Si 超时", "relax-Si Timed out"), slurm, "12346", p)

        p = PushInfo()
        p.source = .process; p.id = "8812"; p.elapsed = 5 * 3600 + 40 * 60; p.elapsedAtLeast = true
        p.app = L("NEP 训练：12,000/50,000 代 · L1 4.51e-2", "NEP training: 12,000/50,000 gen · L1 4.51e-2")
        p.appNote = L("输出文件最后更新于 3分钟前", "output last updated 3m ago")
        p.sample = L("CPU 98% · 内存 3.2 GB · 显存 18.2 GB", "CPU 98% · mem 3.2 GB · GPU mem 18.2 GB"); p.dir = "/data/demo/nep/train-03"
        ev(.failed, L("NEP 训练提前结束", "NEP training ended early"), direct, "8812", p)

        p = PushInfo()
        p.id = "12400"; p.partition = "cpu"; p.array = L("98 个成功 · 2 个失败 / 共 100 个", "98 done · 2 failed / 100 total")
        p.failedElements = ["#17", "#52"]; p.dir = "/home/demo/scan"
        ev(.failed, L("scan 数组已全部结束", "scan: array finished"), slurm, "12400", p)

        p = PushInfo()
        p.id = "12410"; p.partition = "gpu"; p.nodeList = "gpu07"; p.limit = 24 * 3600
        ev(.started, L("md-run 开始运行", "md-run started"), slurm, "12410", p)

        ev(.warning, L("磁盘快满了：/home", "Disk almost full: /home"), slurm, nil, nil,
           sub: L("已用 92% · 剩余 41 GB", "92% used · 41 GB free"))
        return out
    }
}
