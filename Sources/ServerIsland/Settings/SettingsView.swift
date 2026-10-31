import AppKit
import SwiftUI

@MainActor
final class SettingsWindowController {
    private var window: NSWindow?
    private let store: ConfigStore
    private let engine: MonitorEngine

    init(store: ConfigStore, engine: MonitorEngine) {
        self.store = store
        self.engine = engine
    }

    func show() {
        if window == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 860, height: 620),
                             styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                             backing: .buffered, defer: false)
            w.title = "ServerIsland"
            w.toolbarStyle = .unified
            w.isReleasedWhenClosed = false
            w.minSize = NSSize(width: 760, height: 520)
            w.contentView = NSHostingView(rootView: SettingsView(store: store, engine: engine))
            w.center()
            window = w
            NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { _ in
                MainActor.assumeIsolated { _ = NSApp.setActivationPolicy(.accessory) }
            }
        }
        // 菜单栏 App 打开窗口时需要临时激活。
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

enum SettingsPage: Hashable {
    case general
    case rules
    case display
    case host(UUID)
}

struct SettingsView: View {
    /// 右键菜单：调整服务器的顺序（刘海里也按这个顺序显示）。
    @ViewBuilder
    private func orderMenu(_ id: UUID) -> some View {
        let hosts = store.settings.hosts
        let i = hosts.firstIndex { $0.id == id } ?? 0
        Button(L("移到最上面", "Move to Top")) { moveHost(id, to: 0) }.disabled(i == 0)
        Button(L("上移", "Move Up")) { moveHost(id, to: i - 1) }.disabled(i == 0)
        Button(L("下移", "Move Down")) { moveHost(id, to: i + 1) }.disabled(i >= hosts.count - 1)
        Button(L("移到最下面", "Move to Bottom")) { moveHost(id, to: hosts.count - 1) }.disabled(i >= hosts.count - 1)
    }

    private func moveHost(_ id: UUID, to target: Int) {
        guard let i = store.settings.hosts.firstIndex(where: { $0.id == id }) else { return }
        let h = store.settings.hosts.remove(at: i)
        store.settings.hosts.insert(h, at: min(max(target, 0), store.settings.hosts.count))
    }

    @ObservedObject var store: ConfigStore
    @ObservedObject var engine: MonitorEngine
    @State private var selection: SettingsPage? = .general
    @State private var showingAdd = false

    init(store: ConfigStore, engine: MonitorEngine, initial: SettingsPage = .general) {
        self.store = store
        self.engine = engine
        _selection = State(initialValue: initial)
    }

