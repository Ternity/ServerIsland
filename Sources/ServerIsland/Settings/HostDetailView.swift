import SwiftUI

struct HostDetailView: View {
    @Binding var host: HostConfig
    @ObservedObject var engine: MonitorEngine
    var globalInterval: Int = 60
    /// 全部服务器（选跳板机用）。
    var allHosts: [HostConfig] = []
    var onDelete: () -> Void

    @State private var diagnosing = false
    @State private var diagnosis = ""
    @State private var detecting = false
    @State private var detectMessage = ""
    @State private var confirmDelete = false
    @State private var newPID = ""
    @State private var masterAlive: Bool?
    @State private var loginError: String?
    @State private var checkingEnv = false
    @State private var envResult: [(String, String?)] = []

    private var sshEntry: SSHConfigEntry? {
        SSHConfigParser.load().first { $0.alias == host.alias }
    }

    private var snap: HostSnapshot? { engine.snapshots[host.id] }

    var body: some View {
        Form {
            Section { hero }
            connection
            environment
            polling
            display
            apps
            alerts
            if host.kind == .direct || host.slurmAlsoProcesses { watched }
            danger
            advanced
            diagnostics
        }
        .formStyle(.grouped)
        .navigationTitle(host.displayName)
        .task(id: host.auth) { if host.auth.needsLogin { await checkMaster() } }
        .confirmationDialog(L("移除 \(host.displayName)？", "Remove \(host.displayName)?"), isPresented: $confirmDelete) {
            Button(L("移除", "Remove"), role: .destructive, action: onDelete)
        } message: {
            Text(L("只会从 ServerIsland 中移除，不会修改 ~/.ssh/config。", "Only removes it from ServerIsland; ~/.ssh/config is not modified."))
        }
    }

    // MARK: - 连接

