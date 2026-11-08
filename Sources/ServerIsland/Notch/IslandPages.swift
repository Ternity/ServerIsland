import AppKit
import SwiftUI

/// 展开面板里的二级页面。
enum IslandPage: Equatable {
    case list
    case job(host: UUID, job: String)
    case history(host: UUID)
    case picker(host: UUID)
    case jobs
    case partitions(host: UUID)
    case costs
}

// MARK: - 通用

/// 预览渲染（ImageRenderer）画不出 ScrollView 的内容，预览时换成普通布局。
enum IslandPreview { static var active = false }

struct MaybeScroll<Content: View>: View {
    var showsIndicators = true
    @ViewBuilder var content: Content
    var body: some View {
        if IslandPreview.active {
            VStack(spacing: 0) { content; Spacer(minLength: 0) }
        } else {
            ScrollView(.vertical, showsIndicators: showsIndicators) { content }
        }
    }
}

struct PageHeader<Trailing: View>: View {
    var title: String
    var subtitle: String
    var onBack: () -> Void
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 10) {
            IslandIconButton(systemName: "chevron.left", help: L("返回", "Back"), action: onBack)
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(Theme.rounded(13.5, .semibold))
                    .foregroundStyle(Theme.text)
                    .lineLimit(1).truncationMode(.middle)
                Text(subtitle)
                    .font(Theme.rounded(10.5))
                    .foregroundStyle(Theme.tertiary)
                    .lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 8)
            trailing
        }
        .frame(height: 30)
    }
}

/// 按住一段时间才会触发的按钮（防误触）。
struct HoldToConfirmButton: View {
    var title: String
    var tint: Color
    var duration: Double = 1.5
    var action: () -> Void
    @State private var progress: CGFloat = 0
    @State private var pressing = false

    var body: some View {
        ZStack(alignment: .leading) {
            Capsule().fill(tint.opacity(0.12))
            GeometryReader { geo in
                Capsule().fill(tint.opacity(0.35)).frame(width: geo.size.width * progress)
            }
            HStack(spacing: 6) {
                Image(systemName: "hand.tap.fill").font(.system(size: 10, weight: .semibold))
                Text(pressing ? L("继续按住…", "Keep holding…") : title).font(Theme.rounded(11, .semibold))
            }
            .foregroundStyle(tint)
            .frame(maxWidth: .infinity)
        }
        .frame(width: 190, height: 28)
        .contentShape(Capsule())
        .onLongPressGesture(minimumDuration: duration, maximumDistance: 30) {
            progress = 0
            action()
        } onPressingChanged: { down in
            pressing = down
            if down {
                withAnimation(.linear(duration: duration)) { progress = 1 }
            } else {
                withAnimation(.easeOut(duration: 0.2)) { progress = 0 }
            }
        }
        .help(L("为防止误触，需要按住 ", "To avoid accidents, hold for ") + String(format: "%.1f", duration) + L(" 秒", " s"))
    }
}

struct SmallIconButton: View {
    var systemName: String
    var help: String
    var tint: Color = Theme.tertiary
    var action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(hovering ? Theme.text : tint)
                .frame(width: 20, height: 20)
                .background(Circle().fill(Color.white.opacity(hovering ? 0.12 : 0)))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(help)
    }
}

// MARK: - 任务详情

struct JobDetailPage: View {
    var host: HostConfig
    var job: RemoteJob
    var sampledAt: Date?
    var stillListed: Bool
    @ObservedObject var engine: MonitorEngine
    var onBack: () -> Void
    var openSettings: () -> Void
    /// 预览渲染用：直接给定详情，不连接服务器。
    var preloaded: JobDetail?
    var previewStage: CancelStage?
    var onOpenElement: (RemoteJob) -> Void = { _ in }
    /// 预览渲染用：直接给定曲线。
    var preloadedCurves: [AppCurve] = []

    enum CancelStage: Equatable {
        case idle
        case confirm(force: Bool)
        case running
        case result(CancelOutcome, wasTerm: Bool)
    }

