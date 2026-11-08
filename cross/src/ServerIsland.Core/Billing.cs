using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

// 计费（估算）：用户给每个分区填单价，ServerIsland 用作业占用的资源 × 运行时长算出花了多少钱。
// 只算 Slurm / PBS 的作业；资源量来自调度系统的分配记录（运行时 scontrol show job 的 AllocTRES / TRES，结束后 sacct 的 AllocTRES）。
// 和 Swift 版的 Models/Billing.swift 对应。

/// 计价方式。存储的值 gpu / cpu / node 和 Mac 版一致。
public enum PriceUnit { Gpu, Cpu, Node }

public static class PriceUnitText
{
    /// 设置里的选项名。
    public static string Label(this PriceUnit u) => u switch
    {
        PriceUnit.Gpu => L("按卡时", "Per GPU-hour"),
        PriceUnit.Cpu => L("按核时", "Per core-hour"),
        _ => L("按节点时", "Per node-hour"),
    };

    /// 单价后面的单位："/卡时"。
    public static string PerLabel(this PriceUnit u) => u switch
    {
        PriceUnit.Gpu => L("卡时", "GPU-h"),
        PriceUnit.Cpu => L("核时", "core-h"),
        _ => L("节点时", "node-h"),
    };

    /// 资源量："4 卡"。
    public static string Count(this PriceUnit u, double n)
    {
        var v = n == Math.Round(n) ? ((int)n).ToString(CultureInfo.InvariantCulture) : n.ToString("0.0", CultureInfo.InvariantCulture);
        var s = n == 1 ? "" : "s";
        return u switch
        {
            PriceUnit.Gpu => L($"{v} 卡", $"{v} GPU{s}"),
            PriceUnit.Cpu => L($"{v} 核", $"{v} core{s}"),
            _ => L($"{v} 节点", $"{v} node{s}"),
        };
    }
}

/// 一个分区的单价。`Partition` 为空表示"其他分区"（没有单独设置的分区都按它算）。
public sealed class PartitionPrice
{
    [JsonConverter(typeof(UpperGuidConverter))]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Partition { get; set; } = "";
    public PriceUnit Unit { get; set; } = PriceUnit.Gpu;
    /// 每单位每小时的价格；0 表示不计费。
    public double Price { get; set; }
    /// 读取分区时记下的说明（卡的型号和数量），只用于设置页显示。
    public string Note { get; set; } = "";

    public PartitionPrice() { }
    public PartitionPrice(string partition, PriceUnit unit, double price, string note = "")
    {
        Partition = partition; Unit = unit; Price = price; Note = note;
    }
}

/// 作业分到的资源（来自 TRES 字符串，如 `cpu=32,mem=128G,node=1,billing=32,gres/gpu=4`）。
public sealed record JobAlloc(int? Cpus = null, int? Gpus = null, int? Nodes = null)
{
    static readonly string[] Kinds = ["gpu", "dcu", "npu", "mlu"];

    /// 加速卡和 Partitions 一样认 gpu / dcu / npu / mlu。同一种卡既有总数（gres/gpu=4）又有分型号（gres/gpu:a100=4）时只用总数。
    public static JobAlloc? Parse(string tres)
    {
        int? cpus = null, gpus = null, nodes = null;
        var plain = new Dictionary<string, int>();
        var typed = new Dictionary<string, int>();
        foreach (var part in tres.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            if (key == "cpu") cpus = n;
            else if (key == "node") nodes = n;
            else if (key.StartsWith("gres/"))
            {
                var name = key[5..];
                var kind = name.Split(':', 2)[0];
                if (!Kinds.Contains(kind)) continue;
                var map = name.Contains(':') ? typed : plain;
                map[kind] = map.GetValueOrDefault(kind) + n;
            }
        }
        var all = plain.Keys.Union(typed.Keys).ToList();
        if (all.Count > 0) gpus = all.Sum(k => plain.TryGetValue(k, out var p) ? p : typed.GetValueOrDefault(k));
        if (cpus == null && gpus == null && nodes == null) return null;
        // 写明了 CPU 却没有加速卡：就是 0 张卡（按卡时计费的分区里算 0 元）
        if (gpus == null && cpus != null) gpus = 0;
        return new JobAlloc(cpus, gpus, nodes);
    }

    /// 分到的资源里没有加速卡时，按申请的卡数算：有的 Slurm 没把 gres/gpu 写进 AccountingStorageTRES，
    /// 作业明明分到了卡，AllocTRES 里却没有（GPU 是整张分配的，申请几张就占几张）。
    /// `perNode` / `perJob`：scontrol 的 TresPerNode（--gres=gpu:1）/ TresPerJob（--gpus=4），如 `gres/gpu:1`、`gres/gpu:a100:2`、老版本的 `gres:gpu:1`。
    public static JobAlloc? Parse(string tres, string perNode, string perJob, int? nodes)
    {
        var a = Parse(tres);
        if ((a?.Gpus ?? 0) != 0) return a;
        var n = a?.Nodes ?? nodes ?? 1;
        var req = RequestedGpus(perJob) ?? RequestedGpus(perNode) * Math.Max(n, 1);
        if (req is not > 0) return a;
        return (a ?? new JobAlloc(null, null, nodes)) with { Gpus = req };
    }