    private var connection: some View {
        Section {
            Picker(L("类型", "Type"), selection: $host.kind) {
                ForEach(HostKind.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)
            HStack {
                Button(detecting ? L("检测中…", "Detecting…") : L("自动检测类型", "Detect Type")) { detect() }
                    .disabled(detecting)
                if !detectMessage.isEmpty {
                    Text(detectMessage).font(.caption).foregroundStyle(.secondary)
                }
            }
            Picker(selection: Binding(get: { host.isManual }, set: { setManual($0) })) {
                Text(L("~/.ssh/config 别名", "~/.ssh/config alias")).tag(false)
                Text(L("手动填写", "Manual")).tag(true)
            } label: {
                HelpLabel(L("连接方式", "Connect Using"),
                          help: L("别名：用 ~/.ssh/config 里写好的 Host（推荐已经配置过 ssh 的用户）。\n手动填写：直接填地址、用户名、端口、私钥，不需要 ~/.ssh/config。ServerIsland 不会修改 ~/.ssh/config。",
                                  "Alias: use a Host from ~/.ssh/config (for people who already set up ssh).\nManual: fill in the address, user, port and key here — no ~/.ssh/config needed. ServerIsland never modifies ~/.ssh/config."))
            }
            .pickerStyle(.segmented)
            if host.isManual {
                ManualAddressFields(host: $host)
            } else {
                LabeledContent(L("别名", "Alias")) {
                    Text(host.alias).font(.system(.body, design: .monospaced)).textSelection(.enabled)
                }
            }
            JumpHostPicker(host: $host, allHosts: allHosts)
            AuthFields(host: $host)
            if host.auth.needsLogin {
                Picker(selection: $host.keepAlive) {
                    ForEach(KeepAlive.allCases) { Text($0.label).tag($0) }
                } label: {
                    HelpLabel(L("保持连接", "Keep Connection"),
                              help: L("登录后主连接在后台保持多久，期间无需再次输入。每 30 秒发心跳；网络切换或长时间合盖可能断开。",
                                      "How long the master connection stays open after logging in. Heartbeats every 30 s; switching networks or long sleep may drop it."))
                }
                HStack(spacing: 10) {
                    connectionBadge
                    Spacer()
                    Button(engine.loggingIn.contains(host.id) ? L("等待输入…", "Waiting…") : L("登录…", "Log In…")) { login() }
                        .buttonStyle(.borderedProminent)
                        .disabled(engine.loggingIn.contains(host.id))
                    Button(L("终端登录", "Terminal")) { SSHRunner.openLoginInTerminal(host: host) }
                    Button(L("断开", "Disconnect")) {
                        Task { await SSHRunner.closeMaster(host: host); await checkMaster() }
                    }
                    .disabled(masterAlive != true)
                }
                if let loginError {
                    Text(L("登录失败：", "Login failed: ") + loginError).font(.caption).foregroundStyle(.orange)
                }
            }
        } header: { SettingsHeader(title: L("连接", "Connection"), symbol: "network", tint: .blue) }
    }

    // MARK: - 环境

    private var environment: some View {
        Section {
            VStack(alignment: .leading, spacing: 6) {
                HelpLabel(L("连接后先执行", "Run First"),
                          help: L("每次查询前先执行这些命令，用来加载 Slurm、CUDA 等环境。例如：\nsource ~/software/slurm/env.sh\nmodule load slurm\nexport PATH=$HOME/software/slurm/install/bin:$PATH",
                                  "Commands run before every query, to load Slurm, CUDA, etc. For example:\nsource ~/software/slurm/env.sh\nmodule load slurm\nexport PATH=$HOME/software/slurm/install/bin:$PATH"))
                TextEditor(text: $host.commands.preamble)
                    .font(.system(size: 11.5, design: .monospaced))
                    .frame(minHeight: 54, maxHeight: 110)
                    .scrollContentBackground(.hidden)
                    .padding(4)
                    .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.05)))
                    .overlay(alignment: .topLeading) {
                        if host.commands.preamble.isEmpty {
                            Text("source ~/software/slurm/env.sh")
                                .font(.system(size: 11.5, design: .monospaced))
                                .foregroundStyle(.tertiary)
                                .padding(.horizontal, 9).padding(.vertical, 4)
                                .allowsHitTesting(false)
                        }
                    }
            }
            Toggle(isOn: $host.importShellPath) {
                HelpLabel(L("补全 PATH", "Import PATH from rc files"),
                          help: L("非交互登录不会执行 ~/.bashrc 中“非交互就 return”之后的内容，装在家目录下的程序会找不到。开启后，把 ~/.bashrc 等文件里 PATH= 行中存在的目录追加到 PATH 末尾，不会启动交互式 shell。",
                                  "Non-interactive logins skip most of ~/.bashrc, so programs installed under your home directory may not be found. When on, existing directories from PATH= lines in ~/.bashrc etc. are appended to PATH, without starting an interactive shell."))
            }
            HStack(alignment: .top) {
                Button(checkingEnv ? L("检查中…", "Checking…") : L("检查命令", "Check Commands")) { checkEnv() }
                    .disabled(checkingEnv)
                if !envResult.isEmpty {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(envResult, id: \.0) { name, path in
                            HStack(spacing: 6) {
                                Image(systemName: path == nil ? "xmark.circle.fill" : "checkmark.circle.fill")
                                    .foregroundStyle(path == nil ? Color.secondary : Color.green)
                                Text(name).font(.system(.callout, design: .monospaced))
                                Text(path ?? L("未找到", "not found"))
                                    .font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                                    .lineLimit(1).truncationMode(.middle)
                            }
                        }
                    }
                    .padding(.leading, 8)
                }
            }
        } header: { SettingsHeader(title: L("环境", "Environment"), symbol: "shippingbox.fill", tint: .brown) }
    }

    // MARK: - 轮询

    private var polling: some View {
        Section {
            Toggle(isOn: $host.paused) {
                HelpLabel(L("暂停自动刷新", "Pause Auto Refresh"),
                          help: L("仍然显示在刘海里，但不会自动连接；需要时在卡片上点“刷新一次”。",
                                  "Stays in the island but is never contacted automatically; use “Refresh Once” on its card."))
            }
            IntervalPicker(title: L("轮询间隔", "Refresh Interval"), seconds: $host.interval, allowsInherit: true,
                           inheritedValue: globalInterval)
                .disabled(host.paused)
        } header: { SettingsHeader(title: L("轮询", "Refresh"), symbol: "arrow.triangle.2.circlepath", tint: .cyan) }
    }

    // MARK: - 显示内容

    private var display: some View {
        Section {
            Toggle(isOn: $host.watchJobs) {
                HelpLabel(host.kind == .slurm ? L("我的 Slurm 作业", "My Slurm Jobs") : host.kind == .pbs ? L("我的 PBS 作业", "My PBS Jobs") : L("我的进程", "My Processes"),
                          help: host.kind == .slurm ? L("用 squeue 读取你的作业，数组作业会合并成一行。", "Reads your jobs with squeue; job arrays are merged into one row.")
                              : host.kind == .pbs ? L("用 qselect + qstat -f 读取你的作业（PBS Pro / OpenPBS / Torque），数组作业会合并成一行。", "Reads your jobs with qselect + qstat -f (PBS Pro / OpenPBS / Torque); job arrays are merged into one row.")
                                                    : L("用 ps 读取你的进程，按下面的规则过滤。", "Reads your processes with ps, filtered by the rules below."))
            }
            if host.kind.isScheduler {
                Toggle(isOn: $host.slurmAlsoProcesses) {
                    HelpLabel(L("同时显示直接运行的进程", "Also Show Direct Processes"),
                              help: L("适合单机装了作业调度系统的工作站：既提交作业，也直接运行程序。属于作业的进程不会重复显示。集群登录节点一般不需要。",
                                      "For single-machine workstations with a scheduler where you also run programs directly. Processes that belong to jobs are not listed twice. Usually not needed on cluster login nodes."))
                }
            }
            LabeledContent {
                TextField("", text: $host.pathFilter, prompt: Text("~/projects/LLZO"), axis: .vertical)
                    .lineLimit(1...4)
                    .font(.system(.body, design: .monospaced))
            } label: {
                HelpLabel(L("只看这些目录", "Only These Folders"),
                          help: L("只显示工作目录在这些文件夹（含子文件夹）里的任务，每行一个，可以用 ~。作业按提交时的工作目录判断，进程按当前所在目录判断。留空显示全部。手动监听的进程和进度文件不受影响。",
                                  "Only show tasks whose working directory is inside one of these folders (subfolders included), one per line; ~ works. Jobs use their submit directory, processes their current directory. Empty shows everything. Watched processes and progress files are not affected."))
            }
            if host.kind == .direct || host.slurmAlsoProcesses {
                LabeledContent {
                    TextField("", text: $host.processFilter).font(.system(.body, design: .monospaced))
                } label: {
                    HelpLabel(L("包含", "Include"),
                              help: L("只显示命令行匹配这个正则的进程；留空显示全部。占用 GPU 的进程总会显示。",
                                      "Only processes whose command line matches this regex are shown; empty shows all. GPU processes are always shown."))
                }
                LabeledContent {
                    HStack {
                        TextField("", text: $host.processExclude).font(.system(.body, design: .monospaced))
                        if host.processExclude != HostConfig.defaultExclude {
                            Button(L("恢复默认", "Reset")) { host.processExclude = HostConfig.defaultExclude }.buttonStyle(.link)
                        }
                    }
                } label: {
                    HelpLabel(L("排除", "Exclude"),
                              help: L("匹配这个正则的进程一律忽略，默认排除系统服务、桌面和编辑器后台。运行不到 30 秒就结束的进程不提醒。",
                                      "Processes matching this regex are ignored (system services, desktop, editor backends by default). Processes that end within 30 s are not reported."))
                }
            }
            Toggle("CPU", isOn: $host.showCPU)
            Toggle(L("内存", "Memory"), isOn: $host.showMemory)
            Toggle(isOn: $host.showGPU) {
                HelpLabel("GPU", help: L("通过 nvidia-smi 读取。Slurm 登录节点上通常看不到计算节点的 GPU。",
                                         "Read with nvidia-smi. On Slurm login nodes the compute nodes' GPUs are usually not visible."))
            }
            Toggle(isOn: $host.showDisk) {
                HelpLabel(L("磁盘", "Disk"), help: L("超过提醒阈值时提醒一次；路径可以在“高级”里修改。",
                                                    "Alerts once when usage passes the threshold; paths can be changed under Advanced."))
            }
            Toggle(isOn: $host.readProgressFiles) {
                HelpLabel(L("进度文件", "Progress Files"),
                          help: L("读取 ~/.serverisland/progress/*.json，用来显示脚本自己上报的进度。",
                                  "Reads ~/.serverisland/progress/*.json to show progress reported by your scripts."))
            }
        } header: { SettingsHeader(title: L("显示内容", "Display"), symbol: "rectangle.3.group.fill", tint: .teal) }
    }

    // MARK: - 计算软件

    private var apps: some View {
        Section {
            Toggle(isOn: $host.detectApps) {
                HelpLabel(L("识别计算进度", "Detect Progress"),
                          help: L("在任务的工作目录里找特征文件：\nloss.out + nep.in → NEP 训练\nloss.out + gnep.in → GNEP 训练\n带表头的 loss.out（可在 output/ 里）→ TorchNEP 训练\nlogs/*.log → MACE 训练\nlcurve.out → DeePMD 训练\nrun.in + neighbor.out / thermo.out → GPUMD 模拟\n与任务列表同一次轮询完成，只读文件末尾。",
                                  "Looks for marker files in each job's working directory:\nloss.out + nep.in → NEP training\nloss.out + gnep.in → GNEP training\nloss.out with a header (may be in output/) → TorchNEP training\nlogs/*.log → MACE training\nlcurve.out → DeePMD training\nrun.in + neighbor.out / thermo.out → GPUMD MD\nDone in the same poll, reading only file tails."))
            }
            if host.detectApps {
                LabeledContent {
                    HStack {
                        TextField("", text: $host.appProcessNames).font(.system(.body, design: .monospaced))
                        if host.appProcessNames != HostConfig.defaultAppProcessNames {
                            Button(L("恢复默认", "Reset")) { host.appProcessNames = HostConfig.defaultAppProcessNames }.buttonStyle(.link)
                        }
                    }
                } label: {
                    HelpLabel(L("进程名", "Process Names"),
                              help: L("名字匹配的进程会被检查工作目录；另外，占用 GPU、CPU ≥ 50% 或属于 Slurm 作业的进程也会检查。",
                                      "Processes with matching names have their working directory checked; so do processes using a GPU, ≥ 50% CPU, or belonging to a Slurm job."))
                }
            }
            Text(L("支持 NEP / GNEP 训练和 GPUMD 模拟。", "Supports NEP / GNEP training and GPUMD simulations."))
                .font(.caption).foregroundStyle(.secondary)
        } header: { SettingsHeader(title: L("计算软件", "Scientific Software"), symbol: "atom", tint: .mint) }
    }

    // MARK: - 提醒 / 监听 / 危险操作

    private var alerts: some View {
        Section {
            Toggle(isOn: $host.muted) {
                HelpLabel(L("静音", "Mute"), help: L("不弹提示、不发通知、不响铃，数据照常刷新。刘海卡片上的铃铛也能切换。",
                                                    "No pop-ups, notifications or sounds; data still refreshes. Also toggled by the bell on the island card."))
            }
        } header: { SettingsHeader(title: L("提醒", "Alerts"), symbol: "bell.fill", tint: .orange) }
    }

    private var watched: some View {
        Section {
            if host.watched.isEmpty {
                Text(L("还没有监听的进程。也可以在刘海卡片上点 👁 从高负载进程里挑。监听的进程不受过滤规则影响，结束时一定会提醒。",
                       "No watched processes yet. You can also pick one with 👁 on the island card. Watched processes ignore filters and always alert when they end."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            ForEach(host.watched) { w in
                HStack {
                    Image(systemName: "eye.fill").foregroundStyle(.green)
                    Text(w.name.isEmpty ? "PID \(w.pid)" : w.name)
                    Text("pid \(w.pid)").font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                    Spacer()
                    Button(L("移除", "Remove")) { host.watched.removeAll { $0.pid == w.pid } }.buttonStyle(.link)
                }
            }
            HStack {
                TextField(L("输入 PID", "PID"), text: $newPID)
                    .font(.system(.body, design: .monospaced))
                    .frame(width: 140)
                    .onSubmit(addPID)
                Button(L("添加", "Add"), action: addPID)
                    .disabled(Int(newPID.trimmingCharacters(in: .whitespaces)) == nil)
            }
        } header: { SettingsHeader(title: L("监听的进程", "Watched Processes"), symbol: "eye.fill", tint: .green) }
    }

    private var danger: some View {
        Section {
            Toggle(isOn: $host.allowCancel) {
                HelpLabel(L("允许在刘海里取消任务", "Allow Cancelling from the Island"),
                          help: L("在任务详情页按住按钮 1.5 秒并再确认一次，才会执行 \(host.kind.cancelCommand)；执行前会再次核对任务名称、所有者和启动时间。",
                                  "Hold the button for 1.5 s on the job page and confirm again to run \(host.kind.cancelCommand). The job's name, owner and start time are re-checked first."))
            }
        } header: { SettingsHeader(title: L("危险操作", "Danger Zone"), symbol: "exclamationmark.triangle.fill", tint: .red) }
    }

    // MARK: - 高级

    private var advanced: some View {
        Section {
            DisclosureGroup(L("远程命令", "Remote Commands")) {
                CommandField(title: L("远端 shell", "Remote Shell"), hint: L("脚本通过 stdin 传入", "script is sent via stdin"),
                             text: $host.commands.shell, defaultValue: CommandSet.defaults.shell)
                if host.kind == .slurm {
                    CommandField(title: "squeue", hint: "%i|%j|%T|%M|%l|%D|%R|%P|%F|%K",
                                 text: $host.commands.squeue, defaultValue: CommandSet.defaults.squeue)
                    CommandField(title: "sacct", hint: L("{jobs} 替换为作业号", "{jobs} = job IDs"),
                                 text: $host.commands.sacct, defaultValue: CommandSet.defaults.sacct)
                }
                if host.kind == .pbs {
                    CommandField(title: "qstat", hint: L("输出自己作业的 qstat -f 全文", "prints qstat -f for your jobs"),
                                 text: $host.commands.qstat, defaultValue: CommandSet.defaults.qstat)
                }
                if host.kind == .direct || host.slurmAlsoProcesses {
                    CommandField(title: L("进程列表", "Processes"), hint: "pid etimes pcpu rss args",
                                 text: $host.commands.processes, defaultValue: CommandSet.defaults.processes)
                    CommandField(title: L("GPU 进程", "GPU Processes"), hint: "pid, used_memory(MB)",
                                 text: $host.commands.gpuApps, defaultValue: CommandSet.defaults.gpuApps)
                }
                CommandField(title: "GPU", hint: "index, name, util, mem.used, mem.total, temp",
                             text: $host.commands.gpu, defaultValue: CommandSet.defaults.gpu)
                CommandField(title: L("进度文件", "Progress Files"), hint: L("shell 通配符", "shell glob"),
                             text: $host.commands.progressGlob, defaultValue: CommandSet.defaults.progressGlob)
                CommandField(title: L("磁盘", "Disk"), hint: "df -P -k ~ /scratch",
                             text: $host.commands.disk, defaultValue: CommandSet.defaults.disk)
                Toggle(L("复用 SSH 连接", "Reuse SSH Connection"), isOn: $host.reuseConnection)
                    .disabled(host.auth.needsLogin)
            }
        } header: { SettingsHeader(title: L("高级", "Advanced"), symbol: "terminal.fill", tint: .gray) }
    }

    private var diagnostics: some View {
        Section {
            HStack {
                Button(diagnosing ? L("运行中…", "Running…") : L("测试连接并查看原始输出", "Test Connection & Show Raw Output")) { diagnose() }
                    .disabled(diagnosing)
                Spacer()
                Button(L("移除这台服务器", "Remove Server"), role: .destructive) { confirmDelete = true }
            }
            if !diagnosis.isEmpty {
                ScrollView {
                    Text(diagnosis)
                        .font(.system(size: 11, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                }
                .frame(height: 220)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.black.opacity(0.25)))
            }
        } header: { SettingsHeader(title: L("诊断", "Diagnostics"), symbol: "stethoscope", tint: .purple) }
    }

    // MARK: - 顶部概览

    private var mood: PetMood {
        switch snap?.health ?? .unknown {
        case .error: return .sad
        case .needsLogin: return .worried
        case .unknown: return .waiting
        case .ok: return (snap?.runningCount ?? 0) > 0 ? .working : .idle
        }
    }

    private var hero: some View {
        HStack(alignment: .center, spacing: 14) {
            PetView(mood: mood, size: 38)
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.black.opacity(0.85)))
            VStack(alignment: .leading, spacing: 4) {
                TextField(L("显示名称", "Display Name"), text: $host.displayName, prompt: Text(L("显示名称", "Display Name")))
                    .labelsHidden()
                    .textFieldStyle(.plain)
                    .font(.system(size: 19, weight: .bold, design: .rounded))
                HStack(spacing: 6) {
                    Text(host.isManual ? SSHHosts.summary(host) : host.alias).font(.system(.callout, design: .monospaced))
                    if !host.isManual, let e = sshEntry { Text(e.summary).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                    if let j = host.jumpHostID.flatMap({ id in allHosts.first { $0.id == id } }) {
                        Text(L("经 \(j.displayName)", "via \(j.displayName)")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                HStack(spacing: 6) {
                    chip(statusText, Theme.color(for: snap?.health ?? .unknown))
                    if let s = snap, case .ok = s.health {
                        chip(L("运行 \(s.runningCount)", "Running \(s.runningCount)"), Theme.mint)
                        chip(L("排队 \(s.pendingCount)", "Pending \(s.pendingCount)"), Theme.amber)
                        if let c = s.metrics.cpuPercent { chip("CPU \(Int(c))%", .secondary) }
                        if let g = s.gpus.first { chip("GPU \(Int(g.utilization))%", .secondary) }
                    }
                }
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 6) {
                HStack(spacing: 6) {
                    Text(L("启用", "Enabled")).font(.callout).foregroundStyle(.secondary)
                    Toggle("", isOn: $host.enabled).toggleStyle(.switch).labelsHidden()
                }
                Button(L("立即刷新", "Refresh Now")) { engine.refresh(host.id) }.controlSize(.small)
            }
            .fixedSize()
        }
        .padding(.vertical, 4)
    }

    private var statusText: String {
        guard host.enabled else { return L("已停用", "Disabled") }
        switch snap?.health ?? .unknown {
        case .ok: return L("在线", "Online") + (snap?.latency.map { " · \(Int($0 * 1000))ms" } ?? "")
        case .needsLogin(let m): return m
        case .error(let m): return m
        case .unknown: return L("连接中…", "Connecting…")
        }
    }

    private func chip(_ text: String, _ color: Color) -> some View {
        Text(text)
            .font(.system(size: 11, weight: .medium, design: .rounded))
            .foregroundStyle(color)
            .lineLimit(1)
            .fixedSize()
            .padding(.horizontal, 8).padding(.vertical, 2.5)
            .background(Capsule().fill(color.opacity(0.14)))
    }

    private var connectionBadge: some View {
        HStack(spacing: 6) {
            Circle().fill(masterAlive == true ? Color.green : (masterAlive == false ? Color.orange : Color.gray))
                .frame(width: 8, height: 8)
            Text(masterAlive == true ? L("已登录", "Logged in") : (masterAlive == false ? L("未登录", "Not logged in") : L("检查中…", "Checking…")))
                .font(.callout)
        }
    }

    // MARK: - 动作

    /// 切换"别名 / 手动填写"：切到手动时把别名里能看出来的信息带过去。
    private func setManual(_ on: Bool) {
        guard on != host.isManual else { return }
        if on {
            let e = sshEntry
            var addr = e?.hostName ?? host.alias
            if e == nil, let at = addr.lastIndex(of: "@") {
                if host.user.isEmpty { host.user = String(addr[..<at]) }
                addr = String(addr[addr.index(after: at)...])
            }
            host.hostName = addr
            if host.user.isEmpty, let u = e?.user { host.user = u }
            if host.port == nil, let p = e?.port.flatMap(Int.init), p != 22 { host.port = p }
        } else {
            host.hostName = ""
        }
    }

    private func checkMaster() async {
        masterAlive = await SSHRunner.masterAlive(host: host)
    }

    private func login() {
        loginError = nil
        let h = host
        Task {
            loginError = await engine.login(h)
            await checkMaster()
        }
    }

    private func checkEnv() {
        checkingEnv = true
        let h = host
        Task {
            envResult = await engine.checkEnvironment(h)
            checkingEnv = false
        }
    }

    private func addPID() {
        let pid = newPID.trimmingCharacters(in: .whitespaces)
        guard Int(pid) != nil, !host.watched.contains(where: { $0.pid == pid }) else { return }
        host.watched.append(WatchedProcess(pid: pid, name: "", startedAt: nil))
        newPID = ""
    }

    private func diagnose() {
        diagnosing = true
        let h = host
        Task {
            let out = await engine.diagnose(h)
            diagnosis = out
            diagnosing = false
            engine.refresh(h.id)
        }
    }

    private func detect() {
        detecting = true
        detectMessage = ""
        let h = host
        Task {
            if let r = await engine.detect(h) {
                // 写回到"发起检测的那台服务器"，即使用户已经切到别的页面。
                // 本机就跑着 slurmd（单机 Slurm 工作站）：GPU 就在这台机器上，打开 GPU 显示。
                // "同时显示直接运行的进程"不自动打开（有调度系统时默认只看作业），只在提示里说一声。
                engine.setKind(h.id, r.slurm ? .slurm : r.pbs ? .pbs : .direct,
                               showGPU: r.slurm ? (r.gpu || r.localSlurmd) : nil)
                DebugLog.log("[\(h.displayName)] 自动检测类型：\(r.slurm ? "Slurm" : r.pbs ? "PBS" : "直连") gpu=\(r.gpu) slurmd=\(r.localSlurmd)")
                detectMessage = (r.slurm ? (r.localSlurmd ? L("检测到单机 Slurm（也直接运行程序的话，可以在“显示内容”里打开“同时显示直接运行的进程”）",
                                                                "Single-machine Slurm detected (if you also run programs directly, turn on “Also Show Direct Processes” under Display)")
                                                              : L("检测到 Slurm", "Slurm detected"))
                                 : r.pbs ? L("检测到 PBS", "PBS detected")
                                         : L("未检测到 Slurm / PBS，设为直连", "No Slurm / PBS found; set to direct"))
                    + (r.gpu ? " · nvidia-smi" : "") + (r.info.isEmpty ? "" : " · " + r.info)
            } else {
                detectMessage = L("连接失败，请先测试连接", "Connection failed; test the connection first")
            }
            detecting = false
        }
    }
}

struct CommandField: View {
    var title: String
    var hint: String
    @Binding var text: String
    var defaultValue: String
    var multiline = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title).font(.callout.weight(.medium))
                Text(hint).font(.caption).foregroundStyle(.secondary)
                Spacer()
                if text != defaultValue {
                    Button(L("恢复默认", "Reset")) { text = defaultValue }
                        .buttonStyle(.link).font(.caption)
                }
            }
            TextField("", text: $text, axis: multiline ? .vertical : .horizontal)
                .lineLimit(multiline ? 2...5 : 1...1)
                .font(.system(size: 11.5, design: .monospaced))
                .textFieldStyle(.roundedBorder)
        }
        .padding(.vertical, 2)
    }
}