    @State private var detail: JobDetail?
    @State private var loading = false
    @State private var showStderr = false
    @State private var stage: CancelStage = .idle
    @State private var curves: [AppCurve] = []

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PageHeader(title: job.name,
                       subtitle: "\(host.displayName) · " + (job.array != nil ? L("数组作业 #\(job.id)", "Job array #\(job.id)")
                                                                : job.source == .slurm ? L("作业 #\(job.id)", "Job #\(job.id)")
                                                                : job.source == .process ? "PID \(job.id)" : L("进度文件", "Progress file"))
                           + (stillListed ? "" : L(" · 已不在列表中", " · no longer listed")),
                       onBack: onBack) {
                HStack(spacing: 6) {
                    // 数组里的单个元素不能单独关注（关注的是整个数组）
                    if job.array != nil || job.arrayJobID == nil {
                        let starred = engine.isStarred(host.id, job.id)
                        IslandIconButton(systemName: starred ? "star.fill" : "star",
                                         help: starred ? L("取消关注", "Unstar") : L("关注：排在前面，可以只提醒关注的任务", "Star: keep it on top; alerts can be limited to starred jobs"),
                                         active: starred, activeTint: Theme.amber) {
                            engine.toggleStar(hostID: host.id, job: job)
                        }
                    }
                    if job.source != .progressFile {
                        CopyButton(value: job.id, help: job.source == .process ? L("复制 PID", "Copy PID") : L("复制作业号", "Copy job ID"),
                                   label: job.source == .process ? "PID" : L("作业号", "Job ID"))
                    }
                    stateChip
                    IslandIconButton(systemName: "arrow.clockwise", help: L("刷新详情", "Refresh details"), spinning: loading) {
                        Task { await load() }
                    }
                }
            }
            if let a = job.array {
                if let c = costText {
                    Label(c, systemImage: Billing.symbol(engine.store.settings.currency))
                        .font(Theme.rounded(10.5)).foregroundStyle(Theme.secondary)
                }
                ArrayOverview(job: job, array: a, onOpen: onOpenElement)
            } else {
                fields
                if !curves.isEmpty {
                    HStack(spacing: 8) { ForEach(Array(curves.enumerated()), id: \.offset) { CurveChart(curve: $0.element, compact: curves.count > 1) } }
                        .frame(height: 104)
                }
                logBox
            }
            dangerZone.frame(height: 30)
        }
        .onAppear {
            if let preloaded { detail = preloaded }
            if let previewStage { stage = previewStage }
            if !preloadedCurves.isEmpty { curves = preloadedCurves }
        }
        .task(id: job.id) {
            guard preloaded == nil, job.array == nil else { return }
            // 打开期间每 4 秒刷新一次日志末尾；曲线（增量）每 20 秒一次。离开页面自动停止。
            var tick = 0
            while !Task.isCancelled {
                await load()
                if tick % 5 == 0, let app = job.app, !AppCurves.sources(for: app).isEmpty {
                    let c = await engine.fetchCurves(host: host, app: app)
                    if c != curves { curves = c }
                }
                tick += 1
                try? await Task.sleep(for: .seconds(4))
            }
        }
    }

    private func load() async {
        loading = true
        let d = await engine.fetchDetail(host: host, job: job)
        if detail != d { detail = d }
        loading = false
    }

    private var stateChip: some View {
        let c = Theme.color(for: job.state)
        return Text(job.state.label)
            .font(Theme.rounded(10, .bold))
            .foregroundStyle(c)
            .padding(.horizontal, 7).padding(.vertical, 2.5)
            .background(Capsule().fill(c.opacity(0.15)))
    }

    /// 计算软件（NEP / GPUMD）识别到的信息，排在服务器字段前面。
    private var appFields: [(String, String)] {
        if job.state == .pending, let r = PendingReason.explain(job.reason) {
            return [(L("排队原因", "Pending reason"), r.short + "：" + r.long.replacingOccurrences(of: "\n", with: " "))]
        }
        guard let a = job.app else { return [] }
        var f: [(String, String)] = [(L("软件", "Software"), a.title)]
        if let c = a.current {
            var p = AppProgress.compact(c) + (a.total.map { " / " + AppProgress.compact($0) } ?? "") + " " + a.unit
            if let fr = a.fraction { p += "（\(Format.percent(fr))）" }
            if !a.hasTarget {
                switch a.kind {
                case .gpumd: p += L("，run.in 无 run 步数", ", no run steps in run.in")
                case .nep, .gnep: p += L("，未设目标按默认值", ", default target")
                case .vasp: p += a.vaspMode == .relax ? L("（收敛前不知道总步数）", " (total unknown until converged)") : ""
                case .torchnep, .mace: p += L("，没读到总 epoch", ", total epochs unknown")
                case .deepmd: p += L("，没读到总步数", ", total steps unknown")
                case .lammps, .custom: break
                }
            }
            f.append((L("进度", "Progress"), p))
        }
        if let r = a.rate { f.append((L("速度", "Speed"), AppProgress.compact(r) + L(" \(a.unit)/秒", " \(a.unit)/s"))) }
        if let e = a.eta { f.append((L("预计剩余", "ETA"), Format.duration(e))) }
        if let u = a.updatedAt { f.append((L("输出更新", "Last output"), Format.relative(u))) }
        if let m = a.endMarker {
            f.append((L("结束标记", "End marker"), m ? L("已正常结束", "finished normally") : L("还没有出现", "not yet")))
        }
        if let n = a.neighbor {
            f.append((L("近邻", "Neighbors"), L("径向 \(n.radialActual)/\(n.radialMax) · 角向 \(n.angularActual)/\(n.angularMax)",
                                                  "radial \(n.radialActual)/\(n.radialMax) · angular \(n.angularActual)/\(n.angularMax)")))
        }
        if !a.losses.isEmpty {
            f.append(("Loss", a.losses.filter { !["L1", "L2"].contains($0.label) }
                .map { "\($0.label) \(AppProgress.sci($0.value))" }.joined(separator: " · ")))
        }
        f.append((L("工作目录", "Directory"), a.dir))
        return f
    }

    /// 费用（打开计费、填了单价、知道分到的资源时）："¥12.40 · ¥4.00/小时 · 4 卡 × ¥1.00/卡时"。
    private var costText: String? {
        guard let c = engine.liveCost(host, job) else { return nil }
        let cur = engine.store.settings.currency
        var parts = [L("约 ", "≈ ") + Billing.money(c.spent, currency: cur)]
        if c.perHour > 0 { parts.append(Billing.money(c.perHour, currency: cur) + L("/小时", "/h")) }
        if !c.detail.isEmpty { parts.append(c.detail) }
        return parts.joined(separator: " · ")
    }

    private var fields: some View {
        var all = appFields
        // 费用整行显示（见下面的 long），不会因为字段太多被截掉
        if let c = costText { all.insert((L("费用", "Cost"), c), at: 0) }
        for kv in detail?.fields ?? [] where !all.contains(where: { $0.0 == kv.0 }) { all.append(kv) }
        let long = Set([L("命令", "Command"), L("工作目录", "Directory"), L("资源", "Resources"), L("节点", "Nodes"), "Loss", L("费用", "Cost"),
                        L("近邻", "Neighbors"), L("进度", "Progress"), L("排队原因", "Pending reason")])
        let short = all.filter { !long.contains($0.0) }.prefix(job.app == nil ? 8 : 10)
        let wide = all.filter { long.contains($0.0) }
        return VStack(alignment: .leading, spacing: 5) {
            if let err = detail?.error {
                Label(err, systemImage: "exclamationmark.triangle.fill")
                    .font(Theme.rounded(11)).foregroundStyle(Theme.amber)
            } else if detail == nil {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.mini).scaleEffect(0.7)
                    Text(L("正在从服务器读取…", "Loading from server…")).font(Theme.rounded(11)).foregroundStyle(Theme.tertiary)
                }
            }
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 14), GridItem(.flexible(), spacing: 14)],
                      alignment: .leading, spacing: 5) {
                ForEach(Array(short.enumerated()), id: \.offset) { _, kv in fieldRow(kv.0, kv.1) }
            }
            ForEach(Array(wide.enumerated()), id: \.offset) { _, kv in fieldRow(kv.0, kv.1) }
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Theme.card))
    }

    private func fieldRow(_ k: String, _ v: String) -> some View {
        HStack(spacing: 8) {
            Text(k).font(Theme.rounded(10, .medium)).foregroundStyle(Theme.tertiary)
                .frame(width: 58, alignment: .leading)
            Text(v).font(Theme.mono(10.5)).foregroundStyle(Theme.secondary)
                .lineLimit(1).truncationMode(.middle)
                .textSelection(.enabled)
                .help(v)
            if [L("工作目录", "Directory"), L("命令", "Command"), L("节点", "Nodes")].contains(k) {
                CopyButton(value: v, help: L("复制", "Copy ") + k)
            }
        }
    }

    private var currentLog: JobDetail.LogTail? {
        showStderr ? detail?.stderr : detail?.stdout
    }

    private var logBox: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Text(L("日志末尾", "Log tail")).font(Theme.rounded(10.5, .semibold)).foregroundStyle(Theme.secondary)
                if detail?.stderr != nil {
                    segment("stdout", on: !showStderr) { showStderr = false }
                    segment("stderr", on: showStderr) { showStderr = true }
                }
                if let path = currentLog?.path {
                    Text(path).font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
                        .lineLimit(1).truncationMode(.head).help(path)
                    CopyButton(value: path, help: L("复制日志路径", "Copy log path"))
                    CopyButton(value: "ssh \(host.alias) " + SSHRunner.shellQuote("tail -f " + SSHRunner.shellQuote(path)),
                               help: L("复制一条可以在本机终端里实时查看日志的命令", "Copy a command to follow this log from your terminal"), label: "tail -f")
                }
                Spacer(minLength: 0)
                if let dir = detail?.fields.first(where: { $0.0 == L("工作目录", "Directory") })?.1 {
                    SmallIconButton(systemName: "terminal", help: L("在终端中打开（ssh 到工作目录）", "Open in Terminal (ssh to the directory)")) {
                        SSHRunner.openShellInTerminal(host: host, directory: dir)
                    }
                }
            }
            ScrollViewReader { proxy in
                MaybeScroll {
                    VStack(alignment: .leading, spacing: 1) {
                        if let log = currentLog, !log.lines.isEmpty {
                            ForEach(Array(log.lines.enumerated()), id: \.offset) { i, line in
                                Text(line.isEmpty ? " " : line)
                                    .font(Theme.mono(10))
                                    .foregroundStyle(Self.tint(line))
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .id(i)
                            }
                        } else {
                            Text(currentLog?.note ?? (detail == nil ? "" : L("（日志为空）", "(empty log)")))
                                .font(Theme.rounded(10.5)).foregroundStyle(Theme.tertiary)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                    .textSelection(.enabled)
                    .padding(8)
                }
                .onChange(of: currentLog?.lines.count ?? 0) { _, n in
                    if n > 0 { proxy.scrollTo(n - 1, anchor: .bottom) }
                }
                .onChange(of: showStderr) { _, _ in
                    if let n = currentLog?.lines.count, n > 0 { proxy.scrollTo(n - 1, anchor: .bottom) }
                }
            }
            .frame(maxHeight: .infinity)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.white.opacity(0.04)))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Theme.hairline, lineWidth: 0.5))
        }
    }

    private func segment(_ title: String, on: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(Theme.mono(9.5, .semibold))
                .foregroundStyle(on ? Theme.text : Theme.tertiary)
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(Capsule().fill(Color.white.opacity(on ? 0.14 : 0)))
        }
        .buttonStyle(.plain)
    }

    /// 报错行标红，便于一眼看到。
    static func tint(_ line: String) -> Color {
        let l = line.lowercased()
        if l.contains("error") || l.contains("traceback") || l.contains("exception") || l.contains("out of memory") || l.contains("killed") {
            return Theme.coral.opacity(0.9)
        }
        if l.contains("warn") { return Theme.amber.opacity(0.85) }
        return Theme.secondary
    }

    // MARK: 取消

    private var cancellable: Bool { job.source != .progressFile && job.state.isActive && stillListed }

    private var cancelCommand: String {
        job.source == .slurm ? "\(host.kind.cancelCommand) \(job.id)" : "kill -TERM \(job.id)"
    }

    private var cancelTarget: String {
        if let a = job.array {
            return L("整个数组「\(job.name)」（运行 \(a.running.count) + 排队 \(a.pending) 个元素）",
                     "the whole array “\(job.name)” (\(a.running.count) running + \(a.pending) pending)")
        }
        return L("「\(job.name)」", "“\(job.name)”")
    }

    @ViewBuilder
    private var dangerZone: some View {
        if !cancellable {
            EmptyView()
        } else if !host.allowCancel {
            HStack(spacing: 6) {
                Image(systemName: "lock.fill").font(.system(size: 9.5))
                Text(L("如需在这里取消任务，请先在设置中为「\(host.displayName)」开启「允许取消任务」", "To cancel jobs here, enable “Allow Cancelling” for \(host.displayName) in Settings"))
                    .lineLimit(1)
                Button(L("打开设置", "Open Settings"), action: openSettings).buttonStyle(.link)
            }
            .font(Theme.rounded(10.5))
            .foregroundStyle(Theme.tertiary)
        } else {
            switch stage {
            case .idle:
                HStack {
                    Spacer()
                    HoldToConfirmButton(title: job.array != nil ? L("按住以取消整个数组", "Hold to cancel array")
                                            : job.source == .slurm ? L("按住以取消作业", "Hold to cancel job") : L("按住以终止进程", "Hold to stop process"),
                                        tint: Theme.coral) {
                        stage = .confirm(force: false)
                    }
                }
            case .confirm(let force):
                confirmRow(force: force)
            case .running:
                HStack(spacing: 6) {
                    Spacer()
                    ProgressView().controlSize(.mini).scaleEffect(0.7)
                    Text(L("正在执行…", "Running…")).font(Theme.rounded(11)).foregroundStyle(Theme.secondary)
                }
            case .result(let outcome, let wasTerm):
                HStack(spacing: 8) {
                    switch outcome {
                    case .done(let msg):
                        Label(msg, systemImage: "checkmark.circle.fill").foregroundStyle(Theme.mint)
                    case .aborted(let msg):
                        Label(L("没有执行：\(msg)", "Not executed: \(msg)"), systemImage: "exclamationmark.triangle.fill").foregroundStyle(Theme.amber)
                    }
                    Spacer()
                    if wasTerm, case .done = outcome, job.source == .process {
                        HoldToConfirmButton(title: L("仍未退出？按住强制结束", "Still running? Hold to force kill"), tint: Theme.coral, duration: 2) {
                            stage = .confirm(force: true)
                        }
                    }
                }
                .font(Theme.rounded(11))
            }
        }
    }

    private func confirmRow(force: Bool) -> some View {
        let cmd = force ? "kill -KILL \(job.id)" : cancelCommand
        return HStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Theme.coral)
            VStack(alignment: .leading, spacing: 1) {
                Text(force ? L("确定要在 \(host.displayName) 上强制结束\(cancelTarget)吗？", "Force-kill \(cancelTarget) on \(host.displayName)?")
                           : L("确定要在 \(host.displayName) 上取消\(cancelTarget)吗？", "Cancel \(cancelTarget) on \(host.displayName)?"))
                    .font(Theme.rounded(11, .semibold)).foregroundStyle(Theme.text).lineLimit(1)
                Text(L("将执行：\(cmd)　执行前会再次核对任务是否还是同一个", "Will run: \(cmd)  — the job is re-checked first")).font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
                    .lineLimit(1)
            }
            Spacer(minLength: 6)
            PillButton(title: L("不了", "Never mind"), tint: Theme.secondary) { stage = .idle }
            Button {
                stage = .running
                Task {
                    let r = await engine.cancel(host: host, job: job, sampledAt: sampledAt, force: force)
                    stage = .result(r, wasTerm: !force)
                }
            } label: {
                Text(force ? L("确认强制结束", "Force Kill") : L("确认取消", "Cancel It"))
                    .font(Theme.rounded(10.5, .bold))
                    .foregroundStyle(.black)
                    .padding(.horizontal, 10).padding(.vertical, 4)
                    .background(Capsule().fill(Theme.coral))
            }
            .buttonStyle(.plain)
        }
    }
}

