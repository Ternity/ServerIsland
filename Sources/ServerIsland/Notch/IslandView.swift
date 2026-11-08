import SwiftUI

@MainActor
final class IslandViewModel: ObservableObject {
    @Published var hovering = false
    @Published var geometry = NotchGeometry.current()
    @Published var page: IslandPage = .list
    /// 固定：鼠标移开也不收起。
    @Published var pinned = false
    /// 合并的空闲机器是否临时展开。
    @Published var showIdle = false
    var onUnpin: () -> Void = {}
    /// 打开详情时的任务快照（任务结束后仍能看到详情）。
    var pinnedJob: RemoteJob?
    /// 全部任务页的筛选条件（打开详情再返回时保留）。
    @Published var jobsFilter = JobsFilter()
    /// 详情页"返回"回到哪里（从全部任务页打开时回到那里）。
    var returnPage: IslandPage?

    /// 打开全部任务页：从卡片进来时只看那台服务器，从头部进来时看全部。
    func openAllJobs(host: UUID? = nil) {
        jobsFilter = JobsFilter(host: host)
        page = .jobs
    }
    var openSettings: () -> Void = {}

    func contentHeight(_ hosts: [(HostConfig, HostSnapshot)], fold: Bool) -> CGFloat {
        page == .list ? IslandLayout.contentHeight(hosts, fold: fold, showIdle: showIdle) : IslandLayout.pageHeight
    }
}

struct IslandRootView: View {
    @ObservedObject var vm: IslandViewModel
    @ObservedObject var engine: MonitorEngine
    @ObservedObject var store: ConfigStore

    private var mode: IslandMode {
        IslandLayout.mode(hovering: vm.hovering, engine: engine, style: store.settings.collapsedStyle)
    }

    var body: some View {
        let mode = self.mode
        let hosts = engine.orderedHosts
        let contentHeight = vm.contentHeight(hosts, fold: store.settings.foldIdleHosts)
        let spec = IslandLayout.spec(mode: mode, geometry: vm.geometry, contentHeight: contentHeight)
        let shape = NotchShape(topRadius: spec.topRadius, bottomRadius: spec.bottomRadius)

        ZStack(alignment: .top) {
            shape
                .fill(Color.black)
                .overlay(
                    // 展开时沿边缘一圈极淡的高光，深色壁纸上也能看清轮廓。
                    shape.stroke(LinearGradient(colors: [Color.white.opacity(0), Color.white.opacity(0.10)],
                                                startPoint: .top, endPoint: .bottom), lineWidth: 1)
                        .opacity(mode == .expanded || mode == .toast ? 1 : 0)
                )
                .shadow(color: .black.opacity(mode == .expanded ? 0.55 : (mode == .toast ? 0.35 : 0)),
                        radius: mode == .expanded ? 22 : 12, y: 8)

            ZStack(alignment: .top) {
                switch mode {
                case .hidden:
                    EmptyView()
                case .collapsed:
                    CollapsedWings(engine: engine, geometry: vm.geometry)
                        .transition(.opacity)
                case .toast:
                    if let event = engine.currentEvent {
                        ToastContent(event: event, geometry: vm.geometry)
                            .id(event.id)
                            .transition(.asymmetric(insertion: .opacity.animation(.easeOut(duration: 0.25).delay(0.12)),
                                                    removal: .opacity.animation(.easeIn(duration: 0.12))))
                    }
                case .expanded:
                    ExpandedContent(engine: engine, vm: vm, store: store, hosts: hosts, scrolls: spec.scrolls,
                                    contentHeight: min(contentHeight, IslandLayout.maxContentHeight))
                        .transition(.asymmetric(
                            insertion: .opacity.combined(with: .scale(scale: 0.96, anchor: .top))
                                .animation(.easeOut(duration: 0.28).delay(0.1)),
                            removal: .opacity.animation(.easeIn(duration: 0.1))))
                }
            }
            .padding(.horizontal, spec.topRadius)
            .frame(width: spec.size.width, height: spec.size.height, alignment: .top)
            .clipShape(shape)
        }
        .frame(width: spec.size.width, height: spec.size.height)
        .opacity(mode == .hidden ? 0 : 1)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .animation(.spring(response: 0.44, dampingFraction: 0.78), value: spec)
        .animation(.easeOut(duration: 0.2), value: mode)
        .environment(\.colorScheme, .dark)
    }
}

