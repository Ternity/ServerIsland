import Foundation

/// 计费（估算）：用户给每个分区填单价，ServerIsland 用作业占用的资源 × 运行时长算出花了多少钱。
/// 只算 Slurm / PBS 的作业；资源量来自调度系统的分配记录（运行时 `scontrol show job` 的 TRES，结束后 sacct 的 AllocTRES）。

/// 计价方式。
enum PriceUnit: String, Codable, CaseIterable, Identifiable {
    case gpu, cpu, node
    var id: String { rawValue }

    /// 设置里的选项名。
    var label: String {
        switch self {
        case .gpu: return L("按卡时", "Per GPU-hour")
        case .cpu: return L("按核时", "Per core-hour")
        case .node: return L("按节点时", "Per node-hour")
        }
    }

    /// 单价后面的单位："/卡时"。
    var perLabel: String {
        switch self {
        case .gpu: return L("卡时", "GPU-h")
        case .cpu: return L("核时", "core-h")
        case .node: return L("节点时", "node-h")
        }
    }

    /// 资源量的单位："4 卡"。
    func count(_ n: Double) -> String {
        let v = n == n.rounded() ? String(Int(n)) : String(format: "%.1f", n)
        switch self {
        case .gpu: return L("\(v) 卡", "\(v) GPU" + (n == 1 ? "" : "s"))
        case .cpu: return L("\(v) 核", "\(v) core" + (n == 1 ? "" : "s"))
        case .node: return L("\(v) 节点", "\(v) node" + (n == 1 ? "" : "s"))
        }
    }
}

/// 一个分区的单价。`partition` 为空表示"其他分区"（没有单独设置的分区都按它算）。
struct PartitionPrice: Codable, Equatable, Identifiable {
    var id = UUID()
    var partition = ""
    var unit: PriceUnit = .gpu
    /// 每单位每小时的价格；0 表示不计费。
    var price: Double = 0
    /// 读取分区时记下的说明（卡的型号和数量），只用于设置页显示。
    var note = ""

    init(partition: String = "", unit: PriceUnit = .gpu, price: Double = 0, note: String = "") {
        self.partition = partition
        self.unit = unit
        self.price = price
        self.note = note
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        partition = try c.decodeIfPresent(String.self, forKey: .partition) ?? ""
        unit = try c.decodeIfPresent(PriceUnit.self, forKey: .unit) ?? .gpu
        price = try c.decodeIfPresent(Double.self, forKey: .price) ?? 0
        note = try c.decodeIfPresent(String.self, forKey: .note) ?? ""
    }
}

/// 作业分到的资源（来自 TRES 字符串，如 `cpu=32,mem=128G,node=1,billing=32,gres/gpu=4`）。
struct JobAlloc: Codable, Equatable {
    var cpus: Int?
    var gpus: Int?
    var nodes: Int?

    /// 加速卡和 Partitions 一样认 gpu / dcu / npu / mlu。同一种卡既有总数（gres/gpu=4）又有分型号（gres/gpu:a100=4）时只用总数。
    static func parse(_ tres: String) -> JobAlloc? {
        var a = JobAlloc()
        var plain: [String: Int] = [:], typed: [String: Int] = [:]
        for part in tres.split(separator: ",") {
            let kv = part.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard kv.count == 2, let n = Int(kv[1]) else { continue }
            let key = kv[0].lowercased()
            if key == "cpu" { a.cpus = n }
            else if key == "node" { a.nodes = n }
            else if key.hasPrefix("gres/") {
                let name = key.dropFirst(5)
                let kind = String(name.split(separator: ":", maxSplits: 1)[0])
                guard ["gpu", "dcu", "npu", "mlu"].contains(kind) else { continue }
                if name.contains(":") { typed[kind, default: 0] += n } else { plain[kind, default: 0] += n }
            }
        }
        let kinds = Set(plain.keys).union(typed.keys)
        if !kinds.isEmpty { a.gpus = kinds.reduce(0) { $0 + (plain[$1] ?? typed[$1] ?? 0) } }
        guard a.cpus != nil || a.gpus != nil || a.nodes != nil else { return nil }
        // 写明了 CPU 却没有加速卡：就是 0 张卡（按卡时计费的分区里算 0 元）
        if a.gpus == nil, a.cpus != nil { a.gpus = 0 }
        return a
    }