// MARK: - 历史记录

struct HistoryPage: View {
    var host: HostConfig
    @ObservedObject var engine: MonitorEngine
    var onBack: () -> Void
    var preloaded: [HistoryEntry]?
    /// 预览渲染用：各任务的费用。
    var preloadedCosts: [String: Double]?
    @State private var entries: [HistoryEntry]?
    @State private var fromServer = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PageHeader(title: L("历史记录", "History"), subtitle: host.displayName + " · " + sourceText, onBack: onBack) {
                if let e = entries {
                    let failed = e.filter { [.failed, .timeout].contains($0.jobState) }.count
                    let done = e.filter { $0.jobState == .completed }.count
                    HStack(spacing: 8) {
                        Label("\(done)", systemImage: "checkmark").foregroundStyle(Theme.mint)
                        Label("\(failed)", systemImage: "xmark").foregroundStyle(failed > 0 ? Theme.coral : Theme.tertiary)
                        if billing {
                            let total = e.compactMap(cost).reduce(0, +)
                            Text(Billing.money(total, currency: engine.store.settings.currency))
                                .foregroundStyle(total > 0 ? Theme.amber : Theme.tertiary)
                                .help(L("列表里这些任务的估算费用合计", "Estimated total for the jobs listed"))
                        }
                    }
                    .font(Theme.rounded(11, .semibold))
                }
            }
            MaybeScroll(showsIndicators: false) {
                VStack(spacing: 4) {
                    if let entries {
                        if entries.isEmpty {
                            Text(L("还没有结束的任务", "No finished jobs yet")).font(Theme.rounded(11.5)).foregroundStyle(Theme.tertiary)
                                .frame(maxWidth: .infinity).padding(.top, 40)
                        }
                        ForEach(entries) { row($0) }
                    } else {
                        ProgressView().controlSize(.small).padding(.top, 40)
                    }
                }
            }
            .frame(maxHeight: .infinity)
        }
        .onAppear { if let preloaded { entries = preloaded; fromServer = true } }
        .task {
            guard preloaded == nil else { return }
            let r = await engine.fetchHistory(host: host)
            fromServer = r.fromServer
            entries = r.entries
        }
    }

    private var billing: Bool { engine.billingActive(host) }

    private func cost(_ e: HistoryEntry) -> Double? {
        if let preloadedCosts { return preloadedCosts[e.id] }
        return engine.costs.cost(host: host.id, id: e.id)
    }

    private var sourceText: String {
        guard entries != nil else { return L("读取中…", "Loading…") }
        return fromServer ? L("最近 24 小时来自 sacct，更早的是本机记录（保留 7 天）", "last 24 h from sacct, older from the local record (kept 7 days)")
            : L("本机记录（保留 7 天）", "local record (kept 7 days)")
    }

    private func row(_ e: HistoryEntry) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            rowTop(e)
            if let dir = e.dir {
                HStack(spacing: 6) {
                    Image(systemName: "folder").font(.system(size: 8.5)).foregroundStyle(Theme.tertiary)
                    Text(dir).font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
                        .lineLimit(1).truncationMode(.head).help(dir)
                    Spacer(minLength: 6)
                    CopyButton(value: dir, help: L("复制目录", "Copy directory"))
                    SmallIconButton(systemName: "terminal", help: L("在终端中打开（ssh 到这个目录）", "Open in Terminal (ssh to this directory)")) {
                        SSHRunner.openShellInTerminal(host: host, directory: dir)
                    }
                }
                .padding(.leading, 30)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Theme.card))
    }

    private func rowTop(_ e: HistoryEntry) -> some View {
        let state = e.jobState
        let unknown = e.state == HistoryEntry.endedUnknown
        let c = unknown ? Theme.secondary : Theme.color(for: state)
        return HStack(spacing: 10) {
            Image(systemName: unknown ? "questionmark" : [.failed, .timeout].contains(state) ? "xmark" : state == .cancelled ? "minus" : "checkmark")
                .font(.system(size: 9, weight: .bold)).foregroundStyle(c)
                .frame(width: 20, height: 20).background(Circle().fill(c.opacity(0.15)))
            Text(e.name).font(Theme.rounded(12, .medium)).foregroundStyle(Theme.text).lineLimit(1)
            Text(e.id).font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
            if let note = e.note { Text(note).font(Theme.rounded(9.5)).foregroundStyle(Theme.tertiary).lineLimit(1) }
            Spacer(minLength: 6)
            Text(e.stateLabel).font(Theme.rounded(10.5, .medium)).foregroundStyle(c)
            if let x = e.exitCode, !x.isEmpty, x != "0:0" {
                Text("exit \(x)").font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
            }
            Text(e.elapsed.map(Format.duration) ?? "—").font(Theme.rounded(10.5).monospacedDigit())
                .foregroundStyle(Theme.secondary).frame(width: 64, alignment: .trailing)
            if billing {
                Text(cost(e).map { Billing.money($0, currency: engine.store.settings.currency) } ?? "—")
                    .font(Theme.rounded(10.5, .medium).monospacedDigit())
                    .foregroundStyle(cost(e) == nil ? Theme.tertiary : Theme.amber)
                    .frame(width: 62, alignment: .trailing)
                    .help(L("估算费用（按结束时的单价）", "Estimated cost (at the price when it ended)"))
            }
            Text(Format.relative(e.endedAt)).font(Theme.rounded(10.5)).foregroundStyle(Theme.tertiary)
                .frame(width: 58, alignment: .trailing)
                .help(e.endedAt.formatted(date: .abbreviated, time: .shortened))
        }
    }
}