    var body: some View {
        NavigationSplitView {
            List(selection: $selection) {
                Section {
                    SidebarRow(title: L("通用", "General"), subtitle: L("轮询、提醒、提示音", "Refresh, alerts, sounds"),
                               symbol: "gearshape.fill", tint: .gray)
                        .tag(SettingsPage.general)
                    SidebarRow(title: L("进度识别", "Progress Detection"),
                               subtitle: store.settings.progressRules.isEmpty ? L("内置软件、自定义规则", "Built-in apps, custom rules")
                                   : L("\(store.settings.progressRules.count) 条自定义规则", "\(store.settings.progressRules.count) custom rules"),
                               symbol: "wand.and.stars", tint: .indigo)
                        .tag(SettingsPage.rules)
                    // 外接副屏：只有在通用页底部打开以后才出现。
                    if store.settings.usbDisplay {
                        SidebarRow(title: L("副屏", "Display"),
                                   subtitle: engine.displayLink?.connected == true ? L("已连接", "Connected") : L("ESP32 小屏幕", "ESP32 screen"),
                                   symbol: "display", tint: .teal)
                            .tag(SettingsPage.display)
                    }
                }
                Section {
                    ForEach(store.settings.hosts) { host in
                        HostSidebarRow(host: host, snap: engine.snapshots[host.id])
                            .tag(SettingsPage.host(host.id))
                            .contextMenu { orderMenu(host.id) }
                    }
                    .onMove { from, to in store.settings.hosts.move(fromOffsets: from, toOffset: to) }
                    // 侧栏样式的列表不画 footer，提示放成一行不能选中的小字
                    if store.settings.hosts.count > 1 {
                        Text(L("拖动或右键可调整顺序", "Drag or right-click to reorder"))
                            .font(.caption2).foregroundStyle(.tertiary)
                            .help(L("刘海里也按这个顺序显示", "The island uses the same order"))
                            .selectionDisabled()
                    }
                } header: {
                    Text(L("服务器", "Servers"))
                }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 210, ideal: 230)
            .safeAreaInset(edge: .bottom) {
                VStack(spacing: 6) {
                    Button { showingAdd = true } label: {
                        Label(L("添加服务器", "Add Server"), systemImage: "plus.circle.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .controlSize(.large)
                    .buttonStyle(.borderedProminent)
                    .tint(Color(red: 0.18, green: 0.68, blue: 0.53))
                    // 菜单栏图标可能被隐藏或挤掉，设置窗口里也放一个退出入口
                    Button { NSApp.terminate(nil) } label: {
                        Label(L("退出 ServerIsland", "Quit ServerIsland"), systemImage: "power")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.plain)
                    .help(L("完全退出（刘海和菜单栏图标都会消失）", "Quit completely (the island and menu bar icon go away)"))
                }
                .padding(12)
            }
        } detail: {
            Group {
                switch selection {
                case .host(let id):
                    if let current = store.host(id) {
                        // 按服务器 ID 绑定（而不是数组下标）：切换页面、删除或调整顺序后，异步结果也不会写到别的服务器上。
                        HostDetailView(host: Binding(get: { store.host(id) ?? current },
                                                     set: { new in
                                                         if let i = store.settings.hosts.firstIndex(where: { $0.id == id }) {
                                                             store.settings.hosts[i] = new
                                                         }
                                                     }),
                                       engine: engine,
                                       globalInterval: store.settings.defaultInterval,
                                       allHosts: store.settings.hosts) {
                            store.settings.hosts.removeAll { $0.id == id }
                            // 以它为跳板机的服务器改回直连；记住的密码一并删除。
                            for i in store.settings.hosts.indices where store.settings.hosts[i].jumpHostID == id {
                                store.settings.hosts[i].jumpHostID = nil
                            }
                            Keychain.delete(id)
                            selection = .general
                        }
                        .id(id)
                    } else {
                        GeneralSettingsView(store: store, engine: engine)
                    }
                case .rules:
                    ProgressRulesView(store: store, engine: engine)
                case .display:
                    if store.settings.usbDisplay {
                        DisplaySettingsView(store: store, link: engine.displayLink)
                            .navigationTitle(L("副屏", "Display"))
                    } else {
                        GeneralSettingsView(store: store, engine: engine)
                    }
                default:
                    GeneralSettingsView(store: store, engine: engine)
                }
            }
            // 标题栏用不透明背景：页面往下滚动时内容不会和标题重叠。
            .toolbarBackground(.visible, for: .windowToolbar)
        }
        .sheet(isPresented: $showingAdd) {
            AddHostSheet(existing: store.settings.hosts) { newHosts in
                store.settings.hosts.append(contentsOf: newHosts)
                if let first = newHosts.first { selection = .host(first.id) }
            }
        }
        .onChange(of: store.settings.hosts) { _, _ in engine.sync() }
        // 关掉"外接副屏"时，副屏页从侧栏消失：回到通用页。
        .onChange(of: store.settings.usbDisplay) { _, on in if !on && selection == .display { selection = .general } }
        .frame(minWidth: 760, minHeight: 520)
        // 切换语言后整页重绘。
        .id(store.settings.language)
    }
}

struct SidebarRow: View {
    var title: String
    var subtitle: String
    var symbol: String
    var tint: Color

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 26, height: 26)
                .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(tint.gradient))
            VStack(alignment: .leading, spacing: 1) {
                Text(title).lineLimit(1)
                Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(.vertical, 2)
    }
}