    /// `gres/gpu:2,gres/gpu:a100:1` -> 3；没有加速卡时返回 null。
    public static int? RequestedGpus(string s)
    {
        int total = 0; bool found = false;
        foreach (var item in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = item.Trim().ToLowerInvariant();
            if (t.StartsWith("gres/") || t.StartsWith("gres:")) t = t[5..];
            var parts = t.Split(':');
            if (parts.Length == 0 || !Kinds.Contains(parts[0])) continue;
            found = true;
            total += parts.Length >= 2 ? (int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1) : 1;
        }
        return found ? total : null;
    }

    /// 结束时用哪份资源：sacct 的最终值为准，但它里面没有卡、运行时看到过卡时（同一个原因：没统计 gres/gpu），卡数用运行时的。
    public static JobAlloc? Best(JobAlloc? final, JobAlloc? seen)
    {
        if (final == null) return seen;
        return (final.Gpus ?? 0) == 0 && seen?.Gpus is > 0 ? final with { Gpus = seen.Gpus } : final;
    }

    /// 按计价方式取资源量；节点数没写时用 squeue 的节点数。
    public double? Amount(PriceUnit unit, int? fallbackNodes = null) => unit switch
    {
        PriceUnit.Gpu => Gpus,
        PriceUnit.Cpu => Cpus,
        _ => Nodes ?? fallbackNodes,
    };
}

public sealed record CostEstimate(double Cost, double PerHour, PartitionPrice Price, double Amount);

public static class Billing
{
    /// 这个分区的单价：先找同名的，再用"其他分区"。单价为 0 的不算。
    public static PartitionPrice? Price(HostConfig host, string? partition)
    {
        var name = (partition ?? "").Trim();
        if (name.Length > 0 && host.Prices.FirstOrDefault(p => p.Partition == name) is { } own)
            return own.Price > 0 ? own : null;
        return host.Prices.FirstOrDefault(p => p.Partition.Length == 0 && p.Price > 0);
    }

    /// 估算一个作业的费用。资源量或时长不知道时返回 null。
    public static CostEstimate? Cost(HostConfig host, string? partition, JobAlloc? alloc, int? nodes, double? seconds)
    {
        if (Price(host, partition) is not { } p || seconds is not { } s || s < 0) return null;
        if ((alloc ?? new JobAlloc()).Amount(p.Unit, nodes) is not { } n) return null;
        var perHour = n * p.Price;
        return new CostEstimate(perHour * s / 3600, perHour, p, n);
    }

    /// 估算的说明：费用页底部和设置页都显示。
    public static string Disclaimer => L("以上是按你填的单价和调度系统记录的资源估算的，仅供参考，实际费用以服务器供应商的账单为准。",
                                          "These are estimates from your prices and the resources the scheduler recorded. Your provider's bill is what counts.");

    /// 货币对应的图标名（Mac 版是 SF Symbols 的 yensign.circle 等，Windows / Linux 版见 Ui/Icons）。
    public static string IconFor(string currency) => currency switch { "$" => "dollar", "€" => "euro", "£" => "pound", _ => "yen" };

    /// 金额：¥12.40；很小的数多留一位，大数不要小数。
    public static string Money(double v, string currency)
    {
        var a = Math.Abs(v);
        var fmt = a >= 10000 ? "0" : a > 0 && a < 0.1 ? "0.000" : "0.00";
        return currency + v.ToString(fmt, CultureInfo.InvariantCulture);
    }
}

/// 一个已结束作业（或数组的一个元素）的费用记录。记下当时的单价：以后改价格不影响旧账。
public sealed class CostEntry
{
    public Guid Host { get; set; }
    public string Id { get; set; } = "";
    /// 数组元素所属的数组主编号。
    public string? ArrayBase { get; set; }
    public string Name { get; set; } = "";
    public string? Partition { get; set; }
    public PriceUnit Unit { get; set; }
    public double Amount { get; set; }
    public double Seconds { get; set; }
    public double Price { get; set; }
    public DateTime EndedAt { get; set; }

    [JsonIgnore] public double Cost => Amount * Price * Seconds / 3600;
}

