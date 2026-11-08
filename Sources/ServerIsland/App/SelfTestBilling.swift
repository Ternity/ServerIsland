import Foundation

/// 自检：计费（TRES 解析、PBS 的资源换算、单价查找、费用计算、账本、汇总、配置升级、远端脚本、引擎记账）。
enum SelfTestBilling {
    /// PBS Pro（ncpus / ngpus）、Torque（nodes=2:ppn=16:gpus=2）、只写了 nodect 的作业。
    static let pbsSample = """
    Job Id: 3001.pbs01
        Job_Name = train
        job_state = R
        queue = gpu
        resources_used.walltime = 02:00:00
        Resource_List.ncpus = 16
        Resource_List.ngpus = 4
        Resource_List.nodect = 1
    Job Id: 3002.torque.example.org
        Job_Name = md
        job_state = R
        queue = batch
        resources_used.walltime = 00:30:00
        Resource_List.nodes = 2:ppn=16:gpus=2
    Job Id: 3003.pbs01
        Job_Name = wait
        job_state = Q
        queue = gpu
        Resource_List.ncpus = 8
    Job Id: 3004.pbs01
        Job_Name = done
        job_state = F
        Exit_status = 0
        queue = gpu
        resources_used.walltime = 01:00:00
        Resource_List.ncpus = 8
        Resource_List.ngpus = 1
        Resource_List.nodect = 1

    """