// MARK: - 收起：刘海两侧的小翅膀

struct CollapsedWings: View {
    @ObservedObject var engine: MonitorEngine
    var geometry: NotchGeometry

    var body: some View {
        let running = engine.totalRunning
        let pending = engine.totalPending
        HStack(spacing: 0) {
            HStack(spacing: 5) {
                PetView(mood: engine.petMood, size: 17)
                if engine.hasProblem && running == 0 {
                    Image(systemName: "exclamationmark")
                        .font(.system(size: 10, weight: .heavy))
                        .foregroundStyle(leftColor)
                } else if running > 0 {
                    Text("\(running)")
                        .font(Theme.rounded(12.5, .semibold).monospacedDigit())
                        .foregroundStyle(Theme.text)
                        .contentTransition(.numericText())
                }
            }
            .frame(width: IslandLayout.wing, alignment: .center)

            Spacer(minLength: geometry.notchWidth)

            Group {
                if let lead = engine.leadJob, let f = lead.fraction {
                    HStack(spacing: 5) {
                        ProgressRing(fraction: f, lineWidth: 2.4)
                            .frame(width: 13, height: 13)
                        Text(Format.percent(f))
                            .font(Theme.rounded(11, .semibold).monospacedDigit())
                            .foregroundStyle(lead.hasRealProgress ? Theme.mint : Theme.secondary)
                    }
                } else if pending > 0 {
                    HStack(spacing: 4) {
                        Image(systemName: "hourglass")
                            .font(.system(size: 9.5, weight: .bold))
                            .foregroundStyle(Theme.amber)
                        Text("\(pending)")
                            .font(Theme.rounded(12, .semibold).monospacedDigit())
                            .foregroundStyle(Theme.amber)
                    }
                } else if let gpu = engine.snapshots.values.flatMap(\.gpus).max(by: { $0.utilization < $1.utilization }) {
                    // 没有任务时显示最忙那张卡的利用率。
                    HStack(spacing: 4) {
                        Image(systemName: "cpu").font(.system(size: 9.5, weight: .semibold))
                        Text("\(Int(gpu.utilization))%").font(Theme.rounded(11, .semibold).monospacedDigit())
                    }
                    .foregroundStyle(Theme.secondary)
                } else {
                    Circle().fill(engine.hasProblem ? leftColor : Theme.mint.opacity(0.7)).frame(width: 5, height: 5)
                }
            }
            .frame(width: IslandLayout.wing, alignment: .center)
        }
        .frame(height: geometry.notchHeight)
    }

    private var leftColor: Color {
        if engine.snapshots.values.contains(where: { if case .error = $0.health { return true }; return false }) { return Theme.coral }
        if engine.hasProblem { return Theme.amber }
        if engine.totalRunning > 0 { return Theme.mint }
        if engine.totalPending > 0 { return Theme.amber }
        return Theme.mint.opacity(0.5)
    }
}

// MARK: - 事件提示

struct ToastContent: View {
    var event: IslandEvent
    var geometry: NotchGeometry

    private var tint: Color {
        switch event.kind {
        case .finished: return Theme.mint
        case .failed: return Theme.coral
        case .started: return Theme.cyan
        case .info: return Theme.blue
        case .warning: return Theme.amber
        }
    }