// MARK: - 选择要监听的进程

struct ProcessPickerPage: View {
    var host: HostConfig
    @ObservedObject var engine: MonitorEngine
    @ObservedObject var store: ConfigStore
    var onBack: () -> Void
    @State private var procs: [RemoteJob]?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PageHeader(title: L("监听进程", "Watch a Process"), subtitle: host.displayName + L(" · 按 GPU 显存和 CPU 排序；监听的进程结束时一定会提醒", " · sorted by VRAM and CPU; watched processes always alert when they end"),
                       onBack: onBack) {
                IslandIconButton(systemName: "arrow.clockwise", help: L("重新读取", "Reload")) { Task { await load() } }
            }
            MaybeScroll(showsIndicators: false) {
                VStack(spacing: 4) {
                    if let procs {
                        if procs.isEmpty {
                            Text(L("没有找到属于你的进程", "No processes of yours found")).font(Theme.rounded(11.5)).foregroundStyle(Theme.tertiary).padding(.top, 40)
                        }
                        ForEach(procs) { row($0) }
                    } else {
                        ProgressView().controlSize(.small).padding(.top, 40)
                    }
                }
            }
            .frame(maxHeight: .infinity)
            Text(L("也可以在 设置 → \(host.displayName) → 监听的进程 里手动输入 PID。", "You can also enter a PID in Settings → \(host.displayName) → Watched Processes."))
                .font(Theme.rounded(10)).foregroundStyle(Theme.tertiary)
        }
        .task { await load() }
    }

    private func load() async {
        procs = nil
        procs = await engine.topProcesses(host: host) ?? []
    }

    private func row(_ p: RemoteJob) -> some View {
        let on = store.host(host.id)?.watched.contains { $0.pid == p.id } ?? false
        return Button {
            if on { engine.unwatch(hostID: host.id, pid: p.id) } else { engine.watch(hostID: host.id, job: p) }
        } label: {
            HStack(spacing: 10) {
                Image(systemName: on ? "eye.fill" : "eye")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(on ? Theme.mint : Theme.tertiary)
                    .frame(width: 20, height: 20)
                    .background(Circle().fill((on ? Theme.mint : Color.white).opacity(on ? 0.16 : 0.05)))
                Text(p.name).font(Theme.rounded(12, .medium)).foregroundStyle(Theme.text).lineLimit(1)
                Text("pid \(p.id)").font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
                Spacer(minLength: 6)
                if let g = p.gpuMemoryMB { tag(L("显存 ", "VRAM ") + Format.bytes(g * 1_048_576), Theme.cyan) }
                if let c = p.cpuPercent { tag("CPU \(Int(c))%", Theme.secondary) }
                if let m = p.memoryBytes { tag(Format.bytes(m), Theme.tertiary) }
                Text(p.elapsed.map(Format.duration) ?? "").font(Theme.rounded(10.5).monospacedDigit())
                    .foregroundStyle(Theme.tertiary).frame(width: 70, alignment: .trailing)
            }
            .padding(.horizontal, 10).padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(on ? Theme.mint.opacity(0.08) : Theme.card))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(p.detail)
    }

    private func tag(_ s: String, _ c: Color) -> some View {
        Text(s).font(Theme.rounded(10, .medium).monospacedDigit()).foregroundStyle(c)
    }
}

