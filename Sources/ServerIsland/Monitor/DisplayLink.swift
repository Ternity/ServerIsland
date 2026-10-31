import AppKit
import Combine
import CoreGraphics
import Foundation
import IOKit
import IOKit.serial

/// USB 副屏（ESP32-S3 + AMOLED，固件在本地的 firmware/，暂不进仓库）：把当前状态和提醒通过 USB 串口推过去。
/// 只连 Espressif 的设备（USB 厂商号 0x303a），而且要先握手：发 hi，对方回 hello 才算副屏。协议见 firmware/PROTOCOL.md。
@MainActor
final class DisplayLink: ObservableObject {
    nonisolated static let espressifVendorID = 0x303a

    private let engine: MonitorEngine
    private let store: ConfigStore
    private var port: SerialPort?
    /// 收到 hello 之后才发数据。
    private var linked = false { didSet { if linked != connected { connected = linked } } }
    /// 给设置页看的：连着没有、固件版本、板子上的设置（板子发来的 settings 消息，见 PROTOCOL.md）。
    @Published private(set) var connected = false
    /// 现在用哪条通道：USB 优先，没有 USB 时用蓝牙。
    enum Transport { case usb, ble }
    @Published private(set) var transport: Transport?
    /// 两条通道各自握手成功没有（收到 hello）。
    private var usbReady = false
    private var bleReady = false
    private var ble: BLELink?
    @Published private(set) var firmware: String?
    @Published private(set) var board: [String: Int] = [:]
    @Published private(set) var network: [String: String] = [:]
    private var networkClients: Int?
    private var lockObservers: [NSObjectProtocol] = []
    private var macLocked = false
    private var screenSleeping = false
    private var scanTimer: Timer?
    private var heartbeat: Timer?
    private var aiTimer: Timer?
    private var slowTimer: Timer?
    /// AI 编程工具的会话（Claude Code / Codex / OpenCode）。
    let agents = AgentMonitor()
    /// 日程、截止日期、天气。
    let extras = DisplayExtras()
    private var extrasTick = 0
    private var lastExtrasKey = ""
    private var handshakeTimer: Timer?
    private var bleHandshakeTimer: Timer?
    private var cancellables: Set<AnyCancellable> = []
    private var pendingSnap: DispatchWorkItem?
    /// 不是副屏固件（握手没回应）的设备，本次运行内不再尝试。
    private var rejected: Set<String> = []

    init(engine: MonitorEngine, store: ConfigStore) {
        self.engine = engine
        self.store = store
    }