    static func run(_ check: (Bool, String) -> Void) {
        // —— TRES ——
        let a = JobAlloc.parse("cpu=32,mem=128G,node=1,billing=32,gres/gpu=4,gres/gpu:a100=4")
        check(a == JobAlloc(cpus: 32, gpus: 4, nodes: 1), "计费：TRES 里同时有 gres/gpu 和分型号时只算总数")
        check(JobAlloc.parse("cpu=8,node=1,gres/gpu:a100=2,gres/gpu:v100=1")?.gpus == 3
              && JobAlloc.parse("cpu=4,gres/dcu=4,node=1")?.gpus == 4
              && JobAlloc.parse("cpu=64,mem=200G,node=2")?.gpus == 0
              && JobAlloc.parse("") == nil && JobAlloc.parse("(null)") == nil,
              "计费：分型号相加、DCU 算加速卡、只有 CPU 时是 0 张卡、空值不算")
        check(Parsers.allocs(["123|cpu=4,node=1,gres/gpu=1", "124|", "125_3|cpu=2,node=1"]).keys.sorted() == ["123", "125_3"],
              "计费：alloc 段（作业号|TRES），查不到的作业跳过")
        // 没把 gres/gpu 写进 AccountingStorageTRES 的 Slurm：AllocTRES 里没有卡，按申请的卡数算
        let req = Parsers.allocs(["1712|cpu=4,mem=30000M,node=1,billing=4|gres/gpu:1||1",
                                  "1713|cpu=8,node=2|gres/gpu:a100:2||2",
                                  "1714|cpu=8,node=1||gres/gpu:4|1",
                                  "1715|cpu=8,node=1,gres/gpu=2|gres/gpu:1||1",
                                  "1716|cpu=8,node=1|gres:gpu:1,gres/shard:2||1",
                                  "1717|cpu=8,node=1|||1"])
        check(req["1712"]?.gpus == 1 && req["1713"]?.gpus == 4 && req["1714"]?.gpus == 4 && req["1715"]?.gpus == 2
              && req["1716"]?.gpus == 1 && req["1717"]?.gpus == 0,
              "计费：AllocTRES 没有卡时按申请的卡数（每节点 × 节点数 / 整个作业），有卡时以分到的为准")
        check(JobAlloc.best(final: JobAlloc(cpus: 4, gpus: 0, nodes: 1), seen: JobAlloc(cpus: 4, gpus: 1, nodes: 1)) == JobAlloc(cpus: 4, gpus: 1, nodes: 1)
              && JobAlloc.best(final: JobAlloc(cpus: 4, gpus: 2, nodes: 1), seen: JobAlloc(cpus: 4, gpus: 1, nodes: 1))?.gpus == 2
              && JobAlloc.best(final: nil, seen: JobAlloc(cpus: 1, gpus: 1, nodes: 1))?.gpus == 1,
              "计费：结束时 sacct 里没有卡、运行时看到过卡，卡数用运行时的")
        let sacctLines = ["9001|relax|COMPLETED|01:00:00|0:0|cpu=64,mem=100G,node=2", "9002|old|FAILED|00:10:00|1:0",
                          "9003|x|COMPLETED|00:01:00|0:0|"]
        check(Parsers.sacctAllocs(sacctLines) == ["9001": JobAlloc(cpus: 64, gpus: 0, nodes: 2)]
              && Parsers.sacct(sacctLines)["9001"]?.elapsed == 3600 && Parsers.sacct(sacctLines)["9001"]?.exit == "0:0",
              "计费：sacct 多了 AllocTRES 一列，原来的解析不受影响")
        let q = Parsers.squeue(["101|relax|RUNNING|1:00:00|2:00:00|2|cn[01-02]|gpu-a100|101|N/A"])
        check(q.first?.partition == "gpu-a100" && q.first?.nodes == 2, "计费：squeue 记下分区和节点数")

        // —— PBS：si_pbs t / a 输出资源 ——
        let t = SelfTestPBS.bash(RemoteScript.pbsFunction + "si_pbs t <<'SI_EOF'\n" + pbsSample + "SI_EOF\n")
        let tl = Parsers.allocs(t.components(separatedBy: "\n"))
        check(tl["3001"] == JobAlloc(cpus: 16, gpus: 4, nodes: 1) && tl["3002"] == JobAlloc(cpus: 32, gpus: 4, nodes: 2)
              && tl["3003"] == nil && tl["3004"] == nil,
              "计费：PBS Pro 的 ncpus / ngpus、Torque 的 nodes=2:ppn=16:gpus=2，只输出运行中的作业")
        let fin = SelfTestPBS.bash(RemoteScript.pbsFunction + "si_pbs a <<'SI_EOF'\n" + pbsSample + "SI_EOF\n")
        let finLines = fin.components(separatedBy: "\n")
        check(Parsers.sacctAllocs(finLines)["3004"] == JobAlloc(cpus: 8, gpus: 1, nodes: 1) && Parsers.sacct(finLines)["3004"]?.state == .completed,
              "计费：PBS 结束状态后面带上资源")
        let pq = Parsers.squeue(SelfTestPBS.bash(RemoteScript.pbsFunction + "si_pbs q <<'SI_EOF'\n" + pbsSample + "SI_EOF\n")
            .components(separatedBy: "\n").filter { !$0.isEmpty })
        check(pq.first { $0.id == "3001" }?.partition == "gpu", "计费：PBS 的队列当作分区")

        // —— 单价和费用 ——
        var h = HostConfig(alias: "x", kind: .slurm)
        h.prices = [PartitionPrice(partition: "", unit: .cpu, price: 0.05),
                    PartitionPrice(partition: "gpu", unit: .gpu, price: 2.5),
                    PartitionPrice(partition: "free", unit: .gpu, price: 0)]
        check(Billing.price(h, partition: "gpu")?.price == 2.5 && Billing.price(h, partition: "other")?.price == 0.05
              && Billing.price(h, partition: "free") == nil && Billing.price(h, partition: nil)?.unit == .cpu,
              "计费：先找同名分区，再用“其他分区”；单价为 0 的分区不计费")
        let c = Billing.cost(h, partition: "gpu", alloc: JobAlloc(cpus: 16, gpus: 4, nodes: 1), nodes: 1, seconds: 5400)
        check(c.map { abs($0.cost - 15) < 1e-9 && abs($0.perHour - 10) < 1e-9 && $0.amount == 4 } == true, "计费：4 卡 × 1.5 小时 × 2.5 = 15")
        check(Billing.cost(h, partition: "gpu", alloc: nil, nodes: 1, seconds: 60) == nil
              && Billing.cost(h, partition: "gpu", alloc: JobAlloc(cpus: 1, gpus: 1, nodes: 1), nodes: 1, seconds: nil) == nil,
              "计费：不知道资源量或时长时不算")
        var nh = h; nh.prices = [PartitionPrice(partition: "", unit: .node, price: 10)]
        check(Billing.cost(nh, partition: "x", alloc: nil, nodes: 3, seconds: 3600)?.cost == 30, "计费：按节点时可以用 squeue 的节点数")
        check(Billing.money(12.4, currency: "¥") == "¥12.40" && Billing.money(0.04, currency: "$") == "$0.040"
              && Billing.money(12345.6, currency: "¥") == "¥12346", "计费：金额格式")

        // —— 汇总 ——
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "Asia/Shanghai")!
        let now = cal.date(from: DateComponents(year: 2026, month: 11, day: 5, hour: 15))!
        let hid = UUID()
        func e(_ id: String, _ base: String?, _ day: Int, month: Int = 11, hours: Double, price: Double = 1, amount: Double = 2) -> CostEntry {
            CostEntry(host: hid, id: id, arrayBase: base, name: base ?? id, partition: "gpu", unit: .gpu, amount: amount,
                      seconds: hours * 3600, price: price, endedAt: cal.date(from: DateComponents(year: 2026, month: month, day: day, hour: 10))!)
        }
        let entries = [e("1", nil, 5, hours: 1), e("2", nil, 3, hours: 2), e("7_1", "7", 4, hours: 1), e("7_2", "7", 4, hours: 1),
                       e("0", nil, 20, month: 10, hours: 10), e("old", nil, 20, month: 9, hours: 100)]
        let s = CostSummary(entries, now: now, calendar: cal)
        check(s.today == 2 && s.month == 10 && s.lastMonth == 20 && s.days.count == 5 && s.days[3] == 4 && s.days[2] == 4,
              "计费汇总：今天 / 本月 / 上月和每天")
        check(s.top.first?.id == "7" && s.top.first?.elements == 2 && s.top.first?.cost == 4 && s.groups.count == 1 && s.groups[0].hours == 10,
              "计费汇总：数组的元素合成一行，按分区合计卡时")
        // 跨零点的任务：两天各算一部分（11 月 5 日 02:00 结束、跑了 4 小时 → 4 日 2 小时、5 日 2 小时）
        let cross = CostEntry(host: hid, id: "x", name: "x", partition: "gpu", unit: .gpu, amount: 1, seconds: 4 * 3600, price: 1,
                              endedAt: cal.date(from: DateComponents(year: 2026, month: 11, day: 5, hour: 2))!)
        let cs = CostSummary([cross], now: now, calendar: cal)
        check(cs.today == 2 && cs.month == 4 && cs.days[3] == 2 && cs.days[4] == 2, "计费汇总：跨零点的任务按实际运行时间摊到两天")