/// 费用账本（cost-ledger.json），保留 400 天，用来按天 / 按月汇总。
public sealed class CostLedger
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);
    public const int MaxEntries = 20000;

    List<CostEntry> _entries = [];
    readonly string _path;
    readonly bool _persist;
    readonly object _lock = new();

    public CostLedger(bool persist = true, string? directory = null)
    {
        _persist = persist;
        _path = Path.Combine(directory ?? AppPaths.ConfigDirectory, "cost-ledger.json");
        if (!persist) return;
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<List<CostEntry>>(File.ReadAllText(_path), Json.Options) ?? [];
        }
        catch { }
        Prune();
    }

    public List<CostEntry> Entries { get { lock (_lock) return [.. _entries]; } }

    /// 同一台机器的同一个编号只记一次（再记时覆盖）。
    public void Record(CostEntry e)
    {
        lock (_lock)
        {
            _entries.RemoveAll(x => x.Host == e.Host && x.Id == e.Id);
            _entries.Add(e);
            Prune();
            Save();
        }
    }

    /// 演示 / 预览用：直接放入一批记录，不写文件。
    public void ReplaceAll(IEnumerable<CostEntry> list) { lock (_lock) _entries = [.. list]; }

    public void Clear() { lock (_lock) { _entries.Clear(); Save(); } }

    /// 一个作业的费用；数组按主编号把各元素加起来。
    public double? CostOf(Guid host, string id)
    {
        lock (_lock)
        {
            var mine = _entries.Where(e => e.Host == host && (e.Id == id || e.ArrayBase == id)).ToList();
            return mine.Count == 0 ? null : mine.Sum(e => e.Cost);
        }
    }

    void Prune()
    {
        var cutoff = DateTime.Now - Retention;
        _entries.RemoveAll(e => e.EndedAt < cutoff);
        if (_entries.Count > MaxEntries) _entries = _entries.OrderByDescending(e => e.EndedAt).Take(MaxEntries).ToList();
    }

    void Save()
    {
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json.Options));
            File.Move(tmp, _path, true);
        }
        catch { }
    }
}

/// 费用页的汇总（今天 / 本月 / 上月、按服务器和分区、按天、最贵的作业）。纯计算，便于自检。
public sealed class CostSummary
{
    public sealed record Group(Guid Host, string Partition, double Cost, double Hours, PriceUnit Unit);
    /// 本月最贵的作业；数组的各元素合成一行。
    public sealed record TopJob(Guid Host, string Id, string Name, string Partition, double Cost, int Elements);

    public double Today, Month, LastMonth;
    public List<Group> Groups = [];
    /// 本月每天的费用（从 1 号到今天）。
    public List<double> Days = [];
    public List<TopJob> Top = [];

    /// 每条记录按它实际运行的时间段（结束时间往前推运行时长）摊到各天 / 各月：跨过零点的任务两天各算一部分。
    /// 正在跑的任务由引擎当作"到上次刷新为止"的记录传进来（MonitorEngine.CostEntriesWithRunning），每次刷新都会变。
    public CostSummary(IEnumerable<CostEntry> entries, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var dayStart = t.Date;
        var monthStart = new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind);
        var lastStart = monthStart.AddMonths(-1);
        var limit = t.AddSeconds(60);
        var all = entries.ToList();
        // 这条记录落在 [a, b) 里的那部分费用占多少（0..1）。
        static double Share(CostEntry e, DateTime a, DateTime b)
        {
            var end = e.EndedAt;
            if (e.Seconds <= 0) return end >= a && end < b ? 1 : 0;
            var start = end.AddSeconds(-e.Seconds);
            var overlap = ((end < b ? end : b) - (start > a ? start : a)).TotalSeconds;
            return Math.Clamp(overlap / e.Seconds, 0, 1);
        }
        var thisMonth = all.Where(e => Share(e, monthStart, limit) > 0).ToList();
        Today = all.Sum(e => e.Cost * Share(e, dayStart, limit));
        Month = thisMonth.Sum(e => e.Cost * Share(e, monthStart, limit));
        LastMonth = all.Sum(e => e.Cost * Share(e, lastStart, monthStart));
        Groups = thisMonth.GroupBy(e => (e.Host, e.Partition ?? ""))
            .Select(g => new Group(g.Key.Host, g.Key.Item2, g.Sum(e => e.Cost * Share(e, monthStart, limit)),
                                   g.Sum(e => e.Amount * e.Seconds / 3600 * Share(e, monthStart, limit)), g.First().Unit))
            .OrderByDescending(g => g.Cost).ToList();
        var n = (dayStart - monthStart).Days + 1;
        Days = Enumerable.Range(0, Math.Max(n, 1)).Select(d =>
        {
            var a = monthStart.AddDays(d);
            var b = a.AddDays(1);
            return thisMonth.Sum(e => e.Cost * Share(e, a, b < limit ? b : limit));
        }).ToList();
        Top = thisMonth.GroupBy(e => (e.Host, Id: e.ArrayBase ?? e.Id))
            .Select(g => new TopJob(g.Key.Host, g.Key.Id, g.First().Name, g.First().Partition ?? "", g.Sum(e => e.Cost * Share(e, monthStart, limit)),
                                    g.Count(e => e.ArrayBase != null)))
            .OrderByDescending(j => j.Cost).ThenByDescending(j => j.Host.ToString("D").ToUpperInvariant() + "|" + j.Id, StringComparer.Ordinal)
            .Take(5).ToList();
    }
}
