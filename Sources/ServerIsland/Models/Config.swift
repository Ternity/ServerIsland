import Foundation

/// 服务器类型：决定"任务"从哪里来。
enum HostKind: String, Codable, CaseIterable, Identifiable {
    case slurm
    case pbs
    case direct

    var id: String { rawValue }
    var label: String {
        switch self {
        case .slurm: return L("Slurm 集群", "Slurm cluster")
        case .pbs: return L("PBS 集群", "PBS cluster")
        case .direct: return L("直连服务器", "Direct server")
        }
    }
    var badge: String {
        switch self {
        case .slurm: return "SLURM"
        case .pbs: return "PBS"
        case .direct: return "DIRECT"
        }
    }
    /// 有作业调度系统（Slurm / PBS）：任务是队列里的作业，而不是进程。
    var isScheduler: Bool { self != .direct }
    /// 作业调度系统的名字（界面文案用）。
    var schedulerName: String { self == .pbs ? "PBS" : "Slurm" }
    /// 取消作业 / 终止进程用的命令（界面文案用）。
    var cancelCommand: String {
        switch self {
        case .slurm: return "scancel"
        case .pbs: return "qdel"
        case .direct: return "kill -TERM"
        }
    }
}

/// 认证方式。需要密码 / 动态口令的机器由 App 弹原生对话框登录一次，建立 SSH 主连接（ControlMaster），
/// 之后的轮询都复用这条连接。密码默认不保存；用户勾选"记住密码"时只存进系统钥匙串（动态口令从不保存）。
/// 原始值 `key` / `interactive` 与旧版本兼容（旧版的"密码 / 动态口令"即现在的 `interactive`）。
enum AuthMode: String, Codable, CaseIterable, Identifiable {
    case key
    case password
    case keyPassword
    case interactive

    var id: String { rawValue }
    var label: String {
        switch self {
        case .key: return L("密钥 / ssh-agent", "SSH key / agent")
        case .password: return L("密码", "Password")
        case .keyPassword: return L("密钥 + 密码", "Key + password")
        case .interactive: return L("动态口令 / 二次验证", "One-time code / 2FA")
        }
    }
    /// 需要先弹窗登录、之后复用主连接。
    var needsLogin: Bool { self != .key }
    /// 可以把密码记进钥匙串（动态口令每次都不同，不能记）。
    var canRemember: Bool { self == .password || self == .keyPassword }
}

/// 需要密码登录的机器，主连接保持多久（期间不用再输密码）。
enum KeepAlive: String, Codable, CaseIterable, Identifiable {
    case h8, h24, d3, forever

    var id: String { rawValue }
    var label: String {
        switch self {
        case .h8: return L("8 小时", "8 hours")
        case .h24: return L("24 小时", "24 hours")
        case .d3: return L("3 天", "3 days")
        case .forever: return L("一直保持", "Until disconnected")
        }
    }
    var controlPersist: String {
        switch self {
        case .h8: return "8h"
        case .h24: return "24h"
        case .d3: return "72h"
        case .forever: return "yes"
        }
    }
}

/// 收起状态下刘海两侧"小翅膀"的显示策略。
enum CollapsedStyle: String, Codable, CaseIterable, Identifiable {
    case auto
    case always
    case never

    var id: String { rawValue }
    var label: String {
        switch self {
        case .auto: return L("有任务或异常时显示", "When busy or failing")
        case .always: return L("始终显示", "Always")
        case .never: return L("从不显示", "Never")
        }
    }
}