struct HostSidebarRow: View {
    var host: HostConfig
    var snap: HostSnapshot?

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: host.kind.isScheduler ? "square.stack.3d.up.fill" : "server.rack")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 26, height: 26)
                .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill((host.kind == .slurm ? Color.purple : host.kind == .pbs ? Color.indigo : Color.teal).gradient))
                .overlay(alignment: .bottomTrailing) {
                    Circle()
                        .fill(host.enabled ? Theme.color(for: snap?.health ?? .unknown) : Color.gray)
                        .frame(width: 9, height: 9)
                        .overlay(Circle().strokeBorder(Color(nsColor: .windowBackgroundColor), lineWidth: 1.5))
                        .offset(x: 3, y: 3)
                }
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 4) {
                    Text(host.displayName).lineLimit(1)
                    if host.muted { Image(systemName: "bell.slash.fill").font(.system(size: 9)).foregroundStyle(.secondary) }
                    if host.paused { Image(systemName: "pause.fill").font(.system(size: 9)).foregroundStyle(.secondary) }
                }
                Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(.vertical, 2)
    }

    private var subtitle: String {
        guard host.enabled else { return L("已停用", "Disabled") }
        if host.paused { return L("已暂停", "Paused") }
        switch snap?.health ?? .unknown {
        case .ok:
            let r = snap?.runningCount ?? 0, p = snap?.pendingCount ?? 0
            return r + p == 0 ? host.kind.label + L(" · 空闲", " · idle") : L("运行 \(r) · 排队 \(p)", "\(r) running · \(p) pending")
        case .needsLogin: return L("需要登录", "Login required")
        case .error(let m): return m
        case .unknown: return L("连接中…", "Connecting…")
        }
    }
}

// MARK: - 通用

/// 项目的网址。
enum AppLinks {
    static let repo = URL(string: "https://github.com/zhyan0603/ServerIsland")!
    static let issues = URL(string: "https://github.com/zhyan0603/ServerIsland/issues")!
}

extension GeneralSettingsView {
    /// 检查更新：按钮 + 状态；有新版本时显示"更新"（自动下载、替换、重新打开）和更新说明。
    var updateRow: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(.blue)
            VStack(alignment: .leading, spacing: 2) {
                Text(updateTitle)
                if case .installFailed(_, let why) = updates.state {
                    Text(why).font(.caption).foregroundStyle(Theme.coral)
                } else if let r = updates.state.release, !r.notes.isEmpty {
                    Text(r.notes).font(.caption).foregroundStyle(.secondary).lineLimit(4)
                } else if let t = updates.lastChecked {
                    Text(L("上次检查：", "Last checked: ") + Format.relative(t)).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()
            switch updates.state {
            case .available(let r):
                Button(L("更新到 v\(r.version)", "Update to v\(r.version)")) { Task { await updates.install(r) } }
                    .buttonStyle(.borderedProminent)
                    .help(L("下载并核对新版本，替换后自动重新打开", "Download and verify the new version, replace this app and reopen it"))
                Button(L("更新说明", "Release Notes")) { NSWorkspace.shared.open(r.pageURL) }
            case .downloading, .installing:
                ProgressView().controlSize(.small)
            case .installFailed(let r, _):
                Button(L("下载 v\(r.version)", "Download v\(r.version)")) { NSWorkspace.shared.open(r.downloadURL) }
                    .buttonStyle(.borderedProminent)
                Button(L("更新说明", "Release Notes")) { NSWorkspace.shared.open(r.pageURL) }
            default:
                Button(updates.state == .checking ? L("检查中…", "Checking…") : L("检查更新", "Check for Updates")) {
                    Task { await updates.check() }
                }
                .disabled(updates.state == .checking)
            }
        }
    }