        // —— 配置 ——
        let oldJSON = #"{"alias":"x","kind":"slurm","commands":{"sacct":"sacct -n -X -P -o JobID,JobName,State,Elapsed,ExitCode -j {jobs}"}}"#
        let old = try? JSONDecoder().decode(HostConfig.self, from: Data(oldJSON.utf8))
        check(old?.commands.sacct == CommandSet.defaults.sacct && old?.prices.isEmpty == true && CommandSet.defaults.sacct.contains("AllocTRES"),
              "计费：旧版默认的 sacct 命令自动加上 AllocTRES，旧配置没有单价")
        if let data = try? JSONEncoder().encode(h), let back = try? JSONDecoder().decode(HostConfig.self, from: data) {
            check(back.prices == h.prices, "计费：单价保存后读回一致")
        } else { check(false, "计费：单价保存后读回一致") }
        let st = try? JSONDecoder().decode(AppSettings.self, from: Data("{}".utf8))
        check(st?.billingEnabled == false && st?.currency == "¥", "计费：默认关闭，货币默认 ¥")

        // —— 远端脚本：只有打开计费时才多一段 ——
        let plain = RemoteScript.build(for: h)
        let withAlloc = RemoteScript.build(for: h, alloc: .init(query: ["9"], known: ["123", "456_7", "bad;rm"]))
        check(!plain.contains("SEC alloc") && withAlloc.contains("SI_K=' 123 456_7 '") && !withAlloc.contains("bad;rm") && !withAlloc.contains("'9'")
              && SelfTestPBS.syntaxOK(withAlloc), "计费：默认 squeue 时同一轮从列表里找新的运行中作业，跳过已知的，作业号经过校验，语法正确")
        var customH = h; customH.commands.squeue = #"squeue -u "$(id -un)" -h -o '%i|%j|%T|%M|%l|%D|%R|%P'"# + " -p gpu"
        let customAlloc = RemoteScript.build(for: customH, alloc: .init(query: ["123", "456_7", "bad;rm"], known: []))
        check(customAlloc.contains("for j in '123' '456_7'; do") && !customAlloc.contains("bad;rm") && SelfTestPBS.syntaxOK(customAlloc)
              && !RemoteScript.build(for: customH, alloc: .init()).contains("SEC alloc"),
              "计费：自定义 squeue 时查上一轮看到的作业；没有要查的作业时不加这一段")
        // 在本机用假的 squeue 结果和假的 scontrol 跑一遍：只查新的运行中作业
        let fake = """
        scontrol() { echo "JobId=$4 AllocTRES=cpu=4,node=1 TresPerNode=gres/gpu:1 NumNodes=1"; }
        SI_JW='101|RUNNING|/a
        102|PENDING|/b
        103_2|RUNNING|/c'
        """ + "\n" + (withAlloc.components(separatedBy: "\n").drop { !$0.contains("SEC alloc") }.prefix(3).joined(separator: "\n"))
        let ran = SelfTestPBS.bash(fake.replacingOccurrences(of: "' 123 456_7 '", with: "' 101 '"))
        check(ran.contains("103_2|cpu=4,node=1|gres/gpu:1||1") && !ran.contains("101|") && !ran.contains("102|"),
              "计费：脚本只查这一轮新出现的运行中作业")
        let pbsHost = HostConfig(alias: "z", kind: .pbs)
        let pbsScript = RemoteScript.build(for: pbsHost, alloc: .init())
        check(pbsScript.contains("si_pbs t") && SelfTestPBS.syntaxOK(pbsScript), "计费：PBS 从这一轮的 qstat 全文里取资源")
        check(Parsers.pbsQueues(["Queue              Max   Tot Ena Str   Que   Run   Hld   Wat   Trn   Ext Type",
                                 "---------------- ----- ----- --- --- ----- ----- ----- ----- ----- ----- ----",
                                 "workq                0     3 yes yes     0     3     0     0     0     0 Exec",
                                 "gpu                  0     1 yes yes     1     0     0     0     0     0 Exec", ""]) == ["workq", "gpu"],
              "计费：qstat -Q 的队列名")