/// 远程执行的命令。默认值适用于大多数 Linux / Slurm 环境，
/// 对不上时可以在设置里逐项修改。
struct CommandSet: Codable, Equatable {
    /// 远端读取脚本的 shell，脚本通过 stdin 传入。用 login shell 以便加载 module / PATH。
    var shell = "bash -l -s"
    /// 在所有命令之前执行，例如 `module load slurm` 或 `export PATH=/opt/slurm/bin:$PATH`。
    var preamble = ""
    var squeue = #"squeue -u "$(id -un)" -h -o '%i|%j|%T|%M|%l|%D|%R|%P|%F|%K'"#
    /// `{jobs}` 会被替换为逗号分隔的作业号。只在作业从队列消失时调用一次。
    /// 最后一列 AllocTRES 是作业分到的资源（计费用）；没有这一列也能解析。
    var sacct = "sacct -n -X -P -o JobID,JobName,State,Elapsed,ExitCode,AllocTRES -j {jobs}"
    var gpu = "nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits"
    var gpuApps = "nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader,nounits"
    var processes = #"ps -u "$(id -un)" -o pid=,etimes=,pcpu=,rss=,args= --sort=-pcpu | head -n 300"#
    var progressGlob = "~/.serverisland/progress/*.json"
    /// 磁盘占用：df -P 输出。路径以空格分隔，默认看 home 所在分区。
    var disk = "df -P -k ~"
    /// PBS（PBS Pro / OpenPBS / Torque）：输出自己作业的 `qstat -f` 全文，由 App 的转换脚本解析。没有作业时什么都不输出。
    var qstat = #"SI_IDS=$(qselect -u "$(id -un)"); [ -z "$SI_IDS" ] || qstat -f -t $SI_IDS"#

    static let defaults = CommandSet()

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = CommandSet.defaults
        shell = try c.decodeIfPresent(String.self, forKey: .shell) ?? d.shell
        preamble = try c.decodeIfPresent(String.self, forKey: .preamble) ?? d.preamble
        squeue = try c.decodeIfPresent(String.self, forKey: .squeue) ?? d.squeue
        // 旧版默认命令没有数组列（%F|%K），自动升级。
        if squeue == #"squeue -u "$(id -un)" -h -o '%i|%j|%T|%M|%l|%D|%R|%P'"# { squeue = d.squeue }
        sacct = try c.decodeIfPresent(String.self, forKey: .sacct) ?? d.sacct
        // 旧版默认命令没有 AllocTRES（计费用），自动升级。
        if sacct == "sacct -n -X -P -o JobID,JobName,State,Elapsed,ExitCode -j {jobs}" { sacct = d.sacct }
        gpu = try c.decodeIfPresent(String.self, forKey: .gpu) ?? d.gpu
        gpuApps = try c.decodeIfPresent(String.self, forKey: .gpuApps) ?? d.gpuApps
        processes = try c.decodeIfPresent(String.self, forKey: .processes) ?? d.processes
        progressGlob = try c.decodeIfPresent(String.self, forKey: .progressGlob) ?? d.progressGlob
        disk = try c.decodeIfPresent(String.self, forKey: .disk) ?? d.disk
        qstat = try c.decodeIfPresent(String.self, forKey: .qstat) ?? d.qstat
    }
}

struct HostConfig: Codable, Identifiable, Equatable {
    var id = UUID()
    /// `~/.ssh/config` 里的 Host 别名，或任意 `ssh` 能识别的目标（如 user@1.2.3.4）。
    /// 手动填写的服务器（hostName 非空）只用它做显示和去重。
    var alias: String
    var displayName: String
    var kind: HostKind = .direct
    var auth: AuthMode = .key
    var enabled = true
    /// nil 表示跟随全局轮询间隔。
    var interval: Int?
    var showCPU = true
    var showMemory = true
    var showGPU = true
    var watchJobs = true
    var readProgressFiles = true
    /// 直连服务器：只显示命令行匹配该正则的进程。
    var processFilter = #"python|torchrun|accelerate|deepspeed|julia|matlab|Rscript|java|node|\./"#
    /// 直连服务器：命令行匹配该正则的进程一律忽略（系统服务、桌面、编辑器后台等）。
    var processExclude = HostConfig.defaultExclude

