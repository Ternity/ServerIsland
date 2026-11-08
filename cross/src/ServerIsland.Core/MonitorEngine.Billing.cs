using ServerIsland.Core.Ssh;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// 计费（估算）：查询分到的资源、作业结束时记账、运行中作业的实时费用、读取分区给设置页用。
/// 和 Swift 版的 MonitorEngine+Billing.swift 对应。
public sealed partial class MonitorEngine
{
    /// 这台机器要不要算费用：打开了计费、是调度系统、至少有一个分区填了单价。
    public bool BillingActive(HostConfig host) =>
        Store.Settings.BillingEnabled && host.Kind.IsScheduler() && host.WatchJobs && host.Prices.Any(p => p.Price > 0);

    /// 这一轮要查资源的作业：Slurm 是还没查过的运行中作业（含数组元素）；PBS 返回空列表（直接从 qstat 全文里取）。不计费时返回 null。
    public RemoteScript.AllocRequest? AllocQuery(HostConfig host)
    {
        if (!BillingActive(host)) return null;
        if (host.Kind != HostKind.Slurm) return RemoteScript.AllocRequest.Empty;
        var known = AllocCache.GetValueOrDefault(host.Id) ?? [];
        var jobs = Snapshots.GetValueOrDefault(host.Id)?.Jobs ?? [];
        var running = jobs.SelectMany(j => j.Array is { } a ? a.Running
            : j.Source == JobSource.Slurm && j.State == JobState.Running ? [j] : new List<RemoteJob>());
        return new RemoteScript.AllocRequest(running.Select(j => j.Id).Where(id => !known.ContainsKey(id)).ToList(),
                                             known.Keys.Order(StringComparer.Ordinal).ToList());
    }

    /// 作业结束时记一笔（没打开计费、没有单价或不知道资源量时不记）。
    /// 返回这次记下的费用（没开计费、不知道单价或资源时为 null），推送时显示。
    public double? RecordCost(HostConfig host, string id, string name, string? partition, JobAlloc? alloc, int? nodes, double? seconds,
                           string? arrayBase = null, DateTime? endedAt = null)
    {
        if (!BillingActive(host) || Billing.Cost(host, partition, alloc, nodes, seconds) is not { } c) return null;
        Costs.Record(new CostEntry
        {
            Host = host.Id, Id = id, ArrayBase = arrayBase, Name = name, Partition = partition, Unit = c.Price.Unit,
            Amount = c.Amount, Seconds = seconds ?? 0, Price = c.Price.Price, EndedAt = endedAt ?? DateTime.Now,
        });
        DebugLog.Log($"[{host.DisplayName}] 记账 {id}：{c.Price.Unit.Count(c.Amount)} × {Format.Duration(seconds ?? 0)} = {Billing.Money(c.Cost, Store.Settings.Currency)}");
        return c.Cost;
    }

    /// Spent：到现在为止花了多少（数组包括已经结束的元素）；PerHour：现在每小时花多少；Detail："4 卡 × ¥2.50/卡时"。
    public sealed record LiveCost(double Spent, double PerHour, string Detail);

    /// 运行中（或刚结束）的作业的费用。不知道资源量时返回 null。
    public LiveCost? JobCost(HostConfig host, RemoteJob job)
    {
        if (!BillingActive(host) || job.Source != JobSource.Slurm) return null;
        var cur = Store.Settings.Currency;
        if (job.Array is { } a)
        {
            var done = Costs.CostOf(host.Id, a.BaseID) ?? 0;
            var parts = a.Running.Select(e => Billing.Cost(host, e.Partition, e.Alloc, e.Nodes, e.Elapsed)).OfType<CostEstimate>().ToList();
            if (parts.Count == 0 && done <= 0) return null;
            var detail = parts.Count == 0 ? "" : L($"{a.Running.Count} 个元素运行中 · 每个 ", $"{a.Running.Count} running · each ")
                + parts[0].Price.Unit.Count(parts[0].Amount) + " × " + Billing.Money(parts[0].Price.Price, cur) + "/" + parts[0].Price.Unit.PerLabel();
            return new LiveCost(done + parts.Sum(p => p.Cost), parts.Sum(p => p.PerHour), detail);
        }
        if (!job.State.IsActive() && Costs.CostOf(host.Id, job.Id) is { } recorded) return new LiveCost(recorded, 0, "");
        if (job.State == JobState.Pending || Billing.Cost(host, job.Partition, job.Alloc, job.Nodes, job.Elapsed) is not { } c) return null;
        return new LiveCost(c.Cost, job.State == JobState.Running ? c.PerHour : 0,
            c.Price.Unit.Count(c.Amount) + " × " + Billing.Money(c.Price.Price, cur) + "/" + c.Price.Unit.PerLabel());
    }

