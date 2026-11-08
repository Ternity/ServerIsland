using System.Text.Json;

namespace ServerIsland.Core;

/// 本机记录的已结束任务（保留 7 天，每台服务器最多 500 条），存在 history.json，重启后还在。
/// 历史页把它和 sacct 查到的最近 24 小时合并显示；本机记录多了工作目录，可以直接在终端打开。
///
/// 另外在 active-jobs.json 里记住每台服务器上次看到的运行 / 排队任务：ServerIsland 退出期间结束的任务，
/// 下次启动、第一次连上服务器时补记到历史里（不弹提醒）。
public sealed class HistoryStore
{
    /// 上次看到还在运行 / 排队的任务。
    public sealed class ActiveJob
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Slurm { get; set; }
        public string? Dir { get; set; }
        public double? Elapsed { get; set; }
        public DateTime SeenAt { get; set; }
        /// 数组已知的元素总数，用于重启后检查 sacct 是否返回了完整结果；旧记录没有这个字段。
        public int? ArrayTotal { get; set; }
        /// 计费用：分区、节点数和分到的资源（ServerIsland 退出期间结束的作业也能补记费用）。
        public string? Partition { get; set; }
        public int? Nodes { get; set; }
        public JobAlloc? Alloc { get; set; }
    }

    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public const int MaxPerHost = 500;

    readonly Dictionary<Guid, List<HistoryEntry>> _entries = [];
    readonly Dictionary<Guid, List<ActiveJob>> _active = [];
    readonly string _path, _activePath;
    /// false：演示模式、自检、--diagnose 等临时引擎，只在内存里记，不碰真实的文件。
    readonly bool _persist;
    readonly object _lock = new();

    public HistoryStore(bool persist = true, string? directory = null)
    {
        _persist = persist;
        var dir = directory ?? AppPaths.ConfigDirectory;
        _path = Path.Combine(dir, "history.json");
        _activePath = Path.Combine(dir, "active-jobs.json");
        if (!persist) return;
        try
        {
            if (File.Exists(_path))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, List<HistoryEntry>>>(File.ReadAllText(_path), Json.Options) ?? [])
                    if (Guid.TryParse(k, out var id)) _entries[id] = v;
        }
        catch { }
        try
        {
            if (File.Exists(_activePath))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, List<ActiveJob>>>(File.ReadAllText(_activePath), Json.Options) ?? [])
                    if (Guid.TryParse(k, out var id)) _active[id] = v;
        }
        catch { }
        Prune();
    }

    public void Record(Guid host, HistoryEntry entry)
    {
        lock (_lock)
        {
            var list = _entries.GetValueOrDefault(host) ?? [];
            // 同一个编号再记一次时（比如数组先记元素、后记整体）保留已知的目录
            entry.Dir ??= list.FirstOrDefault(e => e.Id == entry.Id)?.Dir;
            list.RemoveAll(e => e.Id == entry.Id);
            list.Insert(0, entry);
            _entries[host] = list.Take(MaxPerHost).ToList();
            Prune();
            Save();
        }
    }

    public List<HistoryEntry> Entries(Guid host)
    {
        var cutoff = DateTime.Now - Retention;
        lock (_lock) return (_entries.GetValueOrDefault(host) ?? []).Where(e => e.EndedAt > cutoff).ToList();
    }

    /// 设置里的"清除历史记录"：删掉全部已结束任务的记录（上次看到的运行中任务照常保留，用来补记退出期间结束的任务）。
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Save();
        }
    }

    public int Count { get { lock (_lock) return _entries.Values.Sum(l => l.Count); } }

    /// 上次记住的运行 / 排队任务。
    public List<ActiveJob> ActiveJobs(Guid host)
    {
        lock (_lock) return [.. _active.GetValueOrDefault(host) ?? []];
    }

    /// 每次成功采样后调用；任务列表（编号、目录）没变时不写文件。
    public void SetActive(Guid host, IEnumerable<RemoteJob> jobs)
    {
        var now = DateTime.Now;
        var list = jobs.Where(j => j.State.IsActive() && j.Source != JobSource.ProgressFile)
            .Select(j => new ActiveJob { Id = j.Id, Name = j.Name, Slurm = j.Source == JobSource.Slurm, Dir = j.Workdir ?? j.App?.Dir,
                                         Elapsed = j.Elapsed, SeenAt = now, ArrayTotal = j.Array?.Total,
                                         Partition = j.Partition, Nodes = j.Nodes, Alloc = j.Alloc })
            .ToList();
        lock (_lock)
        {
            var old = _active.GetValueOrDefault(host) ?? [];
            bool changed = !old.Select(a => $"{a.Id}|{a.Dir}|{a.ArrayTotal ?? 0}|{a.Alloc != null}")
                .SequenceEqual(list.Select(a => $"{a.Id}|{a.Dir}|{a.ArrayTotal ?? 0}|{a.Alloc != null}"));
            // 列表没变时也隔 10 分钟更新一次时间，"大约什么时候结束"不会差太多
            if (!changed && !(old.Count > 0 && (now - old[0].SeenAt).TotalSeconds > 600)) return;
            _active[host] = list;
            SaveActive();
        }
    }

    /// 历史页：sacct 的最近 24 小时为准，补上本机记录的目录和备注，再加上更早的本机记录。
    public static List<HistoryEntry> Merge(List<HistoryEntry> server, List<HistoryEntry> local)
    {
        var byId = new Dictionary<string, HistoryEntry>();
        foreach (var e in local) byId.TryAdd(e.Id, e);
        var ids = server.Select(e => e.Id).ToHashSet();
        foreach (var e in server)
        {
            e.Dir ??= byId.GetValueOrDefault(e.Id)?.Dir;
            e.Note ??= byId.GetValueOrDefault(e.Id)?.Note;
        }
        return server.Concat(local.Where(e => !ids.Contains(e.Id))).OrderByDescending(e => e.EndedAt).ToList();
    }

    void Prune()
    {
        var cutoff = DateTime.Now - Retention;
        foreach (var k in _entries.Keys.ToList()) _entries[k] = _entries[k].Where(e => e.EndedAt > cutoff).ToList();
    }

    void Save()
    {
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var dict = _entries.ToDictionary(kv => kv.Key.ToString("D").ToUpperInvariant(), kv => kv.Value);
            File.WriteAllText(_path, JsonSerializer.Serialize(dict, Json.Options));
        }
        catch { }
    }

    void SaveActive()
    {
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_activePath)!);
            var dict = _active.ToDictionary(kv => kv.Key.ToString("D").ToUpperInvariant(), kv => kv.Value);
            File.WriteAllText(_activePath, JsonSerializer.Serialize(dict, Json.Options));
        }
        catch { }
    }
}