    private var updateTitle: String {
        let current = UpdateChecker.currentVersion ?? "dev"
        switch updates.state {
        case .idle: return L("当前版本 ", "Current version ") + current
        case .checking: return L("正在检查 GitHub 上的最新版本…", "Checking GitHub for the latest version…")
        case .upToDate(let v): return L("已是最新版本（\(v)）", "You're up to date (\(v))")
        case .available(let r): return L("有新版本 v\(r.version)（当前 \(current)）", "Version \(r.version) is available (you have \(current))")
        case .failed(let m): return L("检查失败：", "Check failed: ") + m
        case .downloading(let r):
            let size = r.size.map { "（\(Format.bytes(Double($0)))）" } ?? ""
            return L("正在下载并核对 v\(r.version)\(size)…", "Downloading and verifying v\(r.version)\(size)…")
        case .installing(let r): return L("v\(r.version) 已准备好，马上重新打开…", "v\(r.version) is ready; reopening…")
        case .installFailed(let r, _): return L("没能自动更新到 v\(r.version)", "Couldn't update to v\(r.version) automatically")
        }
    }
}

struct GeneralSettingsView: View {
    @ObservedObject var store: ConfigStore
    @ObservedObject var engine: MonitorEngine
    @State private var confirmClearLog = false
    @State private var confirmClearHistory = false
    @State private var historyCleared = false
    @ObservedObject private var updates = UpdateChecker.shared
    @State private var logCleared = false
    /// 读好、等用户确认怎么导入的设置。
    @State private var pendingImport: AppSettings?
    @State private var transferNote = ""