// MARK: - 数组作业概览

struct ArrayOverview: View {
    var job: RemoteJob
    var array: ArrayInfo
    var onOpen: (RemoteJob) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 18) {
                stat(L("总数", "Total"), array.total, Theme.text)
                stat(L("成功", "Done"), array.done, Theme.mint)
                stat(L("失败", "Failed"), array.failed, array.failed > 0 ? Theme.coral : Theme.tertiary)
                stat(L("运行中", "Running"), array.running.count, Theme.cyan)
                stat(L("排队中", "Pending"), array.pending, Theme.amber)
                Spacer()
                Text(array.accurate ? L("已结束数量来自 sacct", "Finished counts from sacct") : L("已结束数量为 ServerIsland 运行期间观察到的", "Finished counts observed by ServerIsland"))
                    .font(Theme.rounded(10)).foregroundStyle(Theme.tertiary)
            }
            ArrayBar(array: array, height: 7)
            Text(L("运行中的元素 · 点击查看日志", "Running elements · click for log"))
                .font(Theme.rounded(10.5, .semibold)).foregroundStyle(Theme.secondary)
            MaybeScroll(showsIndicators: false) {
                VStack(spacing: 4) {
                    if array.running.isEmpty {
                        Text(L("暂时没有正在运行的元素", "No running elements right now")).font(Theme.rounded(11)).foregroundStyle(Theme.tertiary).padding(.top, 20)
                    }
                    ForEach(array.running) { e in
                        Button { onOpen(e) } label: {
                            HStack(spacing: 10) {
                                Text("[\(e.arrayTask ?? "?")]").font(Theme.mono(11, .semibold)).foregroundStyle(Theme.cyan)
                                    .frame(width: 54, alignment: .leading)
                                Text("#\(e.id)").font(Theme.mono(10)).foregroundStyle(Theme.tertiary)
                                Text(e.detail).font(Theme.rounded(10.5)).foregroundStyle(Theme.secondary).lineLimit(1)
                                Spacer()
                                Text(e.elapsed.map(Format.duration) ?? "").font(Theme.rounded(10.5).monospacedDigit())
                                    .foregroundStyle(Theme.secondary)
                                Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold)).foregroundStyle(Theme.tertiary)
                            }
                            .padding(.horizontal, 10).padding(.vertical, 6)
                            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.card))
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .frame(maxHeight: .infinity)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.white.opacity(0.03)))
    }

    private func stat(_ label: String, _ v: Int, _ c: Color) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            Text("\(v)").font(Theme.rounded(17, .semibold).monospacedDigit()).foregroundStyle(c)
            Text(label).font(Theme.rounded(10)).foregroundStyle(Theme.tertiary)
        }
    }
}