    /// 分到的资源里没有加速卡时，按申请的卡数算：有的 Slurm 没把 gres/gpu 写进 AccountingStorageTRES，
    /// 作业明明分到了卡，AllocTRES 里却没有（GPU 是整张分配的，申请几张就占几张）。
    /// `perNode` / `perJob`：scontrol 的 TresPerNode（--gres=gpu:1）/ TresPerJob（--gpus=4），如 `gres/gpu:1`、`gres/gpu:a100:2`、老版本的 `gres:gpu:1`。
    static func parse(_ tres: String, perNode: String, perJob: String, nodes: Int?) -> JobAlloc? {
        var a = parse(tres)
        guard (a?.gpus ?? 0) == 0 else { return a }
        let n = a?.nodes ?? nodes ?? 1
        let req = requestedGPUs(perJob) ?? requestedGPUs(perNode).map { $0 * max(n, 1) }
        guard let req, req > 0 else { return a }
        if a == nil { a = JobAlloc(cpus: nil, gpus: nil, nodes: nodes) }
        a?.gpus = req
        return a
    }

    /// `gres/gpu:2,gres/gpu:a100:1` -> 3；没有加速卡时返回 nil。
    static func requestedGPUs(_ s: String) -> Int? {
        var total = 0, found = false
        for item in s.split(separator: ",") {
            var t = item.trimmingCharacters(in: .whitespaces).lowercased()
            if t.hasPrefix("gres/") { t.removeFirst(5) } else if t.hasPrefix("gres:") { t.removeFirst(5) }
            let parts = t.split(separator: ":").map(String.init)
            guard let kind = parts.first, ["gpu", "dcu", "npu", "mlu"].contains(kind) else { continue }
            found = true
            total += parts.count >= 2 ? (Int(parts.last!) ?? 1) : 1
        }
        return found ? total : nil
    }

    /// 结束时用哪份资源：sacct 的最终值为准，但它里面没有卡、运行时看到过卡时（同一个原因：没统计 gres/gpu），卡数用运行时的。
    static func best(final: JobAlloc?, seen: JobAlloc?) -> JobAlloc? {
        guard var f = final else { return seen }
        if (f.gpus ?? 0) == 0, let g = seen?.gpus, g > 0 { f.gpus = g }
        return f
    }

    /// 按计价方式取资源量；节点数没写时用 squeue 的节点数。
    func amount(_ unit: PriceUnit, nodes fallback: Int? = nil) -> Double? {
        switch unit {
        case .gpu: return gpus.map(Double.init)
        case .cpu: return cpus.map(Double.init)
        case .node: return (nodes ?? fallback).map(Double.init)
        }
    }
}

enum Billing {
    /// 这个分区的单价：先找同名的，再用"其他分区"。单价为 0 的不算。
    static func price(_ host: HostConfig, partition: String?) -> PartitionPrice? {
        let name = (partition ?? "").trimmingCharacters(in: .whitespaces)
        if !name.isEmpty, let p = host.prices.first(where: { $0.partition == name }) {
            return p.price > 0 ? p : nil
        }
        return host.prices.first { $0.partition.isEmpty && $0.price > 0 }
    }