    static let defaultExclude = #"/usr/(lib|libexec|share)/|^/snap/|^/usr/sbin/|gnome-|Xorg|xdg-|ibus|gsd-|gvfs|update-manager|check-new-release|unattended-upgr|networkd-dispatcher|vscode-server|cursor-server|windsurf-server|\.codex/|codex app-server|jupyter-lab|ipykernel_launcher|pylsp|pyright|ruff|wireplumber|pipewire|dbus-daemon|update-notifier|systemd|at-spi|gcr-ssh|gnome-keyring|FlClash|clash"#
    /// 使用 App 管理的 SSH 连接复用（强烈建议开启，轮询几乎零开销）。
    var reuseConnection = true
    var commands = CommandSet()
    /// 静音：不弹提示、不发通知、不响铃（数据照常更新）。
    var muted = false
    /// 允许在刘海里取消这台机器上的任务（默认关闭，避免误操作）。
    var allowCancel = false
    /// 手动监听的进程（直连服务器）：不受过滤规则影响，结束时一定提醒。
    var watched: [WatchedProcess] = []
    var showDisk = true
    var keepAlive: KeepAlive = .h24
    /// 暂停自动刷新（仍然显示在刘海里，可以手动刷新）。
    var paused = false
    /// 识别计算软件（NEP / GPUMD）的进度。
    var detectApps = true
    /// 按这些进程名（pgrep -x，正则）查找计算软件；占用 GPU 或 CPU 很高的进程也会检查。
    var appProcessNames = HostConfig.defaultAppProcessNames
    static let defaultAppProcessNames = "gpumd|nep|gnep|vasp_std|vasp_gam|vasp_ncl|lmp|lmp_mpi|lmp_serial"
    /// Slurm 机器上同时显示不经过 Slurm、直接运行的进程（单机 Slurm 工作站很常见）。
    var slurmAlsoProcesses = false
    /// 读取 ~/.bashrc 等文件里的 PATH 设置，补全非交互 shell 缺少的路径（如装在家目录下的 Slurm）。
    var importShellPath = true
    /// 手动填写的连接信息（不依赖 ~/.ssh/config）。hostName 为空表示用 alias 连接。
    var hostName = ""
    var port: Int?
    var user = ""
    /// 私钥文件路径（可用 ~）。空表示用 ssh 默认的密钥和 ssh-agent。
    var identityFile = ""
    /// 经由另一台 ServerIsland 服务器（跳板机）连接；跳板机自己也可以再有跳板机。
    var jumpHostID: UUID?
    /// 把密码记进系统钥匙串，主连接断开后自动重新登录（默认关闭）。
    var rememberPassword = false
    /// 只看这些目录下的任务（每行一个，可用 ~）。作业按工作目录、进程按当前目录判断；空表示不过滤。
    var pathFilter = ""
    /// 各分区的单价（计费）；分区名为空的一行是"其他分区"。
    var prices: [PartitionPrice] = []