    /// "显示在"的选项：自动、主屏幕、每块已连接的屏幕；选过但现在没连接的屏幕也列出来（标"未连接"）。
    private var screenChoices: [(value: String, label: String)] {
        var r: [(value: String, label: String)] = [("", L("自动", "Automatic")), ("primary", L("主屏幕（菜单栏所在）", "Main Screen (with menu bar)"))]
        let names = NSScreen.screens.map(\.localizedName)
        r += names.map { ($0, $0) }
        let saved = store.settings.islandScreen
        if !saved.isEmpty, saved != "primary", !names.contains(saved) {
            r.append((saved, saved + L("（未连接）", " (not connected)")))
        }
        return r
    }

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable().frame(width: 58, height: 58)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("ServerIsland").font(.system(size: 20, weight: .bold, design: .rounded))
                        Text(L("版本 ", "Version ") + (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"))
                            .font(.caption).foregroundStyle(.secondary)
                        Text(L("\(store.settings.hosts.count) 台服务器 · 运行 \(engine.totalRunning) · 排队 \(engine.totalPending)",
                               "\(store.settings.hosts.count) servers · \(engine.totalRunning) running · \(engine.totalPending) pending"))
                            .font(.callout).foregroundStyle(.secondary)
                    }
                    Spacer()
                    PetView(mood: engine.petMood, size: 40)
                }
                .padding(.vertical, 4)
                Picker(L("语言", "Language"), selection: $store.settings.language) {
                    ForEach(AppLanguage.allCases) { Text($0.label).tag($0) }
                }
                // 开源项目：请用户点 Star / 反馈问题
                HStack(spacing: 10) {
                    Image(systemName: "star.fill").foregroundStyle(.yellow)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(L("喜欢 ServerIsland？", "Enjoying ServerIsland?"))
                        Text(L("它是免费的开源软件，在 GitHub 上点个 Star 能让更多人看到它。",
                               "It's free and open source — a star on GitHub helps others find it."))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button {
                        NSWorkspace.shared.open(AppLinks.repo)
                    } label: {
                        Label(L("点个 Star", "Star on GitHub"), systemImage: "star")
                    }
                    Button(L("反馈问题", "Report an Issue")) {
                        NSWorkspace.shared.open(AppLinks.issues)
                    }
                }
                updateRow
                Toggle(isOn: $store.settings.autoCheckUpdates) {
                    HelpLabel(L("自动检查更新", "Check for Updates Automatically"),
                              help: L("启动时和之后每天一次，读取 GitHub 上最新的发布版本号（只读，不上传任何信息）。有新版本时在刘海里提醒一次。",
                                      "At launch and once a day, reads the latest release number from GitHub (read-only; nothing is sent). You'll get one notch alert per new version."))
                }
            }

            Section {
                Toggle(isOn: Binding(get: { store.settings.autoRefresh }, set: { engine.setAutoRefresh($0) })) {
                    HelpLabel(L("自动刷新", "Auto Refresh"),
                              help: L("关闭后只有点击刷新按钮时才会连接服务器。", "When off, servers are only contacted when you click Refresh."))
                }
                IntervalPicker(title: L("默认轮询间隔", "Default Interval"),
                               seconds: Binding(get: { store.settings.defaultInterval },
                                                set: { store.settings.defaultInterval = $0 ?? 60 }),
                               allowsInherit: false)
                    .disabled(!store.settings.autoRefresh)
            } header: { SettingsHeader(title: L("轮询", "Refresh"), symbol: "arrow.triangle.2.circlepath", tint: .blue) }

            Section {
                Picker(L("收起时", "When Collapsed"), selection: $store.settings.collapsedStyle) {
                    ForEach(CollapsedStyle.allCases) { Text($0.label).tag($0) }
                }
                Picker(selection: $store.settings.islandScreen) {
                    ForEach(screenChoices, id: \.value) { Text($0.label).tag($0.value) }
                } label: {
                    HelpLabel(L("显示在", "Show On"),
                              help: L("灵动岛放在哪块屏幕上。自动：有刘海的屏幕优先，其次是内建屏幕，再其次是主屏幕（菜单栏所在的那块）。选了某块显示器但它没连接时，按自动处理。",
                                      "Which screen the island lives on. Automatic: the screen with a notch, then the built-in screen, then the main screen (the one with the menu bar). If the chosen display isn't connected, Automatic is used."))
                }
                Toggle(isOn: $store.settings.foldIdleHosts) {
                    HelpLabel(L("合并空闲的服务器", "Group Idle Servers"),
                              help: L("展开面板里，没有任务的服务器合并成一行，点击可以展开。全部空闲时照常显示。",
                                      "Servers with no jobs are grouped into one line you can click to expand. All cards show when everything is idle."))
                }
            } header: { SettingsHeader(title: L("刘海", "Island"), symbol: "rectangle.topthird.inset.filled", tint: .indigo) }

            Section {
                Toggle(L("在刘海弹出提示", "Island Pop-ups"), isOn: $store.settings.islandAlerts)
                Toggle(L("系统通知", "System Notifications"), isOn: $store.settings.systemNotifications)
                Picker(selection: $store.settings.alertScope) {
                    ForEach(AlertScope.allCases) { Text($0.label).tag($0) }
                } label: {
                    HelpLabel(L("提醒哪些任务", "Alert For"),
                              help: L("关注任务：在刘海的任务行上点 ☆，或在详情页点星标。关注的任务结束后自动取消关注。手动监听的进程照常提醒；磁盘等服务器提醒不受影响。",
                                      "Star a job with the ☆ on its row or on its details page. Stars are removed when the job ends. Watched processes still alert; disk and other server alerts are not affected."))
                }
                LabeledContent {
                    HStack(spacing: 14) {
                        Toggle(L("开始运行", "Started"), isOn: $store.settings.alertStarted)
                        Toggle(L("完成", "Finished"), isOn: $store.settings.alertFinished)
                        Toggle(L("失败", "Failed"), isOn: $store.settings.alertFailed)
                        Toggle(L("结果未知", "Unknown"), isOn: $store.settings.alertUnknown)
                    }
                    .toggleStyle(.checkbox)
                } label: {
                    HelpLabel(L("提醒这些事件", "Alert When"),
                              help: L("开始运行：排队的作业开始运行（不响提示音）。结果未知：作业离开了队列，但查不到成功还是失败（例如集群没有开启作业记录）。",
                                      "Started: a pending job begins running (no sound). Unknown: a job left the queue but its result couldn't be found (e.g. no job accounting)."))
                }
                Toggle(isOn: $store.settings.quietHours) {
                    HelpLabel(L("勿扰时段", "Quiet Hours"),
                              help: L("这段时间里不响提示音、不在刘海弹窗；系统通知照发但不出声，历史记录照常。",
                                      "During these hours there are no sounds or island pop-ups; system notifications still arrive silently and history is kept as usual."))
                }
                if store.settings.quietHours {
                    LabeledContent(L("时间", "Time")) {
                        HStack(spacing: 6) {
                            QuietTimePicker(minutes: $store.settings.quietStart)
                            Text(L("至", "to")).foregroundStyle(.secondary)
                            QuietTimePicker(minutes: $store.settings.quietEnd)
                            if store.settings.quietStart > store.settings.quietEnd {
                                Text(L("（次日）", "(next day)")).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
                Toggle(isOn: $store.settings.arrayFirstFailureAlert) {
                    HelpLabel(L("数组作业首个失败立即提醒", "Alert on First Array Failure"),
                              help: L("数组作业平时只在全部结束时提醒一次；打开后第一个元素失败时会额外提醒一次。",
                                      "Job arrays normally alert once when all elements finish; this adds one alert when the first element fails."))
                }
                Picker(selection: $store.settings.diskWarnThreshold) {
                    ForEach([0.8, 0.85, 0.9, 0.95], id: \.self) { Text("\(Int($0 * 100))%").tag($0) }
                } label: {
                    HelpLabel(L("磁盘提醒阈值", "Disk Alert Threshold"),
                              help: L("超过时提醒一次；回落后再次超过会重新提醒。不受“提醒哪些任务”影响。", "Alerts once when exceeded, and again after it drops and rises again. Not affected by Alert For."))
                }
            } header: { SettingsHeader(title: L("提醒", "Alerts"), symbol: "bell.badge.fill", tint: .orange) } footer: {
                Text(L("静音的服务器（卡片上的铃铛）什么都不提醒，优先于这里的设置。", "Muted servers (the bell on a card) never alert, whatever is set here."))
                    .font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
                Toggle(L("提示音", "Sounds"), isOn: $store.settings.soundEnabled)
                HStack {
                    Image(systemName: "speaker.fill").foregroundStyle(.secondary)
                    Slider(value: $store.settings.soundVolume, in: 0.1...1)
                    Image(systemName: "speaker.wave.3.fill").foregroundStyle(.secondary)
                }
                .disabled(!store.settings.soundEnabled)
                HStack(spacing: 10) {
                    Text(L("试听", "Preview"))
                    Spacer()
                    Button { SoundPlayer.shared.preview(.success, volume: store.settings.soundVolume) } label: {
                        Label(L("成功", "Success"), systemImage: "checkmark.circle.fill")
                    }
                    Button { SoundPlayer.shared.preview(.failure, volume: store.settings.soundVolume) } label: {
                        Label(L("失败", "Failure"), systemImage: "xmark.circle.fill")
                    }
                    Button { SoundPlayer.shared.preview(.warning, volume: store.settings.soundVolume) } label: {
                        Label(L("提醒", "Warning"), systemImage: "exclamationmark.circle.fill")
                    }
                }
            } header: { SettingsHeader(title: L("提示音", "Sounds"), symbol: "speaker.wave.2.fill", tint: .pink) }

            Section {
                LaunchAtLoginToggle()
            } header: { SettingsHeader(title: L("启动", "Startup"), symbol: "power", tint: .green) }

            Section {
                Text(L("训练脚本往服务器的 ~/.serverisland/progress/<名称>.json 写一行 JSON，刘海里就能显示真实进度：",
                       "Have your script write one line of JSON to ~/.serverisland/progress/<name>.json on the server to show real progress:"))
                    .font(.callout)
                Text(#"{"name": "train", "progress": 0.42, "message": "epoch 3/10", "jobid": "$SLURM_JOB_ID"}"#)
                    .font(.system(.callout, design: .monospaced))
                    .textSelection(.enabled)
            } header: {
                HStack {
                    SettingsHeader(title: L("进度文件", "Progress Files"), symbol: "chart.bar.fill", tint: .teal)
                    HelpLabel("", help: L("写了 jobid 的条目会合并到对应作业上（数组元素写 主编号_序号）。仓库里的 remote/si-progress 是现成的小工具。",
                                          "Entries with a jobid are merged into that job (array elements use base_index). remote/si-progress in the repo is a ready-made helper."))
                }
            }

            Section {
                LabeledContent(L("配置文件", "Config File")) {
                    Text(ConfigStore.fileURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                        .font(.system(.callout, design: .monospaced)).textSelection(.enabled)
                }
                LabeledContent(L("诊断日志", "Diagnostic Log")) {
                    Text(DebugLog.url.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                        .font(.system(.callout, design: .monospaced)).textSelection(.enabled)
                }
                Picker(selection: $store.settings.logRetentionDays) {
                    ForEach([1, 3, 7, 14, 30, 0], id: \.self) { d in
                        Text(d == 0 ? L("永久保留", "Forever") : L("\(d) 天", d == 1 ? "1 day" : "\(d) days")).tag(d)
                    }
                } label: {
                    HelpLabel(L("日志保留", "Keep Logs For"),
                              help: L("诊断日志里早于这个天数的记录会自动删除（启动时和之后每 6 小时检查一次）。日志只在本机，用来排查“为什么没提醒”之类的问题。",
                                      "Diagnostic log entries older than this are deleted automatically (at launch and every 6 hours). The log stays on this Mac and helps answer questions like “why was there no alert?”."))
                }
                .onChange(of: store.settings.logRetentionDays) { _, d in DebugLog.prune(days: d) }
                LabeledContent {
                    HStack {
                        if !transferNote.isEmpty { Text(transferNote).font(.caption).foregroundStyle(.secondary) }
                        Button(L("导出设置…", "Export Settings…"), action: exportSettings)
                        Button(L("导入设置…", "Import Settings…"), action: chooseImport)
                            .confirmationDialog(L("导入设置", "Import Settings"), isPresented: Binding(get: { pendingImport != nil }, set: { if !$0 { pendingImport = nil } })) {
                                if let imp = pendingImport {
                                    Button(L("替换全部设置（\(imp.hosts.count) 台服务器）", "Replace All Settings (\(imp.hosts.count) servers)")) { applyImport(imp, .replace) }
                                    let n = ConfigTransfer.newHosts(current: store.settings, imported: imp).count
                                    Button(L("只添加新的服务器（\(n) 台）", "Only Add New Servers (\(n))")) { applyImport(imp, .addHosts) }
                                        .disabled(n == 0)
                                }
                                Button(L("取消", "Cancel"), role: .cancel) { pendingImport = nil }
                            } message: {
                                Text(L("替换前会把当前设置备份到配置文件旁边。密码不在设置文件里，需要时重新登录即可。",
                                       "Your current settings are backed up next to the config file before replacing. Passwords aren't in the file; log in again if asked."))
                            }
                    }
                } label: {
                    HelpLabel(L("导入 / 导出", "Import / Export"),
                              help: L("把全部设置（服务器、提醒、进度识别规则……）存成一个 JSON 文件，换电脑或在 Mac 和 Windows / Linux 版之间迁移。不包含密码。~/.ssh/config 里的别名和私钥路径要在新电脑上也有效。",
                                      "Save all settings (servers, alerts, progress rules…) to a JSON file to move to another computer, or between the Mac and Windows / Linux versions. Passwords aren't included. ~/.ssh/config aliases and key paths must also work on the new computer."))
                }
                HStack {
                    Button(L("显示配置文件", "Show Config")) {
                        store.saveNow()
                        NSWorkspace.shared.activateFileViewerSelecting([ConfigStore.fileURL])
                    }
                    Button(L("打开诊断日志", "Open Log")) { NSWorkspace.shared.open(DebugLog.url) }
                    Button(L("清除日志…", "Clear Log…")) { confirmClearLog = true }
                        .confirmationDialog(L("清除全部诊断日志？", "Clear the whole diagnostic log?"), isPresented: $confirmClearLog) {
                            Button(L("清除", "Clear"), role: .destructive) {
                                DebugLog.clear { logCleared = true }
                            }
                        } message: {
                            Text(L("当前 \(Format.bytes(Double(DebugLog.size)))。不影响设置和任务历史。", "Currently \(Format.bytes(Double(DebugLog.size))). Settings and job history are kept."))
                        }
                    if logCleared { Text(L("已清除", "Cleared")).font(.caption).foregroundStyle(.secondary) }
                    Button(L("清除历史记录…", "Clear History…")) { confirmClearHistory = true }
                        .confirmationDialog(L("清除全部历史记录？", "Clear all job history?"), isPresented: $confirmClearHistory) {
                            Button(L("清除", "Clear"), role: .destructive) {
                                engine.history.clear()
                                historyCleared = true
                            }
                        } message: {
                            Text(L("本机记下的 \(engine.history.count) 条已结束任务（最多保留 7 天）都会删除。不影响设置和日志；集群上 sacct 的记录不受影响。",
                                   "All \(engine.history.count) finished jobs recorded on this Mac (kept up to 7 days) will be deleted. Settings and logs are kept; sacct records on the cluster are not affected."))
                        }
                    if historyCleared { Text(L("已清除", "Cleared")).font(.caption).foregroundStyle(.secondary) }
                    Spacer()
                    Button(L("立即刷新全部", "Refresh All")) { engine.refreshAll() }
                }
            } header: { SettingsHeader(title: L("文件与诊断", "Files & Diagnostics"), symbol: "folder.fill", tint: .gray) }

            Section {
                Toggle(isOn: $store.settings.usbDisplay) {
                    HelpLabel(L("外接副屏（ESP32）", "External Display (ESP32)"),
                              help: L("把任务状态、AI 编程工具的提醒显示在一块刷了 ServerIsland 固件的 ESP32 小屏幕上（固件暂未公开）。打开后侧栏多一个\"副屏\"页面。没有这种屏幕就不用管它。",
                                      "Mirror job status and AI coding-tool alerts on a small ESP32 screen running the ServerIsland firmware (not yet public). Turning this on adds a Display page to the sidebar. Ignore it if you don't have one."))
                }
            } header: { SettingsHeader(title: L("扩展", "Extensions"), symbol: "puzzlepiece.extension.fill", tint: .teal) }
        }
        .formStyle(.grouped)
        .navigationTitle(L("通用", "General"))
    }

    private func exportSettings() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = ConfigTransfer.defaultFileName()
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try ConfigTransfer.exportData(store.settings).write(to: url, options: .atomic)
            transferNote = L("已导出", "Exported")
            DebugLog.log("导出设置：\(url.lastPathComponent)")
        } catch {
            transferNote = L("导出失败：", "Export failed: ") + error.localizedDescription
        }
    }

    private func chooseImport() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            pendingImport = try ConfigTransfer.read(Data(contentsOf: url))
            transferNote = ""
        } catch ConfigTransfer.Failure.notSettings {
            transferNote = L("这不是 ServerIsland 的设置文件", "Not a ServerIsland settings file")
        } catch {
            transferNote = L("读不出这个文件", "Couldn't read this file")
        }
    }

    private func applyImport(_ imported: AppSettings, _ mode: ConfigTransfer.Mode) {
        pendingImport = nil
        if mode == .replace, store.persist { _ = ConfigTransfer.backupCurrent(store.settings, to: ConfigStore.directory) }
        let before = store.settings.hosts.count
        store.settings = ConfigTransfer.apply(current: store.settings, imported: imported, mode: mode)
        engine.sync()
        transferNote = mode == .replace ? L("已导入 \(store.settings.hosts.count) 台服务器", "Imported \(store.settings.hosts.count) servers")
                                        : L("已添加 \(store.settings.hosts.count - before) 台服务器", "Added \(store.settings.hosts.count - before) servers")
        DebugLog.log("导入设置（\(mode == .replace ? "替换" : "只添加服务器")）")
    }
}

/// 勿扰时段的起止时间：每半小时一档。
struct QuietTimePicker: View {
    @Binding var minutes: Int

    var body: some View {
        Picker("", selection: $minutes) {
            ForEach(Array(stride(from: 0, to: 24 * 60, by: 30)), id: \.self) { Text(AppSettings.clock($0)).tag($0) }
        }
        .labelsHidden()
        .fixedSize()
    }
}