    /// 估算一个作业的费用。资源量或时长不知道时返回 nil。
    static func cost(_ host: HostConfig, partition: String?, alloc: JobAlloc?, nodes: Int?, seconds: TimeInterval?)
        -> (cost: Double, perHour: Double, price: PartitionPrice, amount: Double)? {
        guard let p = price(host, partition: partition), let s = seconds, s >= 0,
              let n = (alloc ?? JobAlloc(nodes: nil)).amount(p.unit, nodes: nodes) else { return nil }
        let perHour = n * p.price
        return (perHour * s / 3600, perHour, p, n)
    }

    /// 估算的说明：费用页底部和设置页都显示。
    static var disclaimer: String {
        L("以上是按你填的单价和调度系统记录的资源估算的，仅供参考，实际费用以服务器供应商的账单为准。",
          "These are estimates from your prices and the resources the scheduler recorded. Your provider's bill is what counts.")
    }

    /// 货币对应的图标（SF Symbols）。
    static func symbol(_ currency: String) -> String {
        switch currency {
        case "$": return "dollarsign.circle"
        case "€": return "eurosign.circle"
        case "£": return "sterlingsign.circle"
        default: return "yensign.circle"
        }
    }

    /// 金额：¥12.40；很小的数多留一位，大数不要小数。
    static func money(_ v: Double, currency: String) -> String {
        let a = abs(v)
        let s: String
        if a >= 10000 { s = String(format: "%.0f", v) }
        else if a > 0 && a < 0.1 { s = String(format: "%.3f", v) }
        else { s = String(format: "%.2f", v) }
        return currency + s
    }
}

/// 一个已结束作业（或数组的一个元素）的费用记录。记下当时的单价：以后改价格不影响旧账。
struct CostEntry: Codable, Equatable {
    var host: UUID
    var id: String
    /// 数组元素所属的数组主编号。
    var arrayBase: String?
    var name: String
    var partition: String?
    var unit: PriceUnit
    var amount: Double
    var seconds: TimeInterval
    var price: Double
    var endedAt: Date

    var cost: Double { amount * price * seconds / 3600 }
}

/// 费用账本（cost-ledger.json），保留 400 天，用来按天 / 按月汇总。
@MainActor
final class CostLedger {
    static let retention: TimeInterval = 400 * 86400
    static let maxEntries = 20000

    private(set) var entries: [CostEntry] = []
    private let url: URL
    private let persist: Bool

    init(directory: URL? = nil, persist: Bool = true) {
        self.persist = persist
        url = (directory ?? ConfigStore.directory).appendingPathComponent("cost-ledger.json")
        guard persist, let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder.iso.decode([CostEntry].self, from: data) else { return }
        entries = decoded
        prune()
    }

    /// 同一台机器的同一个编号只记一次（再记时覆盖）。
    func record(_ e: CostEntry) {
        entries.removeAll { $0.host == e.host && $0.id == e.id }
        entries.append(e)
        prune()
        save()
    }

    /// 演示 / 预览用：直接放入一批记录，不写文件。
    func replaceAll(_ list: [CostEntry]) { entries = list }

    func clear() {
        entries = []
        save()
    }

    /// 一个作业的费用；数组按主编号把各元素加起来。
    func cost(host: UUID, id: String) -> Double? {
        let mine = entries.filter { $0.host == host && ($0.id == id || $0.arrayBase == id) }
        return mine.isEmpty ? nil : mine.reduce(0) { $0 + $1.cost }
    }

    func entries(from start: Date, to end: Date = .distantFuture) -> [CostEntry] {
        entries.filter { $0.endedAt >= start && $0.endedAt < end }
    }

    private func prune() {
        let cutoff = Date().addingTimeInterval(-Self.retention)
        entries.removeAll { $0.endedAt < cutoff }
        if entries.count > Self.maxEntries { entries = Array(entries.sorted { $0.endedAt > $1.endedAt }.prefix(Self.maxEntries)) }
    }

    private func save() {
        guard persist else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? JSONEncoder.iso.encode(entries).write(to: url, options: .atomic)
    }
}