    /// 过滤目录列表（去掉空行）。
    var pathPrefixes: [String] {
        pathFilter.components(separatedBy: CharacterSet(charactersIn: "\n,;"))
            .map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    /// 是否使用手动填写的连接信息。
    var isManual: Bool { !hostName.trimmingCharacters(in: .whitespaces).isEmpty }

    init(alias: String, displayName: String? = nil, kind: HostKind = .direct) {
        self.alias = alias
        self.displayName = displayName ?? alias
        self.kind = kind
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let alias = try c.decode(String.self, forKey: .alias)
        self.init(alias: alias)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        displayName = try c.decodeIfPresent(String.self, forKey: .displayName) ?? alias
        kind = try c.decodeIfPresent(HostKind.self, forKey: .kind) ?? .direct
        auth = try c.decodeIfPresent(AuthMode.self, forKey: .auth) ?? .key
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? true
        interval = try c.decodeIfPresent(Int.self, forKey: .interval)
        showCPU = try c.decodeIfPresent(Bool.self, forKey: .showCPU) ?? true
        showMemory = try c.decodeIfPresent(Bool.self, forKey: .showMemory) ?? true
        showGPU = try c.decodeIfPresent(Bool.self, forKey: .showGPU) ?? true
        watchJobs = try c.decodeIfPresent(Bool.self, forKey: .watchJobs) ?? true
        readProgressFiles = try c.decodeIfPresent(Bool.self, forKey: .readProgressFiles) ?? true
        processFilter = try c.decodeIfPresent(String.self, forKey: .processFilter) ?? processFilter
        processExclude = try c.decodeIfPresent(String.self, forKey: .processExclude) ?? processExclude
        reuseConnection = try c.decodeIfPresent(Bool.self, forKey: .reuseConnection) ?? true
        commands = try c.decodeIfPresent(CommandSet.self, forKey: .commands) ?? CommandSet()
        muted = try c.decodeIfPresent(Bool.self, forKey: .muted) ?? false
        allowCancel = try c.decodeIfPresent(Bool.self, forKey: .allowCancel) ?? false
        watched = try c.decodeIfPresent([WatchedProcess].self, forKey: .watched) ?? []
        showDisk = try c.decodeIfPresent(Bool.self, forKey: .showDisk) ?? true
        keepAlive = try c.decodeIfPresent(KeepAlive.self, forKey: .keepAlive) ?? .h24
        paused = try c.decodeIfPresent(Bool.self, forKey: .paused) ?? false
        detectApps = try c.decodeIfPresent(Bool.self, forKey: .detectApps) ?? true
        appProcessNames = try c.decodeIfPresent(String.self, forKey: .appProcessNames) ?? Self.defaultAppProcessNames
        // 旧版默认值自动升级（用户改过的保持不动）
        if appProcessNames == "gpumd|nep|gnep" { appProcessNames = Self.defaultAppProcessNames }
        slurmAlsoProcesses = try c.decodeIfPresent(Bool.self, forKey: .slurmAlsoProcesses) ?? false
        importShellPath = try c.decodeIfPresent(Bool.self, forKey: .importShellPath) ?? true
        hostName = try c.decodeIfPresent(String.self, forKey: .hostName) ?? ""
        port = try c.decodeIfPresent(Int.self, forKey: .port)
        user = try c.decodeIfPresent(String.self, forKey: .user) ?? ""
        identityFile = try c.decodeIfPresent(String.self, forKey: .identityFile) ?? ""
        jumpHostID = try c.decodeIfPresent(UUID.self, forKey: .jumpHostID)
        rememberPassword = try c.decodeIfPresent(Bool.self, forKey: .rememberPassword) ?? false
        pathFilter = try c.decodeIfPresent(String.self, forKey: .pathFilter) ?? ""
        prices = try c.decodeIfPresent([PartitionPrice].self, forKey: .prices) ?? []
    }
}

/// 一个截止日期（副屏上倒计时）。
struct Deadline: Codable, Equatable, Identifiable {
    var id = UUID()
    var title: String
    var date: Date
}

struct WatchedProcess: Codable, Equatable, Identifiable {
    var pid: String
    var name: String
    /// 进程启动时刻（由 etimes 推算），用来识别 PID 被复用的情况。
    var startedAt: Date?