    private var symbol: String {
        switch event.kind {
        case .finished: return "checkmark"
        case .failed: return "xmark"
        case .started: return "play.fill"
        case .info: return "info"
        case .warning: return "exclamationmark"
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(event.hostName)
                    .font(Theme.rounded(10.5, .semibold))
                    .foregroundStyle(Theme.secondary)
                    .lineLimit(1)
                Spacer(minLength: geometry.notchWidth + 16)
                Text(event.date, style: .time)
                    .font(Theme.rounded(10.5, .medium))
                    .foregroundStyle(Theme.tertiary)
            }
            .padding(.horizontal, 18)
            .frame(height: geometry.notchHeight)

            HStack(spacing: 12) {
                ZStack(alignment: .bottomTrailing) {
                    PetView(mood: event.kind == .failed ? .sad : event.kind == .finished ? .happy : event.kind == .warning ? .worried : .working, size: 30)
                    ZStack {
                        Circle().fill(tint)
                        Image(systemName: symbol)
                            .font(.system(size: 7, weight: .black))
                            .foregroundStyle(.black)
                    }
                    .frame(width: 13, height: 13)
                    .overlay(Circle().strokeBorder(Color.black, lineWidth: 1.5))
                    .offset(x: 4, y: 0)
                }
                .frame(width: 40, height: 40)

                VStack(alignment: .leading, spacing: 3) {
                    Text(event.title)
                        .font(Theme.rounded(14, .semibold))
                        .foregroundStyle(Theme.text)
                        .lineLimit(1)
                    if !event.subtitle.isEmpty {
                        Text(event.subtitle)
                            .font(Theme.rounded(11))
                            .foregroundStyle(Theme.secondary)
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 18)
            .frame(height: IslandLayout.toastBody - 8)
        }
        .overlay(alignment: .bottom) {
            // 自动收起倒计时
            GeometryReader { geo in
                Capsule().fill(tint.opacity(0.55))
                    .frame(width: geo.size.width * countdown, height: 2)
                    .frame(maxWidth: .infinity)
            }
            .frame(height: 2)
            .padding(.horizontal, 40)
            .padding(.bottom, 5)
        }
        .onAppear {
            countdown = 1
            withAnimation(.linear(duration: 5)) { countdown = 0 }
        }
    }

    @State private var countdown: CGFloat = 1
}

// MARK: - 展开面板

struct ExpandedContent: View {
    @ObservedObject var engine: MonitorEngine
    @ObservedObject var vm: IslandViewModel
    @ObservedObject var store: ConfigStore
    var hosts: [(HostConfig, HostSnapshot)]
    var scrolls: Bool
    var contentHeight: CGFloat

    var body: some View {
        VStack(spacing: 0) {
            header
                .frame(height: vm.geometry.notchHeight)
            Spacer().frame(height: IslandLayout.topGap)
            Group {
                if vm.page != .list {
                    page
                } else if hosts.isEmpty {
                    emptyState
                } else if scrolls {
                    MaybeScroll(showsIndicators: false) { cards }
                } else {
                    cards
                }
            }
            .frame(height: contentHeight, alignment: .top)
        }
        .padding(.horizontal, IslandLayout.sidePad)
    }

    private var cards: some View {
        let g = IslandLayout.groups(hosts, fold: store.settings.foldIdleHosts)
        return TimelineView(.periodic(from: .now, by: 1)) { context in
            VStack(spacing: IslandLayout.cardGap) {
                ForEach(g.cards, id: \.0.id) { host, snap in card(host, snap, context.date) }
                if !g.idle.isEmpty {
                    IdleHostsRow(hosts: g.idle, expanded: vm.showIdle) {
                        withAnimation(.spring(response: 0.4, dampingFraction: 0.85)) { vm.showIdle.toggle() }
                    }
                    .frame(height: IslandLayout.idleRow)
                    if vm.showIdle {
                        ForEach(g.idle, id: \.0.id) { host, snap in card(host, snap, context.date) }
                    }
                }
            }
        }
    }

    private func card(_ host: HostConfig, _ snap: HostSnapshot, _ now: Date) -> some View {
        HostCard(host: host, snap: snap, now: now,
                 onLogin: {
                     Task { _ = await engine.login(host) }
                 },
                 onRefresh: { engine.refresh(host.id) },
                 onOpenJob: { job in
                     vm.pinnedJob = job
                     vm.returnPage = nil
                     vm.page = .job(host: host.id, job: job.id)
                 },
                 onHistory: { vm.page = .history(host: host.id) },
                 onPicker: { vm.page = .picker(host: host.id) },
                 onAllJobs: { vm.openAllJobs(host: host.id) },
                 onPartitions: { vm.page = .partitions(host: host.id) },
                 onSwitchToDirect: { engine.setKind(host.id, .direct) },
                 onTogglePause: { engine.setPaused(host.id, !host.paused) },
                 onSetInterval: { engine.setHostInterval(host.id, $0) },
                 globalInterval: store.settings.defaultInterval,
                 globalAuto: store.settings.autoRefresh,
                 onToggleMute: { engine.toggleMute(host.id) },
                 loggingIn: engine.loggingIn.contains(host.id),
                 starred: Set(store.settings.starredJobs.filter { $0.hostID == host.id }.map(\.jobID)),
                 onStar: { engine.toggleStar(hostID: host.id, job: $0) })
    }