/// 费用页的汇总（今天 / 本月 / 上月、按服务器和分区、按天、最贵的作业）。纯计算，便于自检。
struct CostSummary {
    struct Group: Identifiable { var host: UUID; var partition: String; var cost: Double; var hours: Double; var unit: PriceUnit
        var id: String { host.uuidString + "|" + partition } }
    var today = 0.0, month = 0.0, lastMonth = 0.0
    var groups: [Group] = []
    /// 本月每天的费用（从 1 号到今天）。
    var days: [Double] = []
    /// 本月最贵的作业；数组的各元素合成一行。
    struct TopJob: Identifiable { var host: UUID; var id: String; var name: String; var partition: String; var cost: Double; var elements: Int
        var key: String { host.uuidString + "|" + id } }
    var top: [TopJob] = []

    /// 每条记录按它实际运行的时间段（结束时间往前推运行时长）摊到各天 / 各月：跨过零点的任务两天各算一部分。
    /// 正在跑的任务由引擎当作"到上次刷新为止"的记录传进来（`MonitorEngine.costEntriesWithRunning`），每次刷新都会变。
    init(_ entries: [CostEntry], now: Date = Date(), calendar: Calendar = .current) {
        let dayStart = calendar.startOfDay(for: now)
        let monthStart = calendar.date(from: calendar.dateComponents([.year, .month], from: now)) ?? dayStart
        let lastStart = calendar.date(byAdding: .month, value: -1, to: monthStart) ?? monthStart
        let limit = now.addingTimeInterval(60)
        /// 这条记录落在 [a, b) 里的那部分费用占多少（0...1）。
        func share(_ e: CostEntry, _ a: Date, _ b: Date) -> Double {
            let end = e.endedAt, start = e.endedAt.addingTimeInterval(-max(e.seconds, 0))
            guard e.seconds > 0 else { return end >= a && end < b ? 1 : 0 }
            let overlap = min(end, b).timeIntervalSince(max(start, a))
            return max(0, min(1, overlap / e.seconds))
        }
        let thisMonth = entries.filter { share($0, monthStart, limit) > 0 }
        today = entries.reduce(0) { $0 + $1.cost * share($1, dayStart, limit) }
        month = thisMonth.reduce(0) { $0 + $1.cost * share($1, monthStart, limit) }
        lastMonth = entries.reduce(0) { $0 + $1.cost * share($1, lastStart, monthStart) }
        var map: [String: Group] = [:]
        for e in thisMonth {
            let f = share(e, monthStart, limit)
            let k = e.host.uuidString + "|" + (e.partition ?? "")
            var g = map[k] ?? Group(host: e.host, partition: e.partition ?? "", cost: 0, hours: 0, unit: e.unit)
            g.cost += e.cost * f
            g.hours += e.amount * e.seconds / 3600 * f
            map[k] = g
        }
        groups = map.values.sorted { $0.cost > $1.cost }
        let n = (calendar.dateComponents([.day], from: monthStart, to: dayStart).day ?? 0) + 1
        days = Array(repeating: 0, count: max(n, 1))
        for d in days.indices {
            guard let a = calendar.date(byAdding: .day, value: d, to: monthStart),
                  let b = calendar.date(byAdding: .day, value: 1, to: a) else { continue }
            days[d] = thisMonth.reduce(0) { $0 + $1.cost * share($1, a, min(b, limit)) }
        }
        var jobs: [String: TopJob] = [:]
        for e in thisMonth {
            let id = e.arrayBase ?? e.id
            let k = e.host.uuidString + "|" + id
            var t = jobs[k] ?? TopJob(host: e.host, id: id, name: e.name, partition: e.partition ?? "", cost: 0, elements: 0)
            t.cost += e.cost * share(e, monthStart, limit)
            if e.arrayBase != nil { t.elements += 1 }
            jobs[k] = t
        }
        top = Array(jobs.values.sorted { ($0.cost, $0.key) > ($1.cost, $1.key) }.prefix(5))
    }
}