    var id: String { pid }
}

struct AppSettings: Codable, Equatable {
    var hosts: [HostConfig] = []
    var defaultInterval = 60
    /// false：所有机器都只手动刷新。
    var autoRefresh = true
    var collapsedStyle: CollapsedStyle = .always
    /// 展开面板里把空闲的机器合并成一行（全部空闲时照常显示卡片）。
    var foldIdleHosts = true
    /// 任务结束 / 开始时刘海弹出提示。
    var islandAlerts = true
    /// 同时发送系统通知。
    var systemNotifications = true
    /// 提示音（成功 / 失败不同的声音）。
    var soundEnabled = true
    var soundVolume = 0.6
    /// 数组作业第一次出现失败元素时立即提醒一次。
    var arrayFirstFailureAlert = true
    /// 磁盘占用超过该比例时提醒。
    var diskWarnThreshold = 0.9
    var language: AppLanguage = .system
    /// 自定义进度识别规则（所有服务器通用）。
    var progressRules: [ProgressRule] = []
    /// 诊断日志保留天数（更早的行自动删除）。
    var logRetentionDays = 7
    /// 启动时和之后每天检查一次 GitHub 上有没有新版本。
    var autoCheckUpdates = true
    /// 灵动岛显示在哪块屏幕：""=自动（有刘海的屏幕 → 内建屏幕 → 主屏幕），"primary"=主屏幕，其它=屏幕名称。
    var islandScreen = ""
    /// 关注的任务（跨服务器）。任务从完整的列表里消失后自动移除。
    var starredJobs: [StarredJob] = []
    /// 任务提醒的范围：全部任务 / 只提醒关注的任务。磁盘等服务器级提醒不受影响。
    var alertScope: AlertScope = .all
    /// 分别控制四类任务提醒。
    var alertStarted = true
    var alertFinished = true
    var alertFailed = true
    var alertUnknown = true
    /// 勿扰时段：这段时间里不响铃、不在刘海弹窗，系统通知照发但不出声（默认关）。时间是一天里的分钟数，起点晚于终点表示跨午夜。
    var quietHours = false
    var quietStart = 23 * 60
    var quietEnd = 8 * 60
    /// 外接副屏（ESP32 固件，暂不进仓库）。默认关：不知道有这块板子的用户看不到任何相关设置。
    /// 打开后设置窗口侧栏多一个"副屏"页面，插上 USB（或打开蓝牙）就自动连接并推送状态。
    var usbDisplay = false
    /// 也通过蓝牙连接（插着 USB 时仍然优先用 USB）。
    var displayBluetooth = false
    /// 电脑锁屏 / 睡眠时副屏一起熄屏。
    var displayLockSync = true
    /// 副屏的日历页显示 macOS 日历里的日程（需要授权）。
    var displayCalendar = false
    /// 截止日期倒计时（副屏的时钟和日历上显示）。
    var deadlines: [Deadline] = []
    /// 副屏上显示天气的城市（空 = 不显示；数据来自 Open-Meteo）。
    var weatherCity = ""
    /// 计费（估算费用），默认关闭。单价在各服务器的 `prices` 里。
    var billingEnabled = false
    /// 货币符号。
    var currency = "¥"
    /// 手机推送（企业微信群机器人）。key 只存钥匙串，不在这里。
    var phonePush = PhonePushSettings()

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        hosts = try c.decodeIfPresent([HostConfig].self, forKey: .hosts) ?? []
        defaultInterval = try c.decodeIfPresent(Int.self, forKey: .defaultInterval) ?? 60
        autoRefresh = try c.decodeIfPresent(Bool.self, forKey: .autoRefresh) ?? true
        collapsedStyle = try c.decodeIfPresent(CollapsedStyle.self, forKey: .collapsedStyle) ?? .always
        foldIdleHosts = try c.decodeIfPresent(Bool.self, forKey: .foldIdleHosts) ?? true
        islandAlerts = try c.decodeIfPresent(Bool.self, forKey: .islandAlerts) ?? true
        systemNotifications = try c.decodeIfPresent(Bool.self, forKey: .systemNotifications) ?? true
        soundEnabled = try c.decodeIfPresent(Bool.self, forKey: .soundEnabled) ?? true
        soundVolume = try c.decodeIfPresent(Double.self, forKey: .soundVolume) ?? 0.6
        diskWarnThreshold = try c.decodeIfPresent(Double.self, forKey: .diskWarnThreshold) ?? 0.9
        arrayFirstFailureAlert = try c.decodeIfPresent(Bool.self, forKey: .arrayFirstFailureAlert) ?? true
        language = try c.decodeIfPresent(AppLanguage.self, forKey: .language) ?? .system
        progressRules = try c.decodeIfPresent([ProgressRule].self, forKey: .progressRules) ?? []
        logRetentionDays = try c.decodeIfPresent(Int.self, forKey: .logRetentionDays) ?? 7
        autoCheckUpdates = try c.decodeIfPresent(Bool.self, forKey: .autoCheckUpdates) ?? true
        islandScreen = try c.decodeIfPresent(String.self, forKey: .islandScreen) ?? ""
        starredJobs = try c.decodeIfPresent([StarredJob].self, forKey: .starredJobs) ?? []
        alertScope = try c.decodeIfPresent(AlertScope.self, forKey: .alertScope) ?? .all
        alertStarted = try c.decodeIfPresent(Bool.self, forKey: .alertStarted) ?? true
        alertFinished = try c.decodeIfPresent(Bool.self, forKey: .alertFinished) ?? true
        alertFailed = try c.decodeIfPresent(Bool.self, forKey: .alertFailed) ?? true
        usbDisplay = try c.decodeIfPresent(Bool.self, forKey: .usbDisplay) ?? false
        displayBluetooth = try c.decodeIfPresent(Bool.self, forKey: .displayBluetooth) ?? false
        displayLockSync = try c.decodeIfPresent(Bool.self, forKey: .displayLockSync) ?? true
        displayCalendar = try c.decodeIfPresent(Bool.self, forKey: .displayCalendar) ?? false
        deadlines = try c.decodeIfPresent([Deadline].self, forKey: .deadlines) ?? []
        weatherCity = try c.decodeIfPresent(String.self, forKey: .weatherCity) ?? ""
        alertUnknown = try c.decodeIfPresent(Bool.self, forKey: .alertUnknown) ?? true
        quietHours = try c.decodeIfPresent(Bool.self, forKey: .quietHours) ?? false
        quietStart = try c.decodeIfPresent(Int.self, forKey: .quietStart) ?? 23 * 60
        quietEnd = try c.decodeIfPresent(Int.self, forKey: .quietEnd) ?? 8 * 60
        billingEnabled = try c.decodeIfPresent(Bool.self, forKey: .billingEnabled) ?? false
        currency = try c.decodeIfPresent(String.self, forKey: .currency) ?? "¥"
        phonePush = try c.decodeIfPresent(PhonePushSettings.self, forKey: .phonePush) ?? PhonePushSettings()
    }

