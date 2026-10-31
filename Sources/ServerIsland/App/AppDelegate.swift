import AppKit
import SwiftUI
import UserNotifications

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let demo: Bool
    private var store: ConfigStore!
    private var engine: MonitorEngine!
    private var logPruneTimer: Timer?
    private var updateTimer: Timer?
    private var island: IslandController!
    private var settings: SettingsWindowController!
    private var statusItem: NSStatusItem!

    init(demo: Bool) {
        self.demo = demo
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // ⓘ 和按钮的悬停说明：系统默认要停一秒多才出来，改成 0.25 秒（只影响本 App）
        UserDefaults.standard.register(defaults: ["NSInitialToolTipDelay": 250])
        store = ConfigStore(persist: !demo)
        if demo { store.settings = DemoData.settings() }
        NSScreen.islandPreference = store.settings.islandScreen
        engine = MonitorEngine(store: store, demo: demo)
        settings = SettingsWindowController(store: store, engine: engine)
        island = IslandController(engine: engine, store: store) { [weak self] in self?.settings.show() }
        engine.onEvent = { [weak self] e in self?.postNotification(e) }
        engine.displayLink = DisplayLink(engine: engine, store: store)
        // 按设置的天数清理诊断日志：启动时一次，之后每 6 小时一次。
        DebugLog.prune(days: store.settings.logRetentionDays)
        logPruneTimer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { if let self { DebugLog.prune(days: self.store.settings.logRetentionDays) } }
        }
        // 自动检查更新：启动 20 秒后一次，之后每 24 小时一次（演示模式不检查）。
        if !demo {
            DispatchQueue.main.asyncAfter(deadline: .now() + 20) { [weak self] in self?.autoCheckForUpdates() }
            updateTimer = Timer.scheduledTimer(withTimeInterval: 24 * 3600, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.autoCheckForUpdates() }
            }
        }

        setupStatusItem()
        island.show()
        engine.start()
        engine.displayLink?.start()

        if store.settings.systemNotifications && !demo {
            UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
        }
        // 第一次启动：直接打开设置，引导添加服务器。
        if store.settings.hosts.isEmpty && !demo {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { self.settings.show() }
        }
        // 配置文件损坏：在刘海里说明一次（原文件已备份）。
        if let problem = store.loadProblem {
            DebugLog.log(problem)
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
                self?.engine.emit(.init(kind: .warning, title: L("设置文件读取失败", "Couldn't read settings"),
                                        subtitle: problem, hostName: "ServerIsland"))
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationWillTerminate(_ notification: Notification) {
        store.saveNow()
    }

    // MARK: - 菜单栏图标

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            if let image = Bundle.main.image(forResource: "menubar") {
                image.isTemplate = true
                image.size = NSSize(width: 18, height: 18)
                button.image = image
            } else {
                button.image = NSImage(systemSymbolName: "server.rack", accessibilityDescription: "ServerIsland")
            }
        }
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let hosts = engine.orderedHosts
        if hosts.isEmpty {
            menu.addItem(withTitle: L("还没有添加服务器", "No servers yet"), action: nil, keyEquivalent: "")
        }
        for (host, snap) in hosts {
            let status: String
            switch snap.health {
            case .ok: status = L("运行 \(snap.runningCount) · 排队 \(snap.pendingCount)", "\(snap.runningCount) running · \(snap.pendingCount) pending")
            case .needsLogin(let m): status = m
            case .error(let m): status = m
            case .unknown: status = L("连接中…", "Connecting…")
            }
            let dot: String = {
                switch snap.health {
                case .ok: return "🟢"
                case .needsLogin: return "🟡"
                case .error: return "🔴"
                case .unknown: return "⚪️"
                }
            }()
            let item = NSMenuItem(title: "\(dot)  \(host.displayName) — \(status)", action: nil, keyEquivalent: "")
            menu.addItem(item)
        }
        menu.addItem(.separator())
        if case .available(let r) = UpdateChecker.shared.state {
            menu.addItem(item(L("更新到 v\(r.version)…", "Update to v\(r.version)…"), #selector(downloadUpdate), ""))
            menu.addItem(.separator())
        }
        menu.addItem(item(L("立即刷新", "Refresh Now"), #selector(refresh), "r"))
        menu.addItem(item(L("设置…", "Settings…"), #selector(openSettings), ","))
        menu.addItem(.separator())
        menu.addItem(item(L("退出 ServerIsland", "Quit ServerIsland"), #selector(quit), "q"))
    }

    private func item(_ title: String, _ action: Selector, _ key: String) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
        i.target = self
        return i
    }

    @objc private func refresh() { engine.refreshAll() }
    /// 菜单里的"更新到 v…"：打开设置的通用页显示进度，同时开始自动更新。
    @objc private func downloadUpdate() {
        guard case .available(let r) = UpdateChecker.shared.state else { return }
        openSettings()
        Task { await UpdateChecker.shared.install(r) }
    }

    /// 后台检查更新：有新版本时在刘海里提醒一次（同一个版本只提醒一次），菜单里多一项"下载新版本"。
    private func autoCheckForUpdates() {
        guard store.settings.autoCheckUpdates else { return }
        Task { @MainActor in
            guard let r = await UpdateChecker.shared.check() else { return }
            let key = "notifiedUpdateVersion"
            guard UserDefaults.standard.string(forKey: key) != r.version else { return }
            UserDefaults.standard.set(r.version, forKey: key)
            engine.emit(.init(kind: .info, title: L("ServerIsland 有新版本 v\(r.version)", "ServerIsland v\(r.version) is available"),
                              subtitle: L("在菜单栏图标或 设置 → 通用 里一键更新", "Update from the menu bar icon or Settings → General"),
                              hostName: "ServerIsland"))
        }
    }
    @objc private func openSettings() { settings.show() }
    @objc private func quit() { NSApp.terminate(nil) }

    // MARK: - 系统通知

    private func postNotification(_ event: IslandEvent) {
        guard store.settings.systemNotifications, !demo, event.kind != .started else { return }
        let content = UNMutableNotificationContent()
        content.title = event.title
        content.subtitle = event.hostName
        content.body = event.subtitle
        // 已经播放了自己的提示音时，系统通知就不再响；勿扰时段里也不响。
        content.sound = store.settings.soundEnabled || store.settings.isQuiet() ? nil : (event.kind == .failed ? .defaultCritical : .default)
        let req = UNNotificationRequest(identifier: event.id.uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(req) { _ in }
    }
}