    /// 费用汇总用的记录：账本里已经结束的，加上正在跑的任务"到上次刷新为止"花的（每次刷新都会变，不写进账本）。
    /// 数组按运行中的元素算；结束时间取那台机器上次刷新的时间，所以两次刷新之间数字不跳。
    public List<CostEntry> CostEntriesWithRunning()
    {
        var list = Costs.Entries;
        foreach (var host in Store.Settings.Hosts.Where(BillingActive))
        {
            if (Snapshots.GetValueOrDefault(host.Id) is not { } snap) continue;
            var at = snap.LastUpdate ?? DateTime.Now;
            foreach (var job in snap.Jobs.Where(j => j.Source == JobSource.Slurm))
            {
                var parts = job.Array is { } a ? a.Running.Select(e => (Job: e, Base: (string?)a.BaseID))
                    : job.State == JobState.Running ? [(job, (string?)null)] : [];
                foreach (var (j, bas) in parts)
                {
                    if (j.Elapsed is not > 0 || Billing.Cost(host, j.Partition, j.Alloc, j.Nodes, j.Elapsed) is not { } c) continue;
                    list.Add(new CostEntry
                    {
                        Host = host.Id, Id = j.Id, ArrayBase = bas, Name = j.Name, Partition = j.Partition, Unit = c.Price.Unit,
                        Amount = c.Amount, Seconds = j.Elapsed.Value, Price = c.Price.Price, EndedAt = at,
                    });
                }
            }
        }
        return list;
    }

    /// 所有服务器上正在运行的作业每小时一共花多少。
    public double TotalBurnRate => Store.Settings.Hosts.Sum(h =>
        (Snapshots.GetValueOrDefault(h.Id)?.Jobs ?? []).Sum(j => JobCost(h, j)?.PerHour ?? 0));

    public sealed record BillingPartition(string Name, string Note, bool HasGpu);

    /// 设置页"从服务器读取分区"：Slurm 用 sinfo（和分区页同一个查询），PBS 用 qstat -Q 列出队列。读不到时返回错误信息。
    public async Task<(List<BillingPartition> List, string? Error)> FetchBillingPartitions(HostConfig host)
    {
        if (host.Kind == HostKind.Slurm)
        {
            var r = await FetchPartitions(host, force: true);
            if (r.Stats is not { } stats) return ([], r.Error ?? L("sinfo 不可用", "sinfo unavailable"));
            return (stats.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p =>
            {
                var note = L($"{p.Nodes} 节点", $"{p.Nodes} nodes");
                if (p.GpuTotal > 0)
                {
                    var models = string.Join("/", p.GpuModels);
                    note += " · " + (models.Length == 0 ? "" : models + " ") + L($"共 {p.GpuTotal} 张{p.GpuLabel}", $"{p.GpuTotal} {p.GpuLabel}s");
                }
                else note += " · " + L($"共 {p.CpuTotal} 核", $"{p.CpuTotal} cores");
                return new BillingPartition(p.Name, note, p.GpuTotal > 0);
            }).ToList(), null);
        }
        var res = await Run(host, RemoteScript.PbsQueuesScript(host), 25);
        if (SshCommon.Classify(res, host) is { } problem) return ([], problem.Message);
        if (RemoteScript.Parse(res.Stdout).Get("queues") is not { ExitCode: 0 } sec) return ([], L("qstat -Q 不可用", "qstat -Q unavailable"));
        return (Parsers.PbsQueues(sec.Lines).Select(q => new BillingPartition(q, "", false)).ToList(), null);
    }
}