    /// 现在是否在勿扰时段里。起点等于终点时不算勿扰。
    func isQuiet(at date: Date = Date(), calendar: Calendar = .current) -> Bool {
        guard quietHours, quietStart != quietEnd else { return false }
        let c = calendar.dateComponents([.hour, .minute], from: date)
        let m = (c.hour ?? 0) * 60 + (c.minute ?? 0)
        return quietStart < quietEnd ? (m >= quietStart && m < quietEnd) : (m >= quietStart || m < quietEnd)
    }

    /// 勿扰时段的文字，例如 23:00–08:00。
    var quietLabel: String { Self.clock(quietStart) + "–" + Self.clock(quietEnd) }
    static func clock(_ minutes: Int) -> String { String(format: "%02d:%02d", minutes / 60 % 24, minutes % 60) }

    func isStarred(_ hostID: UUID, _ jobID: String) -> Bool {
        starredJobs.contains { $0.hostID == hostID && $0.jobID == jobID }
    }

    /// 这条提醒要不要发：服务器级提醒（没有任务号，如磁盘）总是发；任务提醒看种类开关和关注范围。
    /// 静音在 emit 里判断，优先于这里。
    func allowsAlert(_ e: IslandEvent) -> Bool {
        guard let job = e.jobID, let host = e.hostID else { return true }
        switch e.kind {
        case .started: if !alertStarted { return false }
        case .finished: if !alertFinished { return false }
        case .failed: if !alertFailed { return false }
        case .info: if !alertUnknown { return false }
        case .warning: break
        }
        return alertScope == .all || isStarred(host, job)
    }
}