    @ViewBuilder
    private var page: some View {
        let back = { withAnimation(.spring(response: 0.4, dampingFraction: 0.85)) { vm.page = .list } }
        let backFromJob = {
            withAnimation(.spring(response: 0.4, dampingFraction: 0.85)) {
                vm.page = vm.returnPage ?? .list
                vm.returnPage = nil
            }
        }
        switch vm.page {
        case .job(let hid, let jid):
            if let host = store.host(hid) {
                let snap = engine.snapshots[hid]
                // 数组元素嵌在分组行里，也要能找到。
                let live = snap?.jobs.first { $0.id == jid }
                    ?? snap?.jobs.compactMap(\.array).flatMap(\.running).first { $0.id == jid }
                if let job = live ?? vm.pinnedJob {
                    JobDetailPage(host: host, job: job, sampledAt: snap?.lastUpdate, stillListed: live != nil,
                                  engine: engine, onBack: backFromJob, openSettings: vm.openSettings,
                                  onOpenElement: { e in
                                      vm.pinnedJob = e
                                      vm.page = .job(host: hid, job: e.id)
                                  })
                        .id(jid)
                }
            }
        case .history(let hid):
            if let host = store.host(hid) { HistoryPage(host: host, engine: engine, onBack: back) }
        case .picker(let hid):
            if let host = store.host(hid) { ProcessPickerPage(host: host, engine: engine, store: store, onBack: back) }
        case .partitions(let hid):
            if let host = store.host(hid) { PartitionsPage(host: host, engine: engine, onBack: back) }
        case .jobs:
            AllJobsPage(engine: engine, store: store, filter: $vm.jobsFilter, onBack: back) { host, job in
                vm.pinnedJob = job
                vm.returnPage = .jobs
                vm.page = .job(host: host.id, job: job.id)
            }
        case .costs:
            CostPage(engine: engine, store: store, onBack: back,
                     onOpenSettings: { SettingsView.open(.billing, show: vm.openSettings) })
        case .list:
            EmptyView()
        }
    }

    private var header: some View {
        let sideWidth = max(0, (IslandLayout.spec(mode: .expanded, geometry: vm.geometry, contentHeight: 0).size.width
                                - 28 - 2 * IslandLayout.sidePad - vm.geometry.notchWidth) / 2 - 6)
        return HStack(spacing: 0) {
            HStack(spacing: 7) {
                PetView(mood: engine.petMood, size: 15)
                Text("ServerIsland")
                    .font(Theme.rounded(12.5, .semibold))
                    .foregroundStyle(Theme.text)
                    .lineLimit(1)
                    .fixedSize()
                summaryChip(icon: "bolt.fill", value: engine.totalRunning, color: Theme.mint, help: L("运行中", "Running"))
                    .padding(.leading, 4)
                summaryChip(icon: "hourglass", value: engine.totalPending, color: Theme.amber, help: L("排队中", "Pending"))
                if store.settings.isQuiet() {
                    Image(systemName: "moon.fill").font(.system(size: 9.5, weight: .semibold))
                        .foregroundStyle(Theme.tertiary)
                        .help(L("勿扰时段（\(store.settings.quietLabel)）：不响铃、不弹窗", "Quiet hours (\(store.settings.quietLabel)): no sounds or pop-ups"))
                }
                if engine.demo {
                    Text(L("演示", "Demo"))
                        .font(Theme.rounded(9, .bold))
                        .foregroundStyle(Theme.amber)
                        .padding(.horizontal, 5).padding(.vertical, 1.5)
                        .background(Capsule().fill(Theme.amber.opacity(0.16)))
                        .fixedSize()
                }
            }
            .frame(width: sideWidth, alignment: .leading)

            Spacer(minLength: 0)

            HStack(spacing: 8) {
                GlobalPollMenu(store: store, engine: engine)
                if store.settings.billingEnabled {
                    IslandIconButton(systemName: Billing.symbol(store.settings.currency),
                                     help: L("费用：今天、本月、按分区和作业（估算）", "Costs: today, this month, by partition and job (estimate)"),
                                     active: vm.page == .costs) {
                        vm.page = vm.page == .costs ? .list : .costs
                    }
                }
                IslandIconButton(systemName: "list.bullet.rectangle", help: L("全部任务：所有服务器的任务，可搜索、筛选", "All jobs across servers: search and filter"),
                                 active: vm.page == .jobs) {
                    if vm.page == .jobs { vm.page = .list } else { vm.openAllJobs() }
                }
                IslandIconButton(systemName: vm.pinned ? "pin.fill" : "pin", help: vm.pinned ? L("取消固定", "Unpin") : L("固定面板（鼠标移开也不收起）", "Pin (stay open when the mouse leaves)"),
                                 active: vm.pinned) {
                    vm.pinned.toggle()
                    if !vm.pinned { vm.onUnpin() }
                }
                IslandIconButton(systemName: "arrow.clockwise", help: L("立即刷新", "Refresh now"), spinning: engine.isRefreshing) {
                    engine.refreshAll()
                }
                IslandIconButton(systemName: "gearshape.fill", help: L("设置", "Settings")) { vm.openSettings() }
            }
            .frame(width: sideWidth, alignment: .trailing)
        }
        .padding(.top, 2)
    }

