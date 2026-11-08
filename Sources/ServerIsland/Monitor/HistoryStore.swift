import Foundation

/// 本机记录的已结束任务（保留 7 天，每台服务器最多 500 条），存在 history.json，重启后还在。
/// 历史页把它和 sacct 查到的最近 24 小时合并显示；本机记录多了工作目录，可以直接在终端打开。
///
/// 另外在 active-jobs.json 里记住每台服务器上次看到的运行 / 排队任务：ServerIsland 退出期间结束的任务，
/// 下次启动、第一次连上服务器时补记到历史里（不弹提醒）。
@MainActor
final class HistoryStore {
    /// 上次看到还在运行 / 排队的任务。
    struct ActiveJob: Codable, Equatable {
        var id: String
        var name: String
        var slurm: Bool
        var dir: String?
        var elapsed: TimeInterval?
        var seenAt: Date
        /// 数组已知的元素总数，用于重启后检查 sacct 是否返回了完整结果；旧记录没有这个字段。
        var arrayTotal: Int?
        /// 计费用：分区、节点数和分到的资源（ServerIsland 退出期间结束的作业也能补记费用）。
        var partition: String?
        var nodes: Int?
        var alloc: JobAlloc?
    }

    static let retention: TimeInterval = 7 * 24 * 3600
    static let maxPerHost = 500

    private var entries: [UUID: [HistoryEntry]] = [:]
    private var active: [UUID: [ActiveJob]] = [:]
    private let url: URL
    private let activeURL: URL
    /// false：演示模式、自检、--diagnose 等临时引擎，只在内存里记，不碰真实的文件。
    private let persist: Bool

    init(directory: URL? = nil, persist: Bool = true) {
        self.persist = persist
        let directory = directory ?? ConfigStore.directory
        url = directory.appendingPathComponent("history.json")
        activeURL = directory.appendingPathComponent("active-jobs.json")
        guard persist else { return }
        if let data = try? Data(contentsOf: url),
           let decoded = try? JSONDecoder.iso.decode([String: [HistoryEntry]].self, from: data) {
            for (k, v) in decoded { if let id = UUID(uuidString: k) { entries[id] = v } }
        }
        if let data = try? Data(contentsOf: activeURL),
           let decoded = try? JSONDecoder.iso.decode([String: [ActiveJob]].self, from: data) {
            for (k, v) in decoded { if let id = UUID(uuidString: k) { active[id] = v } }
        }
        prune()
    }

    func record(_ host: UUID, _ entry: HistoryEntry) {
        var list = entries[host] ?? []
        // 同一个编号再记一次时（比如数组先记元素、后记整体）保留已知的目录
        var e = entry
        if e.dir == nil { e.dir = list.first { $0.id == entry.id }?.dir }
        list.removeAll { $0.id == entry.id }
        list.insert(e, at: 0)
        entries[host] = Array(list.prefix(Self.maxPerHost))
        prune()
        save()
    }

    func entries(for host: UUID) -> [HistoryEntry] {
        let cutoff = Date().addingTimeInterval(-Self.retention)
        return (entries[host] ?? []).filter { $0.endedAt > cutoff }
    }

    /// 设置里的"清除历史记录"：删掉全部已结束任务的记录（上次看到的运行中任务照常保留，用来补记退出期间结束的任务）。
    func clear() {
        entries = [:]
        save()
    }

    var count: Int { entries.values.reduce(0) { $0 + $1.count } }

    /// 上次记住的运行 / 排队任务。
    func activeJobs(for host: UUID) -> [ActiveJob] { active[host] ?? [] }

    /// 每次成功采样后调用；任务列表（编号、目录）没变时不写文件。
    func setActive(_ host: UUID, _ jobs: [RemoteJob]) {
        let now = Date()
        let list = jobs.filter { $0.state.isActive && $0.source != .progressFile }.map {
            ActiveJob(id: $0.id, name: $0.name, slurm: $0.source == .slurm, dir: $0.workdir ?? $0.app?.dir,
                      elapsed: $0.elapsed, seenAt: now, arrayTotal: $0.array?.total,
                      partition: $0.partition, nodes: $0.nodes, alloc: $0.alloc)
        }
        let old = active[host] ?? []
        let changed = old.map { "\($0.id)|\($0.dir ?? "")|\($0.arrayTotal ?? 0)|\($0.alloc != nil)" }
            != list.map { "\($0.id)|\($0.dir ?? "")|\($0.arrayTotal ?? 0)|\($0.alloc != nil)" }
        // 列表没变时也隔 10 分钟更新一次时间，"大约什么时候结束"不会差太多
        guard changed || (old.first.map { now.timeIntervalSince($0.seenAt) > 600 } ?? false) else { return }
        active[host] = list
        saveActive()
    }

    private func prune() {
        let cutoff = Date().addingTimeInterval(-Self.retention)
        for (k, v) in entries { entries[k] = v.filter { $0.endedAt > cutoff } }
    }

    private func save() {
        guard persist else { return }
        let dict = Dictionary(uniqueKeysWithValues: entries.map { ($0.key.uuidString, $0.value) })
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? JSONEncoder.iso.encode(dict).write(to: url, options: .atomic)
    }

    private func saveActive() {
        guard persist else { return }
        let dict = Dictionary(uniqueKeysWithValues: active.map { ($0.key.uuidString, $0.value) })
        try? FileManager.default.createDirectory(at: activeURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? JSONEncoder.iso.encode(dict).write(to: activeURL, options: .atomic)
    }
}

extension JSONDecoder {
    static var iso: JSONDecoder { let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601; return d }
}

extension JSONEncoder {
    static var iso: JSONEncoder { let e = JSONEncoder(); e.dateEncodingStrategy = .iso8601; return e }
}