/// 数组进度条：成功（绿）+ 失败（红）+ 运行中（蓝）+ 排队（底色）。
struct ArrayBar: View {
    var array: ArrayInfo
    var height: CGFloat = 4

    var body: some View {
        GeometryReader { geo in
            let total = max(Double(array.total), 1)
            let w = geo.size.width
            HStack(spacing: 1) {
                seg(Theme.mint, Double(array.done) / total * w)
                seg(Theme.coral, Double(array.failed) / total * w)
                seg(Theme.cyan.opacity(0.55), Double(array.running.count) / total * w)
                Spacer(minLength: 0)
            }
            .background(Capsule().fill(Theme.track))
            .clipShape(Capsule())
        }
        .frame(height: height)
    }

    @ViewBuilder
    private func seg(_ c: Color, _ w: CGFloat) -> some View {
        if w > 0.5 { Rectangle().fill(c).frame(width: max(w, 2)) }
    }

    /// 悬停说明：进度条每种颜色代表什么。
    static func legend(_ a: ArrayInfo) -> String {
        L("绿色 完成 \(a.done) · 红色 失败 \(a.failed) · 蓝色 运行中 \(a.running.count) · 灰色 排队 \(a.pending)",
          "Green done \(a.done) · Red failed \(a.failed) · Blue running \(a.running.count) · Gray pending \(a.pending)")
        + (a.accurate ? "" : L("\n完成数只包括 ServerIsland 运行期间看到结束的元素（sacct 不可用），可能偏少。",
                               "\nOnly elements seen finishing while ServerIsland was running are counted (no sacct), so this may be low."))
    }
}

/// 数组状态文字，颜色和 ArrayBar 的分段一致：运行蓝、排队灰、失败红。
struct ArrayStatusText: View {
    var array: ArrayInfo

    var body: some View {
        HStack(spacing: 0) {
            ForEach(Array(parts.enumerated()), id: \.offset) { i, p in
                if i > 0 { Text(" · ").foregroundStyle(Theme.tertiary.opacity(0.6)) }
                Text(p.0).foregroundStyle(p.1)
            }
        }
        .font(Theme.rounded(10))
        .lineLimit(1)
    }

    private var parts: [(String, Color)] {
        var r: [(String, Color)] = []
        if !array.running.isEmpty { r.append((L("运行 \(array.running.count)", "\(array.running.count) running"), Theme.cyan)) }
        if array.pending > 0 { r.append((L("排队 \(array.pending)", "\(array.pending) pending"), Theme.secondary)) }
        if array.failed > 0 { r.append((L("失败 \(array.failed)", "\(array.failed) failed"), Theme.coral)) }
        return r
    }
}

// MARK: - 全部任务（跨服务器）

/// 全部任务页的筛选条件。放在 IslandViewModel 里：从这里打开详情再返回时保持不变。
struct JobsFilter: Equatable {
    enum Status: String, CaseIterable {
        case all, running, pending, array
        var label: String {
            switch self {
            case .all: return L("全部", "All")
            case .running: return L("运行中", "Running")
            case .pending: return L("排队中", "Pending")
            case .array: return L("数组", "Arrays")
            }
        }
    }
    var query = ""
    /// nil：全部服务器。
    var host: UUID?
    var status: Status = .all
    var starredOnly = false

    func matches(_ j: RemoteJob, starred: Bool, hostName: String) -> Bool {
        if starredOnly && !starred { return false }
        switch status {
        case .all: break
        case .running: if j.runningUnits == 0 { return false }
        case .pending: if j.pendingUnits == 0 { return false }
        case .array: if j.array == nil { return false }
        }
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return true }
        return j.name.lowercased().contains(q) || j.id.lowercased().contains(q) || j.detail.lowercased().contains(q)
            || hostName.lowercased().contains(q) || (j.workdir?.lowercased().contains(q) ?? false)
    }
}

/// 所有服务器的任务集中在一页：按服务器分组（顺序同设置），每组里关注的任务在前。
/// 只用轮询已经拿到的快照，不额外连接服务器。
struct AllJobsPage: View {
    @ObservedObject var engine: MonitorEngine
    @ObservedObject var store: ConfigStore
    @Binding var filter: JobsFilter
    var onBack: () -> Void
    var onOpen: (HostConfig, RemoteJob) -> Void
    @FocusState private var searching: Bool

    private struct Group: Identifiable {
        var host: HostConfig
        var snap: HostSnapshot
        var jobs: [RemoteJob]
        var id: UUID { host.id }
    }

