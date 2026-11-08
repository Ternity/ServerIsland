using System.Text.Json;

namespace ServerIsland.Core;

/// 自检：计费（TRES 解析、PBS 的资源换算、单价查找、费用计算、账本、汇总、配置升级、远端脚本、引擎记账）。
/// 和 Swift 版 SelfTestBilling.swift 逐项对应。
public static class SelfTestBilling
{
    /// PBS Pro（ncpus / ngpus）、Torque（nodes=2:ppn=16:gpus=2）、只写了 nodect 的作业。文本和 Swift 版一致。
    public const string PbsSample = """
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

        """;

    public static void Run(Action<bool, string> check, bool hasBash)
    {
        // —— TRES ——
        check(JobAlloc.Parse("cpu=32,mem=128G,node=1,billing=32,gres/gpu=4,gres/gpu:a100=4") == new JobAlloc(32, 4, 1),
              "计费：TRES 里同时有 gres/gpu 和分型号时只算总数");
        check(JobAlloc.Parse("cpu=8,node=1,gres/gpu:a100=2,gres/gpu:v100=1")?.Gpus == 3
              && JobAlloc.Parse("cpu=4,gres/dcu=4,node=1")?.Gpus == 4
              && JobAlloc.Parse("cpu=64,mem=200G,node=2")?.Gpus == 0
              && JobAlloc.Parse("") == null && JobAlloc.Parse("(null)") == null,
              "计费：分型号相加、DCU 算加速卡、只有 CPU 时是 0 张卡、空值不算");
        check(Parsers.Allocs(["123|cpu=4,node=1,gres/gpu=1", "124|", "125_3|cpu=2,node=1"]).Keys.Order(StringComparer.Ordinal).SequenceEqual(["123", "125_3"]),
              "计费：alloc 段（作业号|TRES），查不到的作业跳过");
        // 没把 gres/gpu 写进 AccountingStorageTRES 的 Slurm：AllocTRES 里没有卡，按申请的卡数算
        var req = Parsers.Allocs(["1712|cpu=4,mem=30000M,node=1,billing=4|gres/gpu:1||1", "1713|cpu=8,node=2|gres/gpu:a100:2||2",
                                  "1714|cpu=8,node=1||gres/gpu:4|1", "1715|cpu=8,node=1,gres/gpu=2|gres/gpu:1||1",
                                  "1716|cpu=8,node=1|gres:gpu:1,gres/shard:2||1", "1717|cpu=8,node=1|||1"]);
        check(req["1712"].Gpus == 1 && req["1713"].Gpus == 4 && req["1714"].Gpus == 4 && req["1715"].Gpus == 2
              && req["1716"].Gpus == 1 && req["1717"].Gpus == 0,
              "计费：AllocTRES 没有卡时按申请的卡数（每节点 × 节点数 / 整个作业），有卡时以分到的为准");
        check(JobAlloc.Best(new JobAlloc(4, 0, 1), new JobAlloc(4, 1, 1)) == new JobAlloc(4, 1, 1)
              && JobAlloc.Best(new JobAlloc(4, 2, 1), new JobAlloc(4, 1, 1))?.Gpus == 2
              && JobAlloc.Best(null, new JobAlloc(1, 1, 1))?.Gpus == 1,
              "计费：结束时 sacct 里没有卡、运行时看到过卡，卡数用运行时的");
        string[] sacctLines = ["9001|relax|COMPLETED|01:00:00|0:0|cpu=64,mem=100G,node=2", "9002|old|FAILED|00:10:00|1:0", "9003|x|COMPLETED|00:01:00|0:0|"];
        var sa = Parsers.SacctAllocs(sacctLines);
        check(sa.Count == 1 && sa["9001"] == new JobAlloc(64, 0, 2)
              && Parsers.Sacct(sacctLines)["9001"].Elapsed == 3600 && Parsers.Sacct(sacctLines)["9001"].Exit == "0:0",
              "计费：sacct 多了 AllocTRES 一列，原来的解析不受影响");
        var q = Parsers.Squeue(["101|relax|RUNNING|1:00:00|2:00:00|2|cn[01-02]|gpu-a100|101|N/A"]);
        check(q.FirstOrDefault()?.Partition == "gpu-a100" && q[0].Nodes == 2, "计费：squeue 记下分区和节点数");

        // —— PBS：si_pbs t / a 输出资源 ——
        if (hasBash)
        {
            string Si(string mode) => SelfTest.Bash(RemoteScript.PbsFunction + $"si_pbs {mode} <<'SI_EOF'\n" + PbsSample + "SI_EOF\n");
            var tl = Parsers.Allocs(Si("t").Split('\n'));
            check(tl.GetValueOrDefault("3001") == new JobAlloc(16, 4, 1) && tl.GetValueOrDefault("3002") == new JobAlloc(32, 4, 2)
                  && !tl.ContainsKey("3003") && !tl.ContainsKey("3004"),
                  "计费：PBS Pro 的 ncpus / ngpus、Torque 的 nodes=2:ppn=16:gpus=2，只输出运行中的作业");
            var finLines = Si("a").Split('\n');
            check(Parsers.SacctAllocs(finLines).GetValueOrDefault("3004") == new JobAlloc(8, 1, 1) && Parsers.Sacct(finLines)["3004"].State == JobState.Completed,
                  "计费：PBS 结束状态后面带上资源");
            var pq = Parsers.Squeue(Si("q").Split('\n').Where(l => l.Length > 0));
            check(pq.FirstOrDefault(j => j.Id == "3001")?.Partition == "gpu", "计费：PBS 的队列当作分区");
        }

        // —— 单价和费用 ——
        var h = new HostConfig("x", kind: HostKind.Slurm)
        {
            Prices = [new PartitionPrice("", PriceUnit.Cpu, 0.05), new PartitionPrice("gpu", PriceUnit.Gpu, 2.5), new PartitionPrice("free", PriceUnit.Gpu, 0)],
        };
        check(Billing.Price(h, "gpu")?.Price == 2.5 && Billing.Price(h, "other")?.Price == 0.05
              && Billing.Price(h, "free") == null && Billing.Price(h, null)?.Unit == PriceUnit.Cpu,
              "计费：先找同名分区，再用“其他分区”；单价为 0 的分区不计费");
        var c = Billing.Cost(h, "gpu", new JobAlloc(16, 4, 1), 1, 5400);
        check(c != null && Math.Abs(c.Cost - 15) < 1e-9 && Math.Abs(c.PerHour - 10) < 1e-9 && c.Amount == 4, "计费：4 卡 × 1.5 小时 × 2.5 = 15");
        check(Billing.Cost(h, "gpu", null, 1, 60) == null && Billing.Cost(h, "gpu", new JobAlloc(1, 1, 1), 1, null) == null,
              "计费：不知道资源量或时长时不算");
        var nh = new HostConfig("x", kind: HostKind.Slurm) { Prices = [new PartitionPrice("", PriceUnit.Node, 10)] };
        check(Billing.Cost(nh, "x", null, 3, 3600)?.Cost == 30, "计费：按节点时可以用 squeue 的节点数");
        check(Billing.Money(12.4, "¥") == "¥12.40" && Billing.Money(0.04, "$") == "$0.040" && Billing.Money(12345.6, "¥") == "¥12346", "计费：金额格式");

        // —— 汇总 ——
        var now = new DateTime(2026, 11, 5, 15, 0, 0);
        var hid = Guid.NewGuid();
        CostEntry E(string id, string? bas, int day, int month = 11, double hours = 1) => new()
        {
            Host = hid, Id = id, ArrayBase = bas, Name = bas ?? id, Partition = "gpu", Unit = PriceUnit.Gpu, Amount = 2,
            Seconds = hours * 3600, Price = 1, EndedAt = new DateTime(2026, month, day, 10, 0, 0),
        };
        var s = new CostSummary([E("1", null, 5), E("2", null, 3, hours: 2), E("7_1", "7", 4), E("7_2", "7", 4),
                                 E("0", null, 20, month: 10, hours: 10), E("old", null, 20, month: 9, hours: 100)], now);
        check(s.Today == 2 && s.Month == 10 && s.LastMonth == 20 && s.Days.Count == 5 && s.Days[3] == 4 && s.Days[2] == 4,
              "计费汇总：今天 / 本月 / 上月和每天");
        check(s.Top.FirstOrDefault()?.Id == "7" && s.Top[0].Elements == 2 && s.Top[0].Cost == 4 && s.Groups.Count == 1 && s.Groups[0].Hours == 10,
              "计费汇总：数组的元素合成一行，按分区合计卡时");
        // 跨零点的任务：两天各算一部分（11 月 5 日 02:00 结束、跑了 4 小时 → 4 日 2 小时、5 日 2 小时）
        var cs = new CostSummary([new CostEntry { Host = hid, Id = "x", Name = "x", Partition = "gpu", Unit = PriceUnit.Gpu, Amount = 1,
                                                  Seconds = 4 * 3600, Price = 1, EndedAt = new DateTime(2026, 11, 5, 2, 0, 0) }], now);
        check(cs.Today == 2 && cs.Month == 4 && cs.Days[3] == 2 && cs.Days[4] == 2, "计费汇总：跨零点的任务按实际运行时间摊到两天");

        // —— 配置 ——
        var old = JsonSerializer.Deserialize<HostConfig>("""{"alias":"x","kind":"slurm","commands":{"sacct":"sacct -n -X -P -o JobID,JobName,State,Elapsed,ExitCode -j {jobs}"}}""", Json.Options);
        check(old?.Commands.Sacct == CommandSet.Defaults.Sacct && old.Prices.Count == 0 && CommandSet.Defaults.Sacct.Contains("AllocTRES"),
              "计费：旧版默认的 sacct 命令自动加上 AllocTRES，旧配置没有单价");
        var back = Json.Clone(h);
        check(back.Prices.Select(p => (p.Id, p.Partition, p.Unit, p.Price)).SequenceEqual(h.Prices.Select(p => (p.Id, p.Partition, p.Unit, p.Price))),
              "计费：单价保存后读回一致");
        var st = JsonSerializer.Deserialize<AppSettings>("{}", Json.Options);
        check(st?.BillingEnabled == false && st.Currency == "¥", "计费：默认关闭，货币默认 ¥");
        // Mac 版写的配置（单价、计价方式）能直接读
        var mac = JsonSerializer.Deserialize<HostConfig>("""{"alias":"x","kind":"slurm","prices":[{"id":"11111111-2222-3333-4444-555555555555","partition":"gpu","unit":"gpu","price":2.5,"note":""}]}""", Json.Options);
        check(mac?.Prices.FirstOrDefault()?.Unit == PriceUnit.Gpu && mac.Prices[0].Price == 2.5
              && JsonSerializer.Serialize(mac, Json.Options).Contains("\"11111111-2222-3333-4444-555555555555\""),
              "计费：和 Mac 版的配置格式一致");

        // —— 远端脚本：只有打开计费时才多一段 ——
        var plain = RemoteScript.Build(h);
        var withAlloc = RemoteScript.Build(h, alloc: new(["9"], ["123", "456_7", "bad;rm"]));
        check(!plain.Contains("SEC alloc") && withAlloc.Contains("SI_K=' 123 456_7 '") && !withAlloc.Contains("bad;rm") && !withAlloc.Contains("'9'")
              && (!hasBash || SelfTest.BashSyntax(withAlloc).Code == 0), "计费：默认 squeue 时同一轮从列表里找新的运行中作业，跳过已知的，作业号经过校验，语法正确");
        var customH = Json.Clone(h);
        customH.Commands.Squeue = "squeue -u \"$(id -un)\" -h -o '%i|%j|%T|%M|%l|%D|%R|%P' -p gpu";
        var customAlloc = RemoteScript.Build(customH, alloc: new(["123", "456_7", "bad;rm"], []));
        check(customAlloc.Contains("for j in '123' '456_7'; do") && !customAlloc.Contains("bad;rm") && (!hasBash || SelfTest.BashSyntax(customAlloc).Code == 0)
              && !RemoteScript.Build(customH, alloc: RemoteScript.AllocRequest.Empty).Contains("SEC alloc"),
              "计费：自定义 squeue 时查上一轮看到的作业；没有要查的作业时不加这一段");
        if (hasBash)
        {
            // 在本机用假的 squeue 结果和假的 scontrol 跑一遍：只查新的运行中作业
            var sec = string.Join("\n", withAlloc.Split('\n').SkipWhile(l => !l.Contains("SEC alloc")).Take(3));
            var fake = "scontrol() { echo \"JobId=$4 AllocTRES=cpu=4,node=1 TresPerNode=gres/gpu:1 NumNodes=1\"; }\nSI_JW='101|RUNNING|/a\n102|PENDING|/b\n103_2|RUNNING|/c'\n"
                       + sec.Replace("' 123 456_7 '", "' 101 '");
            var ran = SelfTest.Bash(fake);
            check(ran.Contains("103_2|cpu=4,node=1|gres/gpu:1||1") && !ran.Contains("101|") && !ran.Contains("102|"), "计费：脚本只查这一轮新出现的运行中作业");
        }
        var pbsScript = RemoteScript.Build(new HostConfig("z", kind: HostKind.Pbs), alloc: RemoteScript.AllocRequest.Empty);
        check(pbsScript.Contains("si_pbs t") && (!hasBash || SelfTest.BashSyntax(pbsScript).Code == 0), "计费：PBS 从这一轮的 qstat 全文里取资源");
        check(Parsers.PbsQueues(["Queue              Max   Tot Ena Str   Que   Run   Hld   Wat   Trn   Ext Type",
                                 "---------------- ----- ----- --- --- ----- ----- ----- ----- ----- ----- ----",
                                 "workq                0     3 yes yes     0     3     0     0     0     0 Exec",
                                 "gpu                  0     1 yes yes     1     0     0     0     0     0 Exec", ""]).SequenceEqual(["workq", "gpu"]),
              "计费：qstat -Q 的队列名");

        var lang = Lang.Setting;   // 新建的 ConfigStore 会把语言设成默认值，测完改回来
        EngineScenarios(check).GetAwaiter().GetResult();
        Lang.Setting = lang;
    }

