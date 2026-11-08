import Foundation

/// 计费（估算）：查询分到的资源、作业结束时记账、运行中作业的实时费用、读取分区给设置页用。
extension MonitorEngine {
    /// 这台机器要不要算费用：打开了计费、是调度系统、至少有一个分区填了单价。
    func billingActive(_ host: HostConfig) -> Bool {
        store.settings.billingEnabled && host.kind.isScheduler && host.watchJobs && host.prices.contains { $0.price > 0 }
    }

    /// 这一轮要查资源的作业：`known` 是已经查到的；`query` 是上一轮看到、还没查过的运行中作业（含数组元素，自定义 squeue 时用）。
    /// PBS 直接从 qstat 全文里取，两个都是空的。不计费时返回 nil。
    func allocQuery(_ host: HostConfig) -> RemoteScript.AllocRequest? {
        guard billingActive(host) else { return nil }
        guard host.kind == .slurm else { return RemoteScript.AllocRequest() }
        let known = allocCache[host.id] ?? [:]
        let jobs = snapshots[host.id]?.jobs ?? []
        let running = jobs.flatMap { j -> [RemoteJob] in
            if let a = j.array { return a.running }
            return j.source == .slurm && j.state == .running ? [j] : []
        }
        return RemoteScript.AllocRequest(query: running.map(\.id).filter { known[$0] == nil }, known: known.keys.sorted())
    }

    /// 作业结束时记一笔（没打开计费、没有单价或不知道资源量时不记）。
    /// 返回这次记下的费用（没开计费、不知道单价或资源时为 nil），推送时显示。
    @discardableResult
    func recordCost(host: HostConfig, id: String, arrayBase: String? = nil, name: String, partition: String?,
                    alloc: JobAlloc?, nodes: Int?, seconds: TimeInterval?, endedAt: Date = Date()) -> Double? {
        guard billingActive(host),
              let c = Billing.cost(host, partition: partition, alloc: alloc, nodes: nodes, seconds: seconds) else { return nil }
        costs.record(CostEntry(host: host.id, id: id, arrayBase: arrayBase, name: name, partition: partition,
                               unit: c.price.unit, amount: c.amount, seconds: seconds ?? 0, price: c.price.price, endedAt: endedAt))
        DebugLog.log("[\(host.displayName)] 记账 \(id)：\(c.price.unit.count(c.amount)) × \(Format.duration(seconds ?? 0)) = \(Billing.money(c.cost, currency: store.settings.currency))")
        return c.cost
    }

    struct LiveCost: Equatable {
        /// 到现在为止花了多少（数组包括已经结束的元素）。
        var spent: Double
        /// 现在每小时花多少。
        var perHour: Double
        /// "4 卡 × ¥2.50/卡时"
        var detail: String
    }

    /// 运行中（或刚结束）的作业的费用。不知道资源量时返回 nil。
    func liveCost(_ host: HostConfig, _ job: RemoteJob) -> LiveCost? {
        guard billingActive(host), job.source == .slurm else { return nil }
        let cur = store.settings.currency
        if let a = job.array {
            let done = costs.cost(host: host.id, id: a.baseID) ?? 0
            let parts = a.running.compactMap { Billing.cost(host, partition: $0.partition, alloc: $0.alloc, nodes: $0.nodes, seconds: $0.elapsed) }
            guard !parts.isEmpty || done > 0 else { return nil }
            let rate = parts.reduce(0) { $0 + $1.perHour }
            let detail = parts.first.map { L("\(a.running.count) 个元素运行中 · 每个 ", "\(a.running.count) running · each ")
                + $0.price.unit.count($0.amount) + " × " + Billing.money($0.price.price, currency: cur) + "/" + $0.price.unit.perLabel } ?? ""
            return LiveCost(spent: done + parts.reduce(0) { $0 + $1.cost }, perHour: rate, detail: detail)
        }
        if !job.state.isActive, let c = costs.cost(host: host.id, id: job.id) {
            return LiveCost(spent: c, perHour: 0, detail: "")
        }
        guard job.state != .pending,
              let c = Billing.cost(host, partition: job.partition, alloc: job.alloc, nodes: job.nodes, seconds: job.elapsed) else { return nil }
        return LiveCost(spent: c.cost, perHour: job.state == .running ? c.perHour : 0,
                        detail: c.price.unit.count(c.amount) + " × " + Billing.money(c.price.price, currency: cur) + "/" + c.price.unit.perLabel)
    }