struct AddHostSheet: View {
    var existing: [HostConfig]
    /// 直接打开"手动填写"（预览图用；~/.ssh/config 为空时也会自动切过去）。
    var startManual = false
    var onAdd: ([HostConfig]) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var entries: [SSHConfigEntry] = []
    @State private var picked: Set<String> = []
    @State private var manual = ""
    @State private var kind: HostKind = .direct
    @State private var auth: AuthMode = .key
    @State private var jumpHostID: UUID?
    /// false：从 ~/.ssh/config 选；true：手动填写。
    @State private var manualMode = false
    @State private var draft = HostConfig(alias: "")

    private var existingAliases: Set<String> { Set(existing.map(\.alias)) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("添加服务器", "Add Servers")).font(.title3.weight(.semibold))
            Picker("", selection: $manualMode) {
                Text(L("从 ~/.ssh/config 选择", "From ~/.ssh/config")).tag(false)
                Text(L("手动填写", "Enter Manually")).tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            if manualMode { manualForm } else { configList }

            HStack {
                Spacer()
                Button(L("取消", "Cancel")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("添加", "Add")) { add() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canAdd)
            }
        }
        .padding(20)
        .frame(width: 520, height: 600)
        .onAppear {
            entries = SSHConfigParser.load()
            // ~/.ssh/config 里什么都没有时，直接打开手动填写。
            if entries.isEmpty || startManual { manualMode = true }
        }
    }

    private var configList: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("从 ~/.ssh/config 中选择。连接使用系统 ssh，跳板机、密钥等配置都会生效。",
                   "Pick from ~/.ssh/config. Connections use the system ssh, so ProxyJump, keys, etc. all apply."))
                .font(.callout).foregroundStyle(.secondary)

            List(entries) { e in
                Toggle(isOn: Binding(get: { picked.contains(e.alias) },
                                     set: { if $0 { picked.insert(e.alias) } else { picked.remove(e.alias) } })) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(e.alias).font(.system(.body, design: .monospaced))
                        Text(e.summary).font(.caption).foregroundStyle(.secondary)
                    }
                }
                .disabled(existingAliases.contains(e.alias))
            }
            .frame(minHeight: 180)
            .overlay {
                if entries.isEmpty {
                    Text(L("~/.ssh/config 里没有找到具体的 Host 条目", "No concrete Host entries in ~/.ssh/config")).foregroundStyle(.secondary)
                }
            }

            TextField(L("或直接输入 ssh 目标，如 user@10.0.0.2", "Or type an ssh target, e.g. user@10.0.0.2"), text: $manual)
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
            Picker(L("默认类型", "Type"), selection: $kind) {
                ForEach(HostKind.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)
            Picker(selection: $auth) {
                ForEach(AuthMode.allCases) { Text($0.label).tag($0) }
            } label: { HelpLabel(L("认证方式", "Authentication"), help: AuthFields.help) }
            if !existing.isEmpty {
                JumpHostPicker(host: Binding(get: { var h = HostConfig(alias: ""); h.jumpHostID = jumpHostID; return h },
                                             set: { jumpHostID = $0.jumpHostID }),
                               allHosts: existing)
            }
            Text(L("添加后可以在详情页点“自动检测类型”。", "You can use “Detect Type” on the server page afterwards."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var manualForm: some View {
        Form {
            Section {
                LabeledContent(L("显示名称", "Name")) {
                    TextField("", text: $draft.displayName, prompt: Text(L("比如：实验室集群", "e.g. Lab Cluster")))
                        .labelsHidden().multilineTextAlignment(.trailing)
                }
                ManualAddressFields(host: $draft)
            }
            Section {
                Picker(L("类型", "Type"), selection: $draft.kind) {
                    ForEach(HostKind.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                AuthFields(host: $draft)
                if !existing.isEmpty { JumpHostPicker(host: $draft, allHosts: existing) }
            } footer: {
                Text(L("不会修改 ~/.ssh/config。需要密码的服务器添加后点“登录…”。",
                       "~/.ssh/config is not modified. For servers that need a password, click “Log In…” after adding."))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .padding(.horizontal, -20)
    }

    private var canAdd: Bool {
        manualMode ? draft.isManual : !(picked.isEmpty && manual.trimmingCharacters(in: .whitespaces).isEmpty)
    }

    private func add() {
        if manualMode {
            var h = draft
            h.hostName = h.hostName.trimmingCharacters(in: .whitespaces)
            h.user = h.user.trimmingCharacters(in: .whitespaces)
            h.identityFile = h.identityFile.trimmingCharacters(in: .whitespaces)
            // 别名只用于显示和"改回别名模式"时的兜底，写成 ssh 能识别的 user@host。
            h.alias = (h.user.isEmpty || h.hostName.contains("@") ? "" : h.user + "@") + h.hostName
            if h.displayName.trimmingCharacters(in: .whitespaces).isEmpty { h.displayName = SSHHosts.summary(h) }
            onAdd([h])
        } else {
            var aliases = entries.map(\.alias).filter { picked.contains($0) }
            let m = manual.trimmingCharacters(in: .whitespaces)
            if !m.isEmpty && !existingAliases.contains(m) { aliases.append(m) }
            onAdd(aliases.map { var h = HostConfig(alias: $0, kind: kind); h.auth = auth; h.jumpHostID = jumpHostID; return h })
        }
        dismiss()
    }
}