    static RemoteScript.Parsed Output(params (string Name, int Rc, string[] Lines)[] secs)
    {
        var lines = new List<string> { "@@SI:BEGIN" };
        foreach (var (name, rc, body) in secs) { lines.Add($"@@SI:SEC {name}"); lines.AddRange(body); lines.Add($"@@SI:RC {rc}"); }
        lines.Add("@@SI:END");
        return RemoteScript.Parse(string.Join("\n", lines));
    }

    /// 引擎：查询资源 → 运行中的费用 → 记账（补记 ServerIsland 退出期间结束的作业）。不连接服务器。
    static async Task EngineScenarios(Action<bool, string> check)
    {
        var store = new ConfigStore(persist: false);
        store.Settings.SoundEnabled = false;
        store.Settings.IslandAlerts = false;
        var engine = new MonitorEngine(store, history: new HistoryStore(persist: false));
        var h = new HostConfig("x", kind: HostKind.Slurm) { Enabled = false, Prices = [new PartitionPrice("gpu", PriceUnit.Gpu, 2)] };
        store.Settings.Hosts = [h];
        string[] rows = ["101|train|RUNNING|1:00:00|2:00:00|1|cn01|gpu", "102|wait|PENDING|0:00|2:00:00|1|(Priority)|gpu",
                         "700_1|scan|RUNNING|30:00|1:00:00|1|cn02|gpu|700|1"];
        await engine.Ingest(h, Output(("squeue", 0, rows)), 0.1);
        check(engine.AllocQuery(h) == null, "计费引擎：没打开计费时不查资源");
        store.Settings.BillingEnabled = true;
        check(engine.AllocQuery(h)?.Query.SequenceEqual(["101", "700_1"]) == true, "计费引擎：只查运行中的作业（含数组元素），排队的不查");
        await engine.Ingest(h, Output(("squeue", 0, rows), ("alloc", 0, ["101|cpu=8,node=1,gres/gpu=2", "700_1|cpu=4,node=1,gres/gpu=1"])), 0.1);
        var jobs = engine.Snapshots[h.Id].Jobs;
        var j101 = jobs.FirstOrDefault(j => j.Id == "101");
        var arr = jobs.FirstOrDefault(j => j.Id == "700");
        check(j101?.Alloc?.Gpus == 2 && engine.AllocQuery(h)?.Same(new([], ["101", "700_1"])) == true, "计费引擎：查到的资源记到作业上，下一轮不再查");
        var live = j101 == null ? null : engine.JobCost(h, j101);
        check(live?.Spent == 4 && live.PerHour == 4, "计费引擎：运行中作业的费用（2 卡 × 1 小时 × 2）");
        var al = arr == null ? null : engine.JobCost(h, arr);
        check(al?.Spent == 1 && al.PerHour == 2 && engine.TotalBurnRate == 6, "计费引擎：数组按运行中的元素算，全部合计每小时 6");
        check(jobs.First(j => j.Id == "102") is var p && engine.JobCost(h, p) == null, "计费引擎：排队的作业不算钱");
        var running = engine.CostEntriesWithRunning();
        check(running.Count == 2 && Math.Abs(running.Sum(e => e.Cost) - 5) < 1e-6, "计费引擎：今天 / 本月包括正在跑的任务到上次刷新为止的费用（4 + 1）");

        // 补记退出期间结束的作业：用 sacct 的 AllocTRES 和最终时长
        var gone = new List<HistoryStore.ActiveJob> { new() { Id = "101", Name = "train", Slurm = true, Elapsed = 3600, SeenAt = DateTime.Now,
                                                             Partition = "gpu", Nodes = 1, Alloc = new JobAlloc(8, 2, 1) } };
        string[] fin = ["101|train|COMPLETED|02:00:00|0:0|cpu=8,node=1,gres/gpu=4"];
        engine.RecordEndedWhileAway(h, gone, Parsers.Sacct(fin), Parsers.SacctAllocs(fin));
        check(engine.Costs.CostOf(h.Id, "101") == 16, "计费引擎：结束后按 sacct 的资源和时长记账（4 卡 × 2 小时 × 2）");
        h.Prices[0].Price = 5;
        check(engine.Costs.CostOf(h.Id, "101") == 16, "计费引擎：改单价不影响已经记下的账");
        engine.RecordCost(h, "700_1", "scan", "gpu", new JobAlloc(4, 1, 1), 1, 3600, arrayBase: "700");
        engine.RecordCost(h, "700_1", "scan", "gpu", new JobAlloc(4, 1, 1), 1, 3600, arrayBase: "700");
        check(engine.Costs.CostOf(h.Id, "700") == 5, "计费引擎：数组按元素记账，同一个元素不重复记");
        store.Settings.BillingEnabled = false;
        engine.RecordCost(h, "999", "x", "gpu", new JobAlloc(1, 1, 1), 1, 3600);
        check(engine.Costs.CostOf(h.Id, "999") == null, "计费引擎：关闭计费后不再记账");
    }
}