    func start() {
        // 插拔检测用定时扫描（每 3 秒一次，只查 IOKit 注册表，很轻）。
        scanTimer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.scan() }
        }
        // 状态一变就推（合并 0.3 秒内的连续变化）；另外每 10 秒推一次当心跳，副屏 35 秒收不到就显示断开。
        engine.$snapshots.map { _ in () }
            .merge(with: engine.$currentEvent.map { _ in () })
            .sink { [weak self] in self?.scheduleSnapshot() }
            .store(in: &cancellables)
        heartbeat = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sendSnapshot() }
        }
        // AI 用量变化慢，一分钟读一次就够了。
        aiTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sendAIUsage() }
        }
        // Agents 列表（里面有"多久之前"）和任务历史：半分钟发一次。
        slowTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.sendAgents()
                self.sendHistory()
                // 日程和天气：5 分钟一次（天气自己有 30 分钟的缓存）。
                self.extrasTick += 1
                if self.extrasTick % 10 == 0 { self.sendExtras() }
            }
        }
        NotificationCenter.default.addObserver(forName: .EKEventStoreChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.sendExtras() }
        }
        // 设置页改了日程开关、截止日期、城市：马上发一次。
        store.$settings
            .map { "\($0.displayCalendar)|\($0.deadlines.map { "\($0.title)@\($0.date.timeIntervalSince1970)" })|\($0.weatherCity)" }
            .removeDuplicates()
            .sink { [weak self] key in
                DispatchQueue.main.async { MainActor.assumeIsolated { self?.extrasChanged(key) } }
            }
            .store(in: &cancellables)
        // 锁屏与显示器睡眠是独立状态，唤醒显示器不等于已经解锁。
        macLocked = Self.sessionLocked()
        let dnc = DistributedNotificationCenter.default()
        let ws = NSWorkspace.shared.notificationCenter
        for (center, name, on) in [(dnc as NotificationCenter, Notification.Name("com.apple.screenIsLocked"), false),
                                   (dnc, Notification.Name("com.apple.screenIsUnlocked"), true),
                                   (ws, NSWorkspace.screensDidSleepNotification, false),
                                   (ws, NSWorkspace.screensDidWakeNotification, true)] {
            lockObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    if name == Notification.Name("com.apple.screenIsLocked") || name == Notification.Name("com.apple.screenIsUnlocked") {
                        self.macLocked = !on
                    } else {
                        self.screenSleeping = !on
                        if on { self.macLocked = self.macLocked || Self.sessionLocked() }
                    }
                    self.sendScreenState()
                }
            })
        }
        store.$settings.map(\.displayLockSync).removeDuplicates()
            .sink { [weak self] _ in
                DispatchQueue.main.async { MainActor.assumeIsolated { self?.sendScreenState() } }
            }
            .store(in: &cancellables)
        agents.onChange = { [weak self] in self?.sendAgents() }
        agents.onAlert = { [weak self] s in self?.sendAgentAlert(s) }
        agents.start()
        scan()
    }

    /// 由 MonitorEngine.emit 调用（静音的服务器在那之前就被过滤掉了）。
    func send(event: IslandEvent) {
        guard linked else { return }
        let kind: String
        switch event.kind {
        case .finished: kind = "finished"
        case .failed: kind = "failed"
        case .started: kind = "started"
        case .warning: kind = "warning"
        case .info: kind = "info"
        }
        write(["t": "event", "kind": kind, "title": event.title, "sub": event.subtitle, "host": event.hostName])
    }

    // MARK: - 连接

    private func scan() {
        updateBluetooth()
        guard store.settings.usbDisplay else {
            if port != nil { disconnect(reason: "设置里关闭了副屏") }
            return
        }
        let devices = Self.espressifPorts().filter { !rejected.contains($0) }
        if let p = port {
            if !devices.contains(p.path) && !rejected.contains(p.path) { disconnect(reason: "副屏已拔出") }
            return
        }
        guard let path = devices.first else { return }
        guard let p = SerialPort(path: path) else {
            DebugLog.log("副屏：打不开 \(path)")
            rejected.insert(path)
            return
        }
        DebugLog.log("副屏：发现 \(path)，握手中")
        port = p
        p.onLine = { [weak self] line in self?.handle(line: line, from: .usb) }
        p.onClose = { [weak self] in self?.disconnect(reason: "串口断开") }
        // 刚打开时最先写入的字节可能丢失，所以每 0.5 秒重发一次 hi，3 秒内没有 hello 就放弃这台设备。
        var tries = 0
        handshakeTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] t in
            MainActor.assumeIsolated {
                guard let self else { t.invalidate(); return }
                if self.usbReady { t.invalidate(); return }
                tries += 1
                if tries > 6 {
                    t.invalidate()
                    DebugLog.log("副屏：\(path) 没有回应握手，不是 ServerIsland 副屏固件")
                    self.rejected.insert(path)
                    self.disconnect(reason: "握手超时")
                    return
                }
                self.writeUSB(["t": "hi"])
            }
        }
    }

    private func disconnect(reason: String) {
        if linked { DebugLog.log("副屏：断开（\(reason)）") }
        handshakeTimer?.invalidate()
        port?.onClose = nil
        port?.close()
        port = nil
        usbReady = false
        network = [:]  // 配网状态只来自 USB；改走蓝牙时别显示旧状态
        refreshTransport()
    }

    /// 蓝牙：设置里打开了才创建（会触发系统的蓝牙授权），关了就断开。
    private func updateBluetooth() {
        let want = store.settings.usbDisplay && store.settings.displayBluetooth
        if want, ble == nil {
            let b = BLELink()
            b.onReady = { [weak self] in
                DebugLog.log("副屏蓝牙：通道就绪，握手中")
                guard let self else { return }
                self.bleHandshakeTimer?.invalidate()
                self.writeBLE(["t": "hi"])
                var tries = 0
                self.bleHandshakeTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] timer in
                    MainActor.assumeIsolated {
                        guard let self, !self.bleReady, self.ble != nil else { timer.invalidate(); return }
                        tries += 1
                        // 第一次连接要在 Mac 上输入配对码，给足两分钟。
                        if tries >= 120 { timer.invalidate(); self.ble?.reconnect() }
                        else { self.writeBLE(["t": "hi"]) }
                    }
                }
            }
            b.onLine = { [weak self] line in self?.handle(line: line, from: .ble) }
            b.onDisconnect = { [weak self] in
                self?.bleHandshakeTimer?.invalidate()
                self?.bleReady = false
                self?.refreshTransport()
            }
            ble = b
        } else if !want, let b = ble {
            bleHandshakeTimer?.invalidate()
            b.stop()
            ble = nil
            bleReady = false
            refreshTransport()
        }
    }

    /// 根据两条通道的握手状态决定用哪条；换了通道（或刚连上）就把所有数据重发一遍。
    private func refreshTransport() {
        let now: Transport? = usbReady ? .usb : (bleReady ? .ble : nil)
        guard now != transport else { return }
        transport = now
        linked = now != nil
        guard now != nil else {
            DebugLog.log("副屏：断开")
            firmware = nil
            board = [:]
            network = [:]
            return
        }
        DebugLog.log("副屏：通过\(now == .usb ? "USB" : "蓝牙")连接（固件 \(firmware ?? "?")）")
        write(["t": "getsettings"])
        sendSnapshot()
        sendAIUsage()
        sendAgents()
        sendHistory()
        sendExtras()
        sendScreenState()
    }

    private func handle(line: String, from: Transport) {
        guard line.hasPrefix("{"), let data = line.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        if obj["t"] as? String == "network", from == .usb, usbReady {
            let next = obj.compactMapValues { $0 as? String }
            // 只记状态变化（不含 Wi-Fi 名称和密码），排查配网用。
            if obj["clients"] as? Int != networkClients {
                networkClients = obj["clients"] as? Int
                if let c = networkClients, next["ap_ip"]?.isEmpty == false { DebugLog.log("副屏 Wi-Fi：热点上有 \(c) 台设备") }
            }
            if next["state"] != network["state"] || next["error"] != network["error"] {
                DebugLog.log("副屏 Wi-Fi：\(next["state"] ?? "?")\((next["error"] ?? "").isEmpty ? "" : "（\(next["error"]!)）")\((next["ap_ip"] ?? "").isEmpty ? "" : "，热点已开")")
            }
            network = next
            return
        }
        if obj["t"] as? String == "settings", linked {
            // 板子上的设置（布尔值也转成 0 / 1）。
            var b: [String: Int] = [:]
            for (k, v) in obj where k != "t" {
                if let n = v as? NSNumber { b[k] = n.intValue }
            }
            board = b
            return
        }
        guard obj["t"] as? String == "hello", obj["app"] as? String == "serverisland-display" else { return }
        firmware = obj["fw"] as? String
        switch from {
        case .usb:
            if !usbReady { usbReady = true; handshakeTimer?.invalidate(); refreshTransport(); return }
        case .ble:
            if !bleReady {
                DebugLog.log("副屏蓝牙：握手成功（固件 \(firmware ?? "?")）\(usbReady ? "，插着 USB 时仍走 USB" : "")")
                bleReady = true; bleHandshakeTimer?.invalidate(); refreshTransport(); return
            }
        }
        if from == transport { sendSnapshot() }
    }

    private static func encode(_ obj: [String: Any]) -> Data? {
        guard var data = try? JSONSerialization.data(withJSONObject: obj, options: [.withoutEscapingSlashes]) else { return nil }
        data.append(0x0A)
        return data
    }

    /// 发给副屏：走当前通道。
    private func write(_ obj: [String: Any]) {
        switch transport {
        case .usb: writeUSB(obj)
        case .ble: writeBLE(obj)
        case nil: break
        }
    }

    private func writeUSB(_ obj: [String: Any]) {
        guard let port, let data = Self.encode(obj) else { return }
        port.write(data)
    }

    private func writeBLE(_ obj: [String: Any]) {
        guard let ble, let data = Self.encode(obj) else { return }
        ble.write(data)
    }

    /// 设置页改了板子上的某一项：先在本地显示新值，再发给板子（板子应用后会回一条完整的 settings）。
    func setBoard(_ key: String, _ value: Int) {
        guard linked else { return }
        board[key] = value
        write(["t": "set", key: value])
    }

    /// 校园网凭据只通过 USB 发给板子，不保存进 App 配置或日志。
    func configureCampus(_ fields: [String: Any]) {
        guard usbReady, transport == .usb else { return }
        var message = fields
        message["t"] = "network"
        writeUSB(message)
    }

    private func extrasChanged(_ key: String) {
        let cityChanged = key.split(separator: "|").last.map(String.init) != lastExtrasKey.split(separator: "|").last.map(String.init)
        lastExtrasKey = key
        sendExtras(forceWeather: cityChanged)
    }

    func sendExtras(forceWeather: Bool = false) {
        guard linked else { return }
        write(extras.calendarMessage(settings: store.settings))
        extras.refreshWeather(city: store.settings.weatherCity, force: forceWeather) { [weak self] msg in self?.write(msg) }
    }

    private static func sessionLocked() -> Bool {
        (CGSessionCopyCurrentDictionary() as? [String: Any])?["CGSSessionScreenIsLocked"] as? Bool ?? false
    }

    private func sendScreenState() {
        guard linked else { return }
        write(["t": "screen", "on": !store.settings.displayLockSync || !(macLocked || screenSleeping)])
    }

    // MARK: - 快照

    /// AI 用量（CC Switch 的数据库）：装了 CC Switch 就自动发，不用设置。在后台线程里读，读完回主线程发。
    private func sendAIUsage() {
        guard linked, AIUsage.available else { return }
        Task.detached(priority: .utility) {
            guard let msg = AIUsage.snapshot() else { return }
            await MainActor.run { [weak self] in self?.write(msg) }
        }
    }

    static func toolName(_ tool: String) -> String {
        switch tool {
        case "claude": return "Claude Code"
        case "codex": return "Codex"
        case "opencode": return "OpenCode"
        default: return tool
        }
    }

    private static func project(_ cwd: String) -> String {
        cwd.isEmpty ? "" : (cwd as NSString).lastPathComponent
    }

    private func sendAgents() {
        guard linked else { return }
        let now = Date()
        let list: [[String: Any]] = agents.list.prefix(10).map { s in
            ["tool": s.tool, "proj": Self.project(s.cwd), "title": s.title, "state": s.state.rawValue, "msg": s.message,
             "ago": Int(now.timeIntervalSince(s.changedAt))]
        }
        write(["t": "agents", "list": list])
    }

    private func sendAgentAlert(_ s: AgentSession) {
        guard linked else { return }
        DebugLog.log("副屏 Agent：\(s.tool) \(s.state.rawValue)")
        let name = Self.toolName(s.tool)
        let proj = Self.project(s.cwd)
        let kind: String, title: String, detail: String
        switch s.state {
        case .attention: kind = "attention"; title = L("\(name) 需要你确认", "\(name) needs you"); detail = s.message
        case .error: kind = "failed"; title = L("\(name) 出错了", "\(name) hit an error"); detail = s.title
        default: kind = "agent_done"; title = L("\(name) 回答完了", "\(name) is done"); detail = s.title
        }
        let sub = [proj, detail].filter { !$0.isEmpty }.joined(separator: " · ")
        write(["t": "event", "kind": kind, "title": title, "sub": sub, "host": ""])
        sendAgents()
    }

    /// 最近结束的任务（所有服务器合在一起，新的在前）。
    private func sendHistory() {
        guard linked else { return }
        let now = Date()
        var all: [(HostConfig, HistoryEntry)] = []
        for (host, _) in engine.orderedHosts {
            all += engine.history.entries(for: host.id).map { (host, $0) }
        }
        all.sort { $0.1.endedAt > $1.1.endedAt }
        let list: [[String: Any]] = all.prefix(20).map { host, e in
            var o: [String: Any] = ["name": Self.clip(e.name, 60), "host": host.displayName, "label": e.stateLabel,
                                    "state": e.state == HistoryEntry.endedUnknown ? "other" : e.jobState.rawValue,
                                    "ago": Int(now.timeIntervalSince(e.endedAt))]
            if let el = e.elapsed { o["el"] = Int(el) }
            return o
        }
        write(["t": "history", "list": list])
    }

    private func scheduleSnapshot() {
        guard linked else { return }
        pendingSnap?.cancel()
        let work = DispatchWorkItem { [weak self] in MainActor.assumeIsolated { self?.sendSnapshot() } }
        pendingSnap = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
    }

    private func sendSnapshot() {
        guard linked else { return }
        write(Self.snapshot(engine: engine, store: store))
    }

    /// 每台机器最多带这么多任务（副屏一张卡片显示 4 个，多带几个以后翻页用）。
    static let maxJobs = 8

    static func snapshot(engine: MonitorEngine, store: ConfigStore, now: Date = Date()) -> [String: Any] {
        var hosts: [[String: Any]] = []
        for (host, snap) in engine.orderedHosts {
            var h: [String: Any] = ["name": host.displayName]
            switch snap.health {
            case .ok: h["health"] = "ok"
            case .unknown: h["health"] = "unknown"
            case .needsLogin(let m): h["health"] = "login"; h["msg"] = m
            case .error(let m): h["health"] = "error"; h["msg"] = m
            }
            if host.showCPU, let c = snap.metrics.cpuPercent { h["cpu"] = Int(c.rounded()) }
            if host.showMemory, let f = snap.metrics.memoryFraction { h["mem"] = Int((f * 100).rounded()) }
            if host.showGPU, !snap.gpus.isEmpty {
                h["gpu"] = Int((snap.gpus.map(\.utilization).reduce(0, +) / Double(snap.gpus.count)).rounded())
                // 每张卡：利用率、显存占用、温度（资源页用）。
                h["gpus"] = snap.gpus.prefix(8).map { g -> [String: Any] in
                    var o: [String: Any] = ["u": Int(g.utilization.rounded()), "m": Int((g.memoryFraction * 100).rounded())]
                    if let t = g.temperature { o["t"] = Int(t.rounded()) }
                    return o
                }
            }
            if host.showDisk, !snap.disks.isEmpty {
                h["disks"] = snap.disks.prefix(4).map { ["m": clip($0.mount, 24), "p": Int(($0.fraction * 100).rounded())] }
            }
            let starred = Set(store.settings.starredJobs.filter { $0.hostID == host.id }.map(\.jobID))
            let jobs = MonitorEngine.starredFirst(snap.jobs) { starred.contains($0.id) }
            // 已运行 / 剩余时间是采样时刻的值，按距采样过去的时间修正。
            let age = snap.lastUpdate.map { max(0, now.timeIntervalSince($0)) } ?? 0
            h["jobs"] = jobs.prefix(maxJobs).map { job(job: $0, age: age, starred: starred.contains($0.id)) }
            h["njobs"] = jobs.count
            hosts.append(h)
        }
        let mood: String
        switch engine.petMood {
        case .idle: mood = "idle"
        case .working: mood = "working"
        case .waiting: mood = "waiting"
        case .happy: mood = "happy"
        case .sad: mood = "sad"
        case .worried: mood = "worried"
        }
        // now / tz：副屏的时钟用电脑的时间（UTC 秒 + 时区偏移秒）。
        return ["t": "snap", "lang": Lang.isEnglish ? "en" : "zh", "mood": mood,
                "run": engine.totalRunning, "pend": engine.totalPending, "hosts": hosts,
                "now": Int(now.timeIntervalSince1970), "tz": TimeZone.current.secondsFromGMT(for: now)]
    }

    /// 详情页的文字字段截短，免得一条 snap 太大（副屏的接收缓冲是 32KB）。
    private static func clip(_ s: String, _ n: Int = 120) -> String { s.count > n ? String(s.prefix(n - 1)) + "…" : s }

    private static func job(job j: RemoteJob, age: TimeInterval, starred: Bool) -> [String: Any] {
        var o: [String: Any] = ["id": j.id, "name": clip(j.name, 80), "state": j.state.rawValue]
        if starred { o["star"] = true }
        if let f = j.fraction { o["pct"] = Int((f * 100).rounded()) }
        if j.hasRealProgress { o["real"] = true }
        if let a = j.array { o["array"] = "\(a.done + a.failed)/\(a.total)" }
        if j.state == .running || j.state == .completing {
            if let e = j.eta { o["eta"] = Int(max(0, e - age)) }
            if let e = j.elapsed { o["el"] = Int(e + age) }
        }
        if j.state == .pending, let r = j.reason { o["reason"] = r }
        // 任务详情页用的字段。
        if let l = j.limit, l > 0 { o["limit"] = Int(l) }
        if !j.detail.isEmpty { o["detail"] = clip(j.detail) }
        if let a = j.app {
            o["app"] = a.title
            let summary = a.summary
            if !summary.isEmpty { o["msg"] = clip(summary) }
        } else if let m = j.message, !m.isEmpty {
            o["msg"] = clip(m)
        }
        if let c = j.cpuPercent { o["cpu"] = Int(c.rounded()) }
        if let m = j.memoryBytes { o["memMB"] = Int(m / 1_048_576) }
        if let g = j.gpuMemoryMB { o["gpuMB"] = Int(g) }
        if let d = j.workdir, !d.isEmpty { o["dir"] = clip(d) }
        return o
    }

    /// 当前插着的 Espressif USB 串口（/dev/cu.*）。
    nonisolated static func espressifPorts() -> [String] {
        var iterator: io_iterator_t = 0
        guard let match = IOServiceMatching(kIOSerialBSDServiceValue),
              IOServiceGetMatchingServices(kIOMainPortDefault, match, &iterator) == KERN_SUCCESS else { return [] }
        defer { IOObjectRelease(iterator) }
        var paths: [String] = []
        while case let service = IOIteratorNext(iterator), service != 0 {
            defer { IOObjectRelease(service) }
            guard let path = IORegistryEntryCreateCFProperty(service, kIOCalloutDeviceKey as CFString, kCFAllocatorDefault, 0)?
                    .takeRetainedValue() as? String,
                  let vendor = IORegistryEntrySearchCFProperty(service, kIOServicePlane, "idVendor" as CFString, kCFAllocatorDefault,
                                                                IOOptionBits(kIORegistryIterateRecursively | kIORegistryIterateParents)) as? Int,
                  vendor == espressifVendorID else { continue }
            paths.append(path)
        }
        return paths.sorted()
    }
}

