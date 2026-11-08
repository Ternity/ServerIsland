import Foundation

enum JobState: String, Equatable {
    case running, pending, completing, completed, failed, cancelled, timeout, other

    /// 仍在队列 / 运行中。
    var isActive: Bool { self == .running || self == .pending || self == .completing }

    var label: String {
        switch self {
        case .running: return L("运行中", "Running")
        case .pending: return L("排队中", "Pending")
        case .completing: return L("收尾中", "Completing")
        case .completed: return L("已完成", "Completed")
        case .failed: return L("失败", "Failed")
        case .cancelled: return L("已取消", "Cancelled")
        case .timeout: return L("超时", "Timed out")
        case .other: return L("未知", "Unknown")
        }
    }

    /// 把 Slurm 的状态字符串（如 RUNNING / CANCELLED by 123 / OUT_OF_MEMORY）映射过来。
    init(slurm raw: String) {
        let s = raw.uppercased()
        if s.hasPrefix("RUNNING") || s == "R" { self = .running }
        else if s.hasPrefix("PENDING") || s == "PD" || s.hasPrefix("CONFIGURING") || s.hasPrefix("REQUEUED") || s.hasPrefix("RESIZING") { self = .pending }
        else if s.hasPrefix("COMPLETING") || s == "CG" || s.hasPrefix("STAGE_OUT") { self = .completing }
        else if s.hasPrefix("COMPLETED") || s == "CD" { self = .completed }
        else if s.hasPrefix("CANCELLED") || s == "CA" { self = .cancelled }
        else if s.hasPrefix("TIMEOUT") || s == "TO" || s.hasPrefix("DEADLINE") { self = .timeout }
        else if s.hasPrefix("FAILED") || s.hasPrefix("NODE_FAIL") || s.hasPrefix("OUT_OF_MEMORY") || s.hasPrefix("BOOT_FAIL") || s == "F" || s == "OOM" || s == "NF" { self = .failed }
        else if s.hasPrefix("SUSPENDED") || s.hasPrefix("PREEMPTED") || s.hasPrefix("STOPPED") { self = .pending }
        else { self = .other }
    }
}

/// 一个"任务"：Slurm 作业、直连服务器上的进程，或进度文件里的条目。
struct RemoteJob: Identifiable, Equatable {
    enum Source: Equatable { case slurm, process, progressFile }

    var id: String
    var name: String
    var state: JobState
    var rawState: String = ""
    var source: Source
    /// 采样时刻的已运行时长（秒）。
    var elapsed: TimeInterval?
    /// 时间上限（秒），Slurm 的 TimeLimit。
    var limit: TimeInterval?
    /// 节点列表 / 排队原因 / 分区等补充信息。
    var detail: String = ""
    /// 0...1，来自进度文件。
    var progress: Double?
    var message: String?
    var cpuPercent: Double?
    var memoryBytes: Double?
    var gpuMemoryMB: Double?
    /// 用户手动监听的进程。
    var watched = false
    /// Slurm 数组作业：主编号（如 123）与元素序号或范围（如 5 / [7-99%10]）。
    var arrayJobID: String?
    var arrayTask: String?
    /// 合并后的整个数组（只有分组行有）。
    var array: ArrayInfo?
    /// 识别到的计算软件进度（NEP / GPUMD）。
    var app: AppProgress?
    /// 预计剩余时间（由真实速度算出时才有）。
    var eta: TimeInterval?
    /// 排队原因（原始值，不含括号）。
    var reason: String?
    /// 工作目录（作业：调度系统记录的；进程：当前目录）。写进历史记录，之后可以直接打开。
    var workdir: String?
    /// 分区（Slurm 的 %P，PBS 的队列）和节点数（%D）：计费用。
    var partition: String?
    var nodes: Int?
    /// 调度系统分配的资源（打开计费后才查询）。
    var alloc: JobAlloc?

    /// 用于进度条的比例：数组用"已结束 / 总数"，其次真实进度，再其次"已运行 / 时间上限"。
    var fraction: Double? {
        if let a = array, a.total > 0 { return Double(a.done + a.failed) / Double(a.total) }
        if let p = progress { return min(max(p, 0), 1) }
        if let e = elapsed, let l = limit, l > 0 { return min(max(e / l, 0), 1) }
        return nil
    }

    var hasRealProgress: Bool { progress != nil || array != nil }

    /// 计入"运行中 / 排队中"的数量（数组按元素算）。
    var runningUnits: Int {
        if let a = array { return a.running.count }
        return state == .running || state == .completing ? 1 : 0
    }
    var pendingUnits: Int {
        if let a = array { return a.pending }
        return state == .pending ? 1 : 0
    }
}

/// 一个 Slurm 数组作业的汇总。
struct ArrayInfo: Equatable {
    var baseID: String
    var running: [RemoteJob] = []
    var pending = 0
    /// 已结束的元素（成功 / 失败），来自 sacct 或运行期间的观察。
    var done = 0
    var failed = 0
    /// 元素总数（观察到的最大值）。
    var total = 0
    /// done / failed 是否来自 sacct（否则是 ServerIsland 运行期间观察到的，可能偏少）。
    var accurate = false
}

struct GPUStat: Identifiable, Equatable {
    var index: Int
    var name: String
    var utilization: Double   // 0...100
    var memoryUsedMB: Double
    var memoryTotalMB: Double
    var temperature: Double?

    var id: Int { index }
    var memoryFraction: Double { memoryTotalMB > 0 ? memoryUsedMB / memoryTotalMB : 0 }
}

struct DiskStat: Identifiable, Equatable {
    var mount: String
    var usedKB: Double
    var totalKB: Double