    var body: some View {
        let hosts = engine.orderedHosts
        let groups: [Group] = hosts.compactMap { host, snap in
            if let h = filter.host, h != host.id { return nil }
            let jobs = engine.displayJobs(host.id, snap.jobs)
                .filter { filter.matches($0, starred: engine.isStarred(host.id, $0.id), hostName: host.displayName) }
            return jobs.isEmpty ? nil : Group(host: host, snap: snap, jobs: jobs)
        }
        let count = groups.reduce(0) { $0 + $1.jobs.count }
        let starredCount = store.settings.starredJobs.filter { s in hosts.contains { $0.0.id == s.hostID } }.count
        VStack(alignment: .leading, spacing: 10) {
            PageHeader(title: L("全部任务", "All Jobs"),
                       subtitle: L("\(hosts.count) 台服务器 · 运行 \(engine.totalRunning) · 排队 \(engine.totalPending)",
                                   "\(hosts.count) servers · \(engine.totalRunning) running · \(engine.totalPending) pending"),
                       onBack: onBack) { searchField }
            HStack(spacing: 6) {
                ForEach(JobsFilter.Status.allCases, id: \.self) { f in
                    chip(f.label, selected: filter.status == f) { filter.status = f }
                }
                Rectangle().fill(Color.white.opacity(0.1)).frame(width: 1, height: 14).padding(.horizontal, 2)
                chip(L("关注", "Starred") + (starredCount > 0 ? " \(starredCount)" : ""), icon: filter.starredOnly ? "star.fill" : "star",
                     selected: filter.starredOnly, tint: Theme.amber) { filter.starredOnly.toggle() }
                    .help(L("只看关注的任务", "Show starred jobs only"))
                Spacer(minLength: 6)
                HostFilterMenu(hosts: hosts.map(\.0), selected: $filter.host)
                Text(L("\(count) 项", "\(count) items")).font(Theme.rounded(10.5).monospacedDigit()).foregroundStyle(Theme.tertiary)
                    .fixedSize()
            }
            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                MaybeScroll(showsIndicators: false) {
                    VStack(alignment: .leading, spacing: IslandLayout.jobGap) {
                        if groups.isEmpty { empty }
                        ForEach(groups) { g in
                            groupHeader(g)
                            ForEach(g.jobs) { job in
                                let starred = engine.isStarred(g.host.id, job.id)
                                JobRowButton(job: job, kind: g.host.kind, now: ctx.date, sampledAt: g.host.paused ? nil : g.snap.lastUpdate,
                                             starred: starred, onStar: { engine.toggleStar(hostID: g.host.id, job: job) }) {
                                    onOpen(g.host, job)
                                }
                                .frame(height: IslandLayout.jobRow)
                                .opacity(g.host.paused ? 0.45 : 1)
                            }
                        }
                    }
                    .padding(.horizontal, 6)
                }
            }
            .frame(maxHeight: .infinity)
            .clipped()
        }
    }

    private var searchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").font(.system(size: 10)).foregroundStyle(Theme.tertiary)
            if IslandPreview.active {
                // 离屏渲染画不出文本框
                Text(filter.query.isEmpty ? L("搜索名称 / 作业号 / 目录", "Search name / ID / folder") : filter.query)
                    .font(Theme.rounded(11.5)).foregroundStyle(filter.query.isEmpty ? Theme.tertiary : Theme.text)
                    .frame(width: 170, alignment: .leading)
            } else {
                TextField(L("搜索名称 / 作业号 / 目录", "Search name / ID / folder"), text: $filter.query)
                    .textFieldStyle(.plain)
                    .font(Theme.rounded(11.5))
                    .foregroundStyle(Theme.text)
                    .focused($searching)
                    .frame(width: 170)
            }
            if !filter.query.isEmpty {
                Button { filter.query = "" } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 10)).foregroundStyle(Theme.tertiary)
                }.buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 5)
        .background(Capsule().fill(Color.white.opacity(searching ? 0.12 : 0.07)))
    }

    private func chip(_ title: String, icon: String? = nil, selected: Bool, tint: Color = Theme.text, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 4) {
                if let icon { Image(systemName: icon).font(.system(size: 9, weight: .bold)) }
                Text(title).font(Theme.rounded(10.5, .semibold).monospacedDigit())
            }
            .foregroundStyle(selected ? tint : Theme.tertiary)
            .padding(.horizontal, 10).padding(.vertical, 3.5)
            .background(Capsule().fill(selected ? (tint == Theme.text ? Color.white.opacity(0.14) : tint.opacity(0.18)) : Color.white.opacity(0.04)))
            .fixedSize()
        }
        .buttonStyle(.plain)
    }

    /// 分组标题：服务器名、类型、这台的运行 / 排队数。
    private func groupHeader(_ g: Group) -> some View {
        HStack(spacing: 7) {
            StatusDot(color: g.host.paused ? Theme.tertiary : Theme.color(for: g.snap.health), size: 5.5)
            Text(g.host.displayName).font(Theme.rounded(11, .semibold)).foregroundStyle(Theme.secondary).lineLimit(1)
            KindBadge(kind: g.host.kind)
            if g.snap.runningCount + g.snap.pendingCount > 0 {
                JobCountChips(running: g.snap.runningCount, pending: g.snap.pendingCount, dimmed: g.host.paused)
            }
            Spacer(minLength: 6)
            if let t = g.snap.lastUpdate {
                Text(Format.relative(t)).font(Theme.rounded(10).monospacedDigit()).foregroundStyle(Theme.tertiary)
            }
        }
        .frame(height: 20)
        .padding(.top, 2)
    }

    private var empty: some View {
        VStack(spacing: 8) {
            PetView(mood: .idle, size: 26)
            if filter.starredOnly && filter.query.isEmpty && filter.status == .all {
                Text(L("还没有关注的任务", "No starred jobs yet")).font(Theme.rounded(12, .semibold)).foregroundStyle(Theme.secondary)
                Text(L("鼠标移到任务行上，点名称旁边的 ☆ 就能关注", "Hover over a job and click the ☆ next to its name"))
                    .font(Theme.rounded(10.5)).foregroundStyle(Theme.tertiary)
            } else {
                Text(L("没有匹配的任务", "No matching jobs")).font(Theme.rounded(12, .semibold)).foregroundStyle(Theme.secondary)
                if filter != JobsFilter() {
                    Button { filter = JobsFilter() } label: {
                        Text(L("清除筛选", "Clear Filters")).font(Theme.rounded(10.5, .semibold))
                            .padding(.horizontal, 10).padding(.vertical, 3.5)
                            .background(Capsule().fill(Theme.cyan.opacity(0.16)))
                            .foregroundStyle(Theme.cyan)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 50)
    }
}

/// 全部任务页右上的服务器筛选菜单。
struct HostFilterMenu: View {
    var hosts: [HostConfig]
    @Binding var selected: UUID?

    var body: some View {
        if IslandPreview.active { label } else { menu }
    }

    private var menu: some View {
        Menu {
            Button { selected = nil } label: {
                Label(L("全部服务器", "All Servers"), systemImage: selected == nil ? "checkmark" : "")
            }
            Divider()
            ForEach(hosts) { h in
                Button { selected = h.id } label: {
                    Label(h.displayName, systemImage: selected == h.id ? "checkmark" : "")
                }
            }
        } label: { label }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help(L("按服务器筛选", "Filter by server"))
    }

    private var label: some View {
        HStack(spacing: 4) {
            Image(systemName: "server.rack").font(.system(size: 9, weight: .semibold))
            Text(hosts.first { $0.id == selected }?.displayName ?? L("全部服务器", "All Servers"))
                .font(Theme.rounded(10.5, .semibold)).lineLimit(1).truncationMode(.middle)
                .frame(maxWidth: 120)
            Image(systemName: "chevron.down").font(.system(size: 7.5, weight: .bold))
        }
        .foregroundStyle(selected == nil ? Theme.tertiary : Theme.cyan)
        .padding(.horizontal, 9).frame(height: 21)
        .background(Capsule().fill(selected == nil ? Color.white.opacity(0.04) : Theme.cyan.opacity(0.16)))
        .fixedSize()
    }
}

// MARK: - Slurm 分区空闲资源

struct PartitionsPage: View {
    var host: HostConfig
    @ObservedObject var engine: MonitorEngine
    var onBack: () -> Void
    var preloaded: [PartitionStat]?
    @State private var stats: [PartitionStat]?
    @State private var at = Date()
    @State private var error: String?
    @State private var loading = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PageHeader(title: L("分区空闲资源", "Free Partition Resources"), subtitle: host.displayName + " · " + subtitle, onBack: onBack) {
                IslandIconButton(systemName: "arrow.clockwise", help: L("重新查询（sinfo）", "Query again (sinfo)"), spinning: loading) {
                    Task { await load(force: true) }
                }
            }
            if let stats {
                let gIdle = stats.reduce(0) { $0 + $1.gpuIdle }, gTotal = stats.reduce(0) { $0 + $1.gpuTotal }
                HStack(spacing: 16) {
                    total(L("空闲 \(gpuLabel)", "Idle \(gpuLabel)"), gIdle, gTotal, Theme.cyan)
                    total(L("空闲 CPU", "Idle CPU"), stats.reduce(0) { $0 + $1.cpuIdle }, stats.reduce(0) { $0 + $1.cpuTotal }, Theme.mint)
                    Spacer()
                    Text(L("同一节点可能属于多个分区，合计会重复计算", "Nodes in several partitions are counted more than once")).font(Theme.rounded(9.5)).foregroundStyle(Theme.tertiary)
                }
            }
            MaybeScroll(showsIndicators: false) {
                VStack(spacing: 5) {
                    if let error {
                        Label(error, systemImage: "exclamationmark.triangle.fill")
                            .font(Theme.rounded(11)).foregroundStyle(Theme.amber).padding(.top, 30)
                    } else if let stats {
                        ForEach(stats) { row($0) }
                    } else {
                        ProgressView().controlSize(.small).padding(.top, 40)
                    }
                }
            }
            .frame(maxHeight: .infinity)
        }
        .onAppear { if let preloaded { stats = preloaded } }
        .task { if preloaded == nil { await load(force: false) } }
    }

    private var subtitle: String {
        if loading && stats == nil { return L("查询中…", "Querying…") }
        return stats == nil ? L("按需查询，不参与轮询", "On demand, not polled") : L("查询于 \(Format.relative(at)) · 按空闲 \(gpuLabel) 排序", "queried \(Format.relative(at)) · sorted by idle \(gpuLabel)s")
    }

    /// 整个集群的加速卡名字（GPU / DCU / GPU/DCU）。
    private var gpuLabel: String { PartitionStat.label((stats ?? []).reduce(into: Set<String>()) { $0.formUnion($1.gpuKinds) }) }

    private func load(force: Bool) async {
        loading = true
        let r = await engine.fetchPartitions(host: host, force: force)
        stats = r.stats; at = r.at; error = r.error
        loading = false
    }

    private func total(_ label: String, _ idle: Int, _ all: Int, _ c: Color) -> some View {
        HStack(spacing: 5) {
            Text(label).font(Theme.rounded(10.5)).foregroundStyle(Theme.tertiary)
            Text("\(idle)").font(Theme.rounded(14, .semibold).monospacedDigit()).foregroundStyle(idle > 0 ? c : Theme.tertiary)
            Text("/ \(all)").font(Theme.rounded(10.5).monospacedDigit()).foregroundStyle(Theme.tertiary)
        }
    }

    private func row(_ p: PartitionStat) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Text(p.name).font(Theme.rounded(12.5, .semibold)).foregroundStyle(p.available ? Theme.text : Theme.tertiary)
                if p.isDefault {
                    Text(L("默认", "default")).font(Theme.rounded(8.5, .bold)).foregroundStyle(Theme.violet)
                        .padding(.horizontal, 5).padding(.vertical, 1.5).background(Capsule().fill(Theme.violet.opacity(0.15)))
                }
                if !p.available {
                    Text(L("已关闭", "down")).font(Theme.rounded(8.5, .bold)).foregroundStyle(Theme.coral)
                        .padding(.horizontal, 5).padding(.vertical, 1.5).background(Capsule().fill(Theme.coral.opacity(0.15)))
                }
                if !p.gpuModels.isEmpty {
                    Text(p.gpuModels.sorted().joined(separator: ", ")).font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
                }
                Spacer()
                Text(nodeText(p)).font(Theme.rounded(10).monospacedDigit()).foregroundStyle(Theme.tertiary)
                if !p.timeLimit.isEmpty {
                    Text(L("上限 ", "limit ") + (p.timeLimit == "infinite" ? L("不限", "none") : p.timeLimit)).font(Theme.rounded(10).monospacedDigit())
                        .foregroundStyle(Theme.tertiary)
                }
            }
            HStack(spacing: 14) {
                if p.gpuTotal > 0 { meter(p.gpuLabel, p.gpuIdle, p.gpuTotal, Theme.cyan) }
                meter("CPU", p.cpuIdle, p.cpuTotal, Theme.mint)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Theme.card))
        .opacity(p.available ? 1 : 0.6)
    }

    private func nodeText(_ p: PartitionStat) -> String {
        var s = L("节点 空闲 \(p.idleNodes) · 部分 \(p.mixNodes) · 满 \(p.allocNodes)", "nodes idle \(p.idleNodes) · mixed \(p.mixNodes) · full \(p.allocNodes)")
        if p.downNodes > 0 { s += L(" · 不可用 \(p.downNodes)", " · down \(p.downNodes)") }
        return s
    }

    private func meter(_ label: String, _ idle: Int, _ total: Int, _ c: Color) -> some View {
        HStack(spacing: 6) {
            Text(label).font(Theme.rounded(9.5, .semibold)).foregroundStyle(Theme.tertiary).fixedSize().frame(minWidth: 26, alignment: .leading)
            SlimBar(fraction: total > 0 ? Double(idle) / Double(total) : 0, fill: AnyShapeStyle(c), height: 5)
            Text(L("空闲 \(idle)/\(total)", "idle \(idle)/\(total)")).font(Theme.rounded(10.5, .medium).monospacedDigit())
                .foregroundStyle(idle > 0 ? Theme.secondary : Theme.tertiary)
                .frame(width: 92, alignment: .trailing)
        }
    }
}