/// 一个打开的串口：后台读取、按行回调；写入在后台队列里排队。
final class SerialPort: @unchecked Sendable {
    let path: String
    private let fd: Int32
    private let queue = DispatchQueue(label: "ServerIsland.serial")
    private var source: DispatchSourceRead?
    private var buffer = Data()
    @MainActor var onLine: ((String) -> Void)?
    @MainActor var onClose: (() -> Void)?

    init?(path: String) {
        self.path = path
        fd = open(path, O_RDWR | O_NOCTTY | O_NONBLOCK)
        guard fd >= 0 else { return nil }
        var t = termios()
        tcgetattr(fd, &t)
        cfmakeraw(&t)
        cfsetspeed(&t, speed_t(B115200))
        t.c_cflag |= tcflag_t(CLOCAL | CREAD)
        tcsetattr(fd, TCSANOW, &t)
        // ESP32-S3 的 USB 串口把"RTS 有效、DTR 无效"当成复位信号。打开时两者都有效，这里只关掉 RTS，
        // 保持 DTR 有效，板子就不会被复位。
        var rts = TIOCM_RTS
        _ = ioctl(fd, TIOCMBIC, &rts)

        let src = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
        src.setEventHandler { [weak self] in self?.readAvailable() }
        source = src
        src.resume()
    }