/// 轻量诊断日志（超过 512KB 自动轮转）。记录轮询结果、任务消失、提醒是否发出以及原因，方便排查"为什么没提醒"。
/// 轻量诊断日志（超过 512KB 轮转成 serverisland.1.log）。超过保留天数（设置 → 通用，默认 7 天）的行自动删除；也可以一键清空。
public static class DebugLog
{
    static readonly object Lock = new();
    public static bool Enabled = true;
    public static string Path => System.IO.Path.Combine(AppPaths.LogDirectory, "serverisland.log");
    public static string RotatedPath => System.IO.Path.ChangeExtension(Path, ".1.log");
    const string Format = "yyyy-MM-dd HH:mm:ss";

    public static void Log(string message)
    {
        if (!Enabled) return;
        var line = $"{DateTime.Now.ToString(Format, System.Globalization.CultureInfo.InvariantCulture)}  {message}\n";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    var info = new FileInfo(Path);
                    if (info.Exists && info.Length > 512_000)
                    {
                        File.Delete(RotatedPath);
                        File.Move(Path, RotatedPath);
                    }
                    File.AppendAllText(Path, line);
                }
                catch { }
            }
        });
    }

    /// 删除早于 days 天的行（0 表示永久保留）。
    public static void Prune(int days)
    {
        if (days <= 0) return;
        var now = DateTime.Now;
        var cutoff = now.AddDays(-days);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (Lock)
            {
                foreach (var file in new[] { RotatedPath, Path })
                {
                    try
                    {
                        if (!File.Exists(file)) continue;
                        var text = File.ReadAllText(file);
                        var kept = PruneText(text, cutoff, now);
                        if (kept == text) continue;
                        if (kept.Length == 0) File.Delete(file); else File.WriteAllText(file, kept);
                    }
                    catch { }
                }
            }
        });
    }

    /// 只保留 cutoff 之后的行；没有时间戳的行（多行消息的后续行）跟随上一行。
    /// 旧版本的时间戳没有年份（"MM-dd HH:mm:ss"），按今年算，晚于现在就算去年。
    public static string PruneText(string text, DateTime cutoff, DateTime now)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var style = System.Globalization.DateTimeStyles.None;
        bool keep = true;
        var outLines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length >= 19 && DateTime.TryParseExact(line[..19], Format, inv, style, out var d)) keep = d >= cutoff;
            else if (line.Length >= 16 && line[14..16] == "  " && DateTime.TryParseExact($"{now.Year}-{line[..14]}", Format, inv, style, out var o))
            {
                if (o > now.AddDays(1)) o = o.AddYears(-1);
                keep = o >= cutoff;
            }
            if (keep) outLines.Add(line);
        }
        var s = string.Join("\n", outLines);
        return s.Trim().Length == 0 ? "" : s;
    }

    /// 清空全部诊断日志。
    public static void Clear()
    {
        lock (Lock)
        {
            try { File.Delete(RotatedPath); } catch { }
            try { File.Delete(Path); } catch { }
        }
    }

    /// 两个日志文件加起来的大小（字节）。
    public static long Size => new[] { Path, RotatedPath }.Select(f => File.Exists(f) ? new FileInfo(f).Length : 0).Sum();
}