/// 手机推送的设置。默认只推送关注（⭐）的任务：平时人在电脑前看刘海就够了，要离开时给重要的任务点个星。
struct PhonePushSettings: Codable, Equatable {
    var enabled = false
    var scope: PushScope = .starred
    var started = false
    var finished = true
    var failed = true
    var unknown = true
    /// 磁盘快满等服务器级提醒。
    var serverWarnings = true
    /// Claude Code / Codex / OpenCode。
    var agents = false
    var agentAttention = true
    var agentDone = true
    /// 详细：资源、用时、目录、软件进度、费用……；简洁：只有标题和服务器。
    var detailed = true
    /// 勿扰时段（通用页设置的时间）里不推送。
    var followQuietHours = true
    /// 勿扰时段里失败的任务照常推送。
    var failuresDuringQuiet = true

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = PhonePushSettings()
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? d.enabled
        scope = try c.decodeIfPresent(PushScope.self, forKey: .scope) ?? d.scope
        started = try c.decodeIfPresent(Bool.self, forKey: .started) ?? d.started
        finished = try c.decodeIfPresent(Bool.self, forKey: .finished) ?? d.finished
        failed = try c.decodeIfPresent(Bool.self, forKey: .failed) ?? d.failed
        unknown = try c.decodeIfPresent(Bool.self, forKey: .unknown) ?? d.unknown
        serverWarnings = try c.decodeIfPresent(Bool.self, forKey: .serverWarnings) ?? d.serverWarnings
        agents = try c.decodeIfPresent(Bool.self, forKey: .agents) ?? d.agents
        agentAttention = try c.decodeIfPresent(Bool.self, forKey: .agentAttention) ?? d.agentAttention
        agentDone = try c.decodeIfPresent(Bool.self, forKey: .agentDone) ?? d.agentDone
        detailed = try c.decodeIfPresent(Bool.self, forKey: .detailed) ?? d.detailed
        followQuietHours = try c.decodeIfPresent(Bool.self, forKey: .followQuietHours) ?? d.followQuietHours
        failuresDuringQuiet = try c.decodeIfPresent(Bool.self, forKey: .failuresDuringQuiet) ?? d.failuresDuringQuiet
    }

    /// 需要读取 Agent 会话（副屏没连着时也要读）。
    var wantsAgents: Bool { enabled && agents && (agentAttention || agentDone) }
}

enum PushScope: String, Codable, CaseIterable, Identifiable {
    case starred
    case all
    case island

    var id: String { rawValue }
    var label: String {
        switch self {
        case .starred: return L("只推送关注（⭐）的任务", "Starred Jobs Only")
        case .all: return L("全部任务", "All Jobs")
        case .island: return L("和刘海提醒一样", "Same as Island Alerts")
        }
    }
    var shortLabel: String {
        switch self {
        case .starred: return L("关注的任务", "starred jobs")
        case .all: return L("全部任务", "all jobs")
        case .island: return L("同刘海提醒", "as island alerts")
        }
    }
}

/// 一个关注的任务。数组作业关注的是整个数组（主编号）。
struct StarredJob: Codable, Equatable {
    var hostID: UUID
    var jobID: String
    /// 关注时的名称（只用于日志和排查）。
    var name = ""
    var since = Date()

    init(hostID: UUID, jobID: String, name: String = "", since: Date = Date()) {
        self.hostID = hostID; self.jobID = jobID; self.name = name; self.since = since
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        hostID = try c.decode(UUID.self, forKey: .hostID)
        jobID = try c.decode(String.self, forKey: .jobID)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        since = try c.decodeIfPresent(Date.self, forKey: .since) ?? Date()
    }
}

enum AlertScope: String, Codable, CaseIterable, Identifiable {
    case all
    case starred