        for (ok, name) in engineScenarios() { check(ok, name) }
    }

    private static func output(_ secs: [(String, Int32?, [String])]) -> RemoteScript.Parsed {
        var lines = ["@@SI:BEGIN"]
        for (name, rc, body) in secs {
            lines.append("@@SI:SEC \(name)")
            lines += body
            if let rc { lines.append("@@SI:RC \(rc)") }
        }
        lines.append("@@SI:END")
        return RemoteScript.parse(lines.joined(separator: "\n"))
    }

    /// 引擎：查询资源 → 运行中的费用 → 记账（补记 ServerIsland 退出期间结束的作业）。不连接服务器。
    private static func engineScenarios() -> [(Bool, String)] {
        final class Results: @unchecked Sendable { var items: [(Bool, String)] = [] }
        let results = Results()
        let sem = DispatchSemaphore(value: 0)
        Task { @MainActor in
            let lang = Lang.setting
            let store = ConfigStore(persist: false)
            store.settings.soundEnabled = false
            store.settings.islandAlerts = false
            let engine = MonitorEngine(store: store)
            var h = HostConfig(alias: "x", kind: .slurm)
            h.enabled = false
            h.prices = [PartitionPrice(partition: "gpu", unit: .gpu, price: 2)]
            store.settings.hosts = [h]
            let rows = ["101|train|RUNNING|1:00:00|2:00:00|1|cn01|gpu", "102|wait|PENDING|0:00|2:00:00|1|(Priority)|gpu",
                        "700_1|scan|RUNNING|30:00|1:00:00|1|cn02|gpu|700|1"]
            await engine.ingest(host: h, parsed: output([("squeue", 0, rows)]), latency: 0.1)
            results.items.append((engine.allocQuery(h) == nil, "计费引擎：没打开计费时不查资源"))
            store.settings.billingEnabled = true
            results.items.append((engine.allocQuery(h)?.query == ["101", "700_1"], "计费引擎：只查运行中的作业（含数组元素），排队的不查"))
            await engine.ingest(host: h, parsed: output([("squeue", 0, rows), ("alloc", 0, ["101|cpu=8,node=1,gres/gpu=2", "700_1|cpu=4,node=1,gres/gpu=1"])]), latency: 0.1)
            let jobs = engine.snapshots[h.id]?.jobs ?? []
            let j101 = jobs.first { $0.id == "101" }, arr = jobs.first { $0.id == "700" }
            results.items.append((j101?.alloc?.gpus == 2 && engine.allocQuery(h) == .init(query: [], known: ["101", "700_1"]), "计费引擎：查到的资源记到作业上，下一轮不再查"))
            let live = j101.flatMap { engine.liveCost(h, $0) }
            results.items.append((live?.spent == 4 && live?.perHour == 4, "计费引擎：运行中作业的费用（2 卡 × 1 小时 × 2）"))
            let al = arr.flatMap { engine.liveCost(h, $0) }
            results.items.append((al?.spent == 1 && al?.perHour == 2 && engine.totalBurnRate == 6, "计费引擎：数组按运行中的元素算，全部合计每小时 6"))
            results.items.append((jobs.first { $0.id == "102" }.flatMap { engine.liveCost(h, $0) } == nil, "计费引擎：排队的作业不算钱"))
            let running = engine.costEntriesWithRunning()
            results.items.append((running.count == 2 && abs(running.reduce(0) { $0 + $1.cost } - 5) < 1e-6,
                                  "计费引擎：今天 / 本月包括正在跑的任务到上次刷新为止的费用（4 + 1）"))

            // 补记退出期间结束的作业：用 sacct 的 AllocTRES 和最终时长
            let gone = [HistoryStore.ActiveJob(id: "101", name: "train", slurm: true, elapsed: 3600, seenAt: Date(), partition: "gpu", nodes: 1,
                                               alloc: JobAlloc(cpus: 8, gpus: 2, nodes: 1))]
            engine.recordEndedWhileAway(host: h, gone: gone, finals: Parsers.sacct(["101|train|COMPLETED|02:00:00|0:0|cpu=8,node=1,gres/gpu=4"]),
                                        allocs: Parsers.sacctAllocs(["101|train|COMPLETED|02:00:00|0:0|cpu=8,node=1,gres/gpu=4"]))
            results.items.append((engine.costs.cost(host: h.id, id: "101") == 16, "计费引擎：结束后按 sacct 的资源和时长记账（4 卡 × 2 小时 × 2）"))
            h.prices[0].price = 5
            store.settings.hosts = [h]
            results.items.append((engine.costs.cost(host: h.id, id: "101") == 16, "计费引擎：改单价不影响已经记下的账"))
            engine.recordCost(host: h, id: "700_1", arrayBase: "700", name: "scan", partition: "gpu", alloc: JobAlloc(cpus: 4, gpus: 1, nodes: 1),
                              nodes: 1, seconds: 3600)
            engine.recordCost(host: h, id: "700_1", arrayBase: "700", name: "scan", partition: "gpu", alloc: JobAlloc(cpus: 4, gpus: 1, nodes: 1),
                              nodes: 1, seconds: 3600)
            results.items.append((engine.costs.cost(host: h.id, id: "700") == 5, "计费引擎：数组按元素记账，同一个元素不重复记"))
            store.settings.billingEnabled = false
            engine.recordCost(host: h, id: "999", name: "x", partition: "gpu", alloc: JobAlloc(cpus: 1, gpus: 1, nodes: 1), nodes: 1, seconds: 3600)
            results.items.append((engine.costs.cost(host: h.id, id: "999") == nil, "计费引擎：关闭计费后不再记账"))
            Lang.setting = lang
            sem.signal()
        }
        while sem.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
        return results.items
    }
}