    private func summaryChip(icon: String, value: Int, color: Color, help: String) -> some View {
        HStack(spacing: 3) {
            Image(systemName: icon).font(.system(size: 8.5, weight: .bold))
            Text("\(value)").font(Theme.rounded(11, .semibold).monospacedDigit())
        }
        .foregroundStyle(value > 0 ? color : Theme.tertiary)
        .fixedSize()
        .help(help)
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            PetView(mood: .idle, size: 30)
            Text(L("还没有添加服务器", "No servers yet"))
                .font(Theme.rounded(13, .semibold))
                .foregroundStyle(Theme.text)
            Button { vm.openSettings() } label: {
                Text(L("从 ~/.ssh/config 添加…", "Add from ~/.ssh/config…"))
                    .font(Theme.rounded(11, .semibold))
                    .padding(.horizontal, 12).padding(.vertical, 5)
                    .background(Capsule().fill(Theme.mint.opacity(0.18)))
                    .foregroundStyle(Theme.mint)
            }
            .buttonStyle(.plain)
        }
        .frame(maxWidth: .infinity)
        .frame(height: IslandLayout.emptyState)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Theme.card))
    }
}

/// 头部的全局轮询菜单：间隔 + 仅手动刷新。
struct GlobalPollMenu: View {
    @ObservedObject var store: ConfigStore
    @ObservedObject var engine: MonitorEngine
    @State private var hovering = false

    var body: some View {
        if IslandPreview.active { menuLabel } else { menu }
    }

    private var menu: some View {
        Menu {
            Section(L("自动刷新间隔", "Auto refresh interval")) {
                ForEach(IntervalMenu.presets, id: \.self) { s in
                    Button { engine.setGlobalInterval(s) } label: {
                        Label(L("每 \(IntervalPicker.label(s))", "Every \(IntervalPicker.label(s))"),
                              systemImage: store.settings.autoRefresh && store.settings.defaultInterval == s ? "checkmark" : "")
                    }
                }
            }
            Divider()
            Button { engine.setAutoRefresh(!store.settings.autoRefresh) } label: {
                Label(L("仅手动刷新", "Manual refresh only"), systemImage: store.settings.autoRefresh ? "" : "checkmark")
            }
            Divider()
            Text(L("单台服务器可以在卡片上单独设置或暂停", "Each server can be set or paused on its card"))
        } label: { menuLabel }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .onHover { hovering = $0 }
        .help(store.settings.autoRefresh ? L("自动刷新间隔（点击修改，或改为仅手动刷新）", "Auto refresh interval (click to change)") : L("当前为仅手动刷新", "Manual refresh only"))
    }

    private var menuLabel: some View {
            HStack(spacing: 3) {
                Image(systemName: store.settings.autoRefresh ? "timer" : "hand.tap.fill")
                    .font(.system(size: 9.5, weight: .semibold))
                Text(label).font(Theme.rounded(10.5, .semibold).monospacedDigit())
            }
            .foregroundStyle(store.settings.autoRefresh ? (hovering ? Theme.text : Theme.secondary) : Theme.amber)
            .padding(.horizontal, 8).frame(height: 24)
            .background(Capsule().fill(store.settings.autoRefresh ? Color.white.opacity(hovering ? 0.14 : 0.07)
                                                                  : Theme.amber.opacity(0.16)))
            .fixedSize()
    }

    private var label: String {
        guard store.settings.autoRefresh else { return L("手动", "manual") }
        let s = store.settings.defaultInterval
        return s < 60 ? "\(s)s" : (s % 60 == 0 ? "\(s / 60)m" : "\(s)s")
    }
}