    var id: String { rawValue }
    var label: String {
        switch self {
        case .all: return L("全部任务", "All Jobs")
        case .starred: return L("只提醒关注的任务", "Starred Jobs Only")
        }
    }
}

/// 读写 `~/Library/Application Support/ServerIsland/config.json`。
@MainActor
final class ConfigStore: ObservableObject {
    @Published var settings: AppSettings {
        didSet {
            Lang.setting = settings.language
            ProgressRule.active = settings.progressRules
            if settings.hosts != oldValue.hosts { SSHHosts.update(settings.hosts, write: persist) }
            if settings != oldValue { scheduleSave() }
        }
    }

    static let directory: URL = {
        // 开发 / 测试用：指向另一份配置，不碰真实设置。
        if let dir = ProcessInfo.processInfo.environment["SERVERISLAND_CONFIG_DIR"], !dir.isEmpty {
            return URL(fileURLWithPath: dir, isDirectory: true)
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("ServerIsland", isDirectory: true)
    }()
    static let fileURL = directory.appendingPathComponent("config.json")

    private var saveWork: DispatchWorkItem?
    let persist: Bool
    /// 配置文件读不出来时的说明（启动后在刘海里提醒一次）；首次启动没有文件时为 nil。
    private(set) var loadProblem: String?
    /// 读取失败、原文件又没能备份时不再保存，免得默认设置把它覆盖掉。
    private var saveBlocked = false

    init(persist: Bool = true) {
        self.persist = persist
        if persist {
            let r = Self.load(from: Self.fileURL)
            settings = r.settings ?? AppSettings()
            loadProblem = r.problem
            saveBlocked = r.blockSave
        } else {
            settings = AppSettings()
        }
        Lang.setting = settings.language
        ProgressRule.active = settings.progressRules
        SSHHosts.update(settings.hosts, write: persist)
    }

    /// 读配置文件。没有文件（首次启动）返回 nil、没有问题；读不出来时先把原文件复制成 `config.broken-<时间>.json`，
    /// 再返回原因（之后改设置会覆盖 config.json，备份不动）。复制也失败时 blockSave 为 true。
    nonisolated static func load(from url: URL) -> (settings: AppSettings?, problem: String?, blockSave: Bool) {
        guard FileManager.default.fileExists(atPath: url.path) else { return (nil, nil, false) }
        do {
            return (try JSONDecoder().decode(AppSettings.self, from: Data(contentsOf: url)), nil, false)
        } catch {
            let stamp = DateFormatter()
            stamp.dateFormat = "yyyyMMdd-HHmmss"
            let backup = url.deletingLastPathComponent().appendingPathComponent("config.broken-\(stamp.string(from: Date())).json")
            NSLog("ServerIsland: failed to read config: \(error)")
            do {
                try FileManager.default.copyItem(at: url, to: backup)
                return (nil, L("设置文件读取失败，已改用默认设置；原文件备份为 \(backup.lastPathComponent)",
                               "Couldn't read the settings file; using defaults. The original was saved as \(backup.lastPathComponent)"), false)
            } catch {
                return (nil, L("设置文件读取失败，也没能备份；这次改动不会保存，原文件留在 \(url.path)",
                               "Couldn't read or back up the settings file; changes won't be saved. The original is at \(url.path)"), true)
            }
        }
    }

    func host(_ id: UUID) -> HostConfig? {
        settings.hosts.first { $0.id == id }
    }

    func interval(for host: HostConfig) -> Int {
        max(10, host.interval ?? settings.defaultInterval)
    }

    private func scheduleSave() {
        guard persist else { return }
        saveWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.saveNow() }
        saveWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }

    func saveNow() {
        guard persist, !saveBlocked else { return }
        do {
            try FileManager.default.createDirectory(at: Self.directory, withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
            try encoder.encode(settings).write(to: Self.fileURL, options: .atomic)
        } catch {
            NSLog("ServerIsland: failed to save config: \(error)")
        }
    }
}