    /// 费用汇总用的记录：账本里已经结束的，加上正在跑的任务"到上次刷新为止"花的（每次刷新都会变，不写进账本）。
    /// 数组按运行中的元素算；结束时间取那台机器上次刷新的时间，所以两次刷新之间数字不跳。
    func costEntriesWithRunning() -> [CostEntry] {
        var list = costs.entries
        for host in store.settings.hosts where billingActive(host) {
            guard let snap = snapshots[host.id] else { continue }
            let at = snap.lastUpdate ?? Date()
            for job in snap.jobs where job.source == .slurm {
                let parts: [(RemoteJob, String?)] = job.array.map { a in a.running.map { ($0, a.baseID) } }
                    ?? (job.state == .running ? [(job, nil)] : [])
                for (j, base) in parts {
                    guard let s = j.elapsed, s > 0,
                          let c = Billing.cost(host, partition: j.partition, alloc: j.alloc, nodes: j.nodes, seconds: s) else { continue }
                    list.append(CostEntry(host: host.id, id: j.id, arrayBase: base, name: j.name, partition: j.partition,
                                          unit: c.price.unit, amount: c.amount, seconds: s, price: c.price.price, endedAt: at))
                }
            }
        }
        return list
    }

    /// 所有服务器上正在运行的作业每小时一共花多少。
    var totalBurnRate: Double {
        store.settings.hosts.reduce(0) { sum, host in
            sum + (snapshots[host.id]?.jobs ?? []).reduce(0) { $0 + (liveCost(host, $1)?.perHour ?? 0) }
        }
    }

    /// 设置页"从服务器读取分区"：Slurm 用 sinfo（和分区页同一个查询），PBS 用 qstat -Q 列出队列。
    /// 返回分区名和说明（卡的型号 / 数量）；读不到时返回错误信息。
    func fetchBillingPartitions(host: HostConfig) async -> (list: [(name: String, note: String, hasGPU: Bool)], error: String?) {
        if host.kind == .slurm {
            let r = await fetchPartitions(host: host, force: true)
            guard let stats = r.stats else { return ([], r.error ?? L("sinfo 不可用", "sinfo unavailable")) }
            let list = stats.sorted { $0.name < $1.name }.map { p -> (name: String, note: String, hasGPU: Bool) in
                var note = L("\(p.nodes) 节点", "\(p.nodes) nodes")
                if p.gpuTotal > 0 {
                    let models = p.gpuModels.sorted().joined(separator: "/")
                    note += " · " + (models.isEmpty ? "" : models + " ") + L("共 \(p.gpuTotal) 张\(p.gpuLabel)", "\(p.gpuTotal) \(p.gpuLabel)s")
                } else {
                    note += " · " + L("共 \(p.cpuTotal) 核", "\(p.cpuTotal) cores")
                }
                return (p.name, note, p.gpuTotal > 0)
            }
            return (list, nil)
        }
        let r = await SSHRunner.run(host: host, remoteCommand: host.commands.shell,
                                    script: RemoteScript.pbsQueuesScript(host: host), timeout: 25)
        if let problem = SSHRunner.classify(r, host: host) {
            switch problem {
            case .needsLogin(let m), .error(let m): return ([], m)
            default: break
            }
        }
        guard let sec = RemoteScript.parse(r.stdout).sections["queues"], sec.exitCode == 0 else {
            return ([], L("qstat -Q 不可用", "qstat -Q unavailable"))
        }
        return (Parsers.pbsQueues(sec.lines).map { ($0, "", false) }, nil)
    }
}