    func write(_ data: Data) {
        queue.async { [fd] in
            var offset = 0
            let deadline = Date().addingTimeInterval(2)
            data.withUnsafeBytes { raw in
                guard let base = raw.baseAddress else { return }
                while offset < data.count && Date() < deadline {
                    let n = Darwin.write(fd, base + offset, data.count - offset)
                    if n > 0 { offset += n } else if errno == EAGAIN { usleep(2000) } else { break }
                }
            }
        }
    }

    func close() {
        queue.async { [self] in
            source?.cancel()
            source = nil
            Darwin.close(fd)
        }
    }

    private func readAvailable() {
        var chunk = [UInt8](repeating: 0, count: 4096)
        let n = read(fd, &chunk, chunk.count)
        if n <= 0 {
            if n == 0 || (errno != EAGAIN && errno != EINTR) {
                source?.cancel()
                source = nil
                DispatchQueue.main.async { MainActor.assumeIsolated { self.onClose?() } }
            }
            return
        }
        buffer.append(contentsOf: chunk[0..<n])
        while let i = buffer.firstIndex(of: 0x0A) {
            let line = String(decoding: buffer[buffer.startIndex..<i], as: UTF8.self)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            buffer.removeSubrange(buffer.startIndex...i)
            DispatchQueue.main.async { MainActor.assumeIsolated { self.onLine?(line) } }
        }
        // 截图等二进制数据不需要：缓冲过大就丢掉。
        if buffer.count > 64 * 1024 { buffer.removeAll() }
    }
}