    var id: String { mount }
    var fraction: Double { totalKB > 0 ? usedKB / totalKB : 0 }
}

struct HostMetrics: Equatable {
    var cpuPercent: Double?
    var load1: Double?
    var cores: Int?
    var memoryUsed: Double?
    var memoryTotal: Double?

    var memoryFraction: Double? {
        guard let u = memoryUsed, let t = memoryTotal, t > 0 else { return nil }
        return u / t
    }
}

enum HostHealth: Equatable {
    case unknown
    case ok
    case needsLogin(String)
    case error(String)

    var isProblem: Bool {
        switch self {
        case .needsLogin, .error: return true
        default: return false
        }
    }
}

struct HostSnapshot: Equatable {
    var health: HostHealth = .unknown
    var metrics = HostMetrics()
    var gpus: [GPUStat] = []
    var disks: [DiskStat] = []
    var jobs: [RemoteJob] = []
    var lastUpdate: Date?
    var latency: TimeInterval?
    /// 某个命令执行失败等非致命问题，显示在卡片里，方便用户去设置里调整。
    var warnings: [String] = []
    var isRefreshing = false

    var runningCount: Int { jobs.reduce(0) { $0 + $1.runningUnits } }
    var pendingCount: Int { jobs.reduce(0) { $0 + $1.pendingUnits } }
}

/// 刘海弹出提示。
struct IslandEvent: Identifiable, Equatable {
    enum Kind: Equatable { case finished, failed, started, info, warning }

    let id = UUID()
    var kind: Kind
    var hostID: UUID?
    /// 任务提醒对应的任务号（数组是主编号）；服务器级提醒（磁盘等）为 nil。用于按关注范围和种类过滤。
    var jobID: String?
    var title: String
    var subtitle: String
    var hostName: String
    var date = Date()
    /// 手机推送用的详细信息（刘海和系统通知不用）。
    var push: PushInfo?

    init(kind: Kind, title: String, subtitle: String, hostName: String, hostID: UUID? = nil, jobID: String? = nil, push: PushInfo? = nil) {
        self.kind = kind; self.title = title; self.subtitle = subtitle; self.hostName = hostName; self.hostID = hostID; self.jobID = jobID
        self.push = push
    }
}

/// 任务提醒的细节：生成事件时已经知道的信息（不额外查询服务器）。查不到的项留空，推送时不显示那一行。
struct PushInfo: Equatable {
    /// 作业 / 进程 / 进度文件条目。
    var source: RemoteJob.Source = .slurm
    /// 显示用的编号（数组元素是 "#17"）。
    var id: String?
    var partition: String?
    var nodeList: String?
    var nodes: Int?
    var cpus: Int?
    var gpus: Int?
    /// 调度系统给的原始状态（OUT_OF_MEMORY、TIMEOUT、CANCELLED by 123…）。
    var state: String?
    var exitCode: String?
    var elapsed: TimeInterval?
    /// 进程只知道"至少运行了多久"。
    var elapsedAtLeast = false
    var limit: TimeInterval?
    var dir: String?
    /// "GNEP 训练：200/200 epoch · loss 1.23e-2"
    var app: String?
    var appNote: String?
    /// 进程最后一次采样："CPU 98% · 内存 12.3 GB · 显存 18.2 GB"
    var sample: String?
    var cost: String?
    /// 数组的结果："98 个成功 · 2 个失败 / 共 100 个"
    var array: String?
    var failedElements: [String] = []
    var note: String?
    var watched = false

    /// 从任务本身填好通用的几项。
    init(job: RemoteJob) {
        source = job.source
        id = job.array?.baseID ?? job.id
        partition = job.partition
        nodes = job.alloc?.nodes ?? job.nodes
        cpus = job.alloc?.cpus
        gpus = job.alloc?.gpus
        elapsed = job.elapsed
        limit = job.limit
        dir = job.workdir ?? job.app?.dir
        watched = job.watched
        if let app = job.app { setApp(app) }
        if job.source == .process {
            elapsedAtLeast = true
            var parts: [String] = []
            if let c = job.cpuPercent { parts.append("CPU \(Int(c.rounded()))%") }
            if let m = job.memoryBytes, m > 0 { parts.append(L("内存 ", "mem ") + Format.bytes(m)) }
            if let g = job.gpuMemoryMB, g > 0 { parts.append(L("显存 ", "GPU mem ") + Format.bytes(g * 1024 * 1024)) }
            sample = parts.isEmpty ? nil : parts.joined(separator: " · ")
        }
    }

    init() {}

    mutating func setApp(_ app: AppProgress) {
        let s = app.summary
        self.app = app.title + (s.isEmpty ? "" : L("：", ": ") + s)
        if dir == nil { dir = app.dir }
    }

    mutating func setAlloc(_ a: JobAlloc?) {
        guard let a else { return }
        if let n = a.nodes { nodes = n }
        if let c = a.cpus { cpus = c }
        if let g = a.gpus { gpus = g }
    }
}

/// 已结束任务的记录（历史页）。
struct HistoryEntry: Codable, Identifiable, Equatable {
    var id: String
    var name: String
    var state: String      // JobState.rawValue 或 Slurm 原始状态
    var elapsed: TimeInterval?
    var endedAt: Date
    var exitCode: String?
    var note: String?
    /// 工作目录（历史页上可以直接在终端打开）。
    var dir: String?

    /// ServerIsland 没在运行时结束的任务，不知道结果（state 写这个值）。
    static let endedUnknown = "ENDED"

    var jobState: JobState { JobState(rawValue: state) ?? JobState(slurm: state) }
    var stateLabel: String {
        if state == Self.endedUnknown { return L("已结束", "Ended") }
        let s = jobState
        return s == .other ? state : s.label
    }
}
