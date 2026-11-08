using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

public sealed record SacctFinal(JobState State, string Raw, double? Elapsed, string Exit);

public static class Parsers
{
    // MARK: - CPU / 内存

    public sealed record CPUSample(double Busy, double Total);

    /// 解析 `head -1 /proc/stat; nproc; cat /proc/loadavg`。
    public static (CPUSample? Sample, int? Cores, double? Load1) Cpu(IEnumerable<string> lines)
    {
        CPUSample? sample = null;
        int? cores = null;
        double? load = null;
        foreach (var line in lines)
        {
            var parts = Text.Words(line);
            if (parts.Count > 0 && parts[0] == "cpu")
            {
                var nums = parts.Skip(1).Select(Text.Dbl).Where(v => v != null).Select(v => v!.Value).ToList();
                if (nums.Count >= 4)
                {
                    // user nice system idle iowait irq softirq steal
                    var idle = nums[3] + (nums.Count > 4 ? nums[4] : 0);
                    var total = nums.Take(8).Sum();
                    sample = new CPUSample(total - idle, total);
                }
            }
            else if (parts.Count == 1 && Text.Int(parts[0]) is { } n) cores = n;
            else if (parts.Count >= 3 && Text.Dbl(parts[0]) is { } l && parts.Count > 3 && parts[3].Contains('/')) load = l;
        }
        return (sample, cores, load);
    }

    public static (double Used, double Total)? Memory(IEnumerable<string> lines)
    {
        double? total = null, available = null;
        foreach (var line in lines)
        {
            var parts = line.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || Text.Dbl(parts[1]) is not { } kb) continue;
            if (parts[0] == "MemTotal") total = kb * 1024;
            if (parts[0] == "MemAvailable") available = kb * 1024;
        }
        if (total is not { } t || available is not { } a) return null;
        return (t - a, t);
    }

    // MARK: - GPU

    public static List<GPUStat> Gpus(IEnumerable<string> lines)
    {
        var list = new List<GPUStat>();
        foreach (var line in lines)
        {
            var f = line.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimWs()).ToList();
            if (f.Count < 5 || Text.Int(f[0]) is not { } idx) continue;
            list.Add(new GPUStat
            {
                Index = idx, Name = f[1],
                Utilization = Text.Dbl(f[2]) ?? 0,
                MemoryUsedMB = Text.Dbl(f[3]) ?? 0,
                MemoryTotalMB = Text.Dbl(f[4]) ?? 0,
                Temperature = f.Count > 5 ? Text.Dbl(f[5]) : null,
            });
        }
        return list;
    }

    /// pid -> 显存占用（MB）
    public static Dictionary<string, double> GpuApps(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, double>();
        foreach (var line in lines)
        {
            var f = line.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimWs()).ToList();
            if (f.Count < 2 || Text.Dbl(f[1]) is not { } mem) continue;
            map[f[0]] = map.GetValueOrDefault(f[0]) + mem;
        }
        return map;
    }

    // MARK: - PBS

    /// `qstat -f` 的一个作业：`    key = value`，续行以 Tab 开头。Variable_List 里的 PBS_O_WORKDIR 单独取出来。
    public static Dictionary<string, string> PbsKeyValues(IEnumerable<string> lines)
    {
        var kv = new Dictionary<string, string>();
        string? key = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('\t'))
            {
                if (key != null) kv[key] += line.TrimStart('\t');
                continue;
            }
            key = null;
            var t = line.TrimStart(' ');
            int eq = t.IndexOf(" = ", StringComparison.Ordinal);
            if (t.Length == line.Length || eq < 0) continue;
            var k = t[..eq];
            if (!kv.ContainsKey(k)) { kv[k] = t[(eq + 3)..]; key = k; }
        }
        if (kv.TryGetValue("Variable_List", out var vars) && Regex.Match(vars, "PBS_O_WORKDIR=[^,]*") is { Success: true } m)
            kv["PBS_O_WORKDIR"] = m.Value["PBS_O_WORKDIR=".Length..];
        return kv;
    }

    // MARK: - 目录过滤

    /// workdirs 段：`id|目录`，以及 `@@SI:HOME 家目录`。
    public static (string? Home, Dictionary<string, string> Dirs) Workdirs(IEnumerable<string> lines)
    {
        string? home = null;
        var dirs = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            if (line.StartsWith(RemoteScript.Marker + "HOME ", StringComparison.Ordinal)) { home = line[(RemoteScript.Marker.Length + 5)..]; continue; }
            int bar = line.IndexOf('|');
            if (bar < 0) continue;
            var id = line[..bar].Trim();
            var dir = line[(bar + 1)..];
            if (id.Length > 0 && dir.Length > 0) dirs[id] = dir;
        }
        return (home, dirs);
    }

    /// 目录是否在某个过滤目录里（含子目录）。`~` 按远端家目录展开；末尾的 / 忽略。
    public static bool PathMatches(string dir, IEnumerable<string> prefixes, string? home)
    {
        string Norm(string p)
        {
            var s = p.Trim();
            if (home != null && (s == "~" || s.StartsWith("~/", StringComparison.Ordinal))) s = home + s[1..];
            while (s.Length > 1 && s.EndsWith('/')) s = s[..^1];
            return s;
        }
        var d = Norm(dir);
        return prefixes.Any(raw =>
        {
            var p = Norm(raw);
            return p == "/" || d == p || d.StartsWith(p + "/", StringComparison.Ordinal);
        });
    }

    // MARK: - Slurm

    static readonly Regex ArrayIdRe = new(@"^(\d+)_(\d+|\[.+\])$");

    /// `%i|%j|%T|%M|%l|%D|%R|%P|%F|%K`（后两列是数组主编号和元素序号；老格式没有也能解析）。
    /// 作业名里可能含 `|`，所以除名称外的列从两端按位置取。
    public static List<RemoteJob> Squeue(IEnumerable<string> lines)
    {
        var jobs = new List<RemoteJob>();
        foreach (var line in lines)
        {
            var f = line.Split('|').Select(x => x.TrimWs()).ToList();
            if (f.Count < 3 || f[0].Length == 0) continue;
            // 新格式在名称后面还有 8 列，老格式 6 列；用倒数第二列是否为纯数字（%F）来判断。
            int tail = f.Count >= 10 && Text.IsAllDigits(f[^2]) ? 8 : f.Count >= 8 ? 6 : Math.Max(0, f.Count - 2);
            int nameEnd = f.Count - tail;
            int restStart = Math.Max(nameEnd, 2);
            var name = string.Join("|", f.GetRange(1, restStart - 1));
            var rest = f.GetRange(restStart, f.Count - restStart);
            string Col(int i) => i < rest.Count ? rest[i] : "";

            var id = f[0];
            var raw = Col(0);
            var job = new RemoteJob(id, name, JobStates.FromSlurm(raw), JobSource.Slurm) { RawState = raw };
            job.Elapsed = SlurmDuration(Col(1));
            job.Limit = SlurmDuration(Col(2));
            var detail = new List<string>();
            var reason = Col(4);
            if (reason.Length > 0 && reason != "(null)") detail.Add(reason);
            if (job.State == JobState.Pending && reason.StartsWith('(') && reason.Length >= 2)
                job.Reason = reason[1..^1];
            if (Col(5).Length > 0) detail.Add(Col(5));
            job.Detail = string.Join(" · ", detail);
            if (Col(5).Length > 0) job.Partition = Col(5);
            job.Nodes = Text.Int(Col(3));

            // 数组：优先用 %F/%K，否则从 "123_5" / "123_[7-99%10]" 里拆。
            if (tail == 8 && Col(7) != "N/A" && Col(7).Length > 0 && Col(6) != id)
            {
                job.ArrayJobID = Col(6);
                job.ArrayTask = Col(7);
            }
            else if (ArrayIdRe.IsMatch(id))
            {
                int us = id.IndexOf('_');
                job.ArrayJobID = id[..us];
                job.ArrayTask = id[(us + 1)..];
            }
            jobs.Add(job);
        }
        return jobs;
    }

    /// "[7-99%10]" -> 93；"[1,3,5-7]" -> 5；"[1-99:2]" -> 50；"5" -> 1
    public static int ArrayTaskCount(string spec)
    {
        var s = spec.Trim('[', ']', ' ');
        int pct = s.IndexOf('%');
        if (pct >= 0) s = s[..pct];
        int n = 0;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var stepParts = part.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (stepParts.Length == 0) continue;
            int step = stepParts.Length > 1 ? Math.Max(1, Text.Int(stepParts[1]) ?? 1) : 1;
            var range = stepParts[0].Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Text.Int).Where(v => v != null).Select(v => v!.Value).ToList();
            if (range.Count == 2 && range[1] >= range[0]) n += (range[1] - range[0]) / step + 1;
            else if (range.Count == 1) n += 1;
        }
        return Math.Max(n, 1);
    }

    /// 一行排队的数组记录代表几个元素。范围以编号里的 "123_[7-99%10]" 为准（总是带方括号）；
    /// %K 列在不同 Slurm 版本里可能不带方括号（"7-99%10"），以前只认带括号的，排队数被算成 1，进度条就偏满了。
    public static int PendingTaskCount(RemoteJob job)
    {
        int l = job.Id.IndexOf("_[", StringComparison.Ordinal);
        if (l >= 0 && job.Id.EndsWith(']')) return ArrayTaskCount(job.Id[(l + 2)..]);
        if (job.ArrayTask is not { } task || !task.Any(c => c is '-' or ',' or '[')) return 1;
        return ArrayTaskCount(task);
    }

    /// 把同一个数组的元素合并成一行。done / failed / total 由引擎补充。
    public static List<RemoteJob> GroupArrays(List<RemoteJob> jobs)
    {
        var outList = new List<RemoteJob>();
        var groups = new Dictionary<string, int>();
        foreach (var job in jobs)
        {
            if (job.Source != JobSource.Slurm || job.ArrayJobID is not { } bas) { outList.Add(job); continue; }
            if (!groups.TryGetValue(bas, out int i))
            {
                var g = new RemoteJob(bas, job.Name, JobState.Pending, JobSource.Slurm)
                {
                    ArrayJobID = bas,
                    Array = new ArrayInfo { BaseID = bas },
                    Limit = job.Limit,
                    Workdir = job.Workdir,
                    Partition = job.Partition,
                };
                outList.Add(g);
                i = outList.Count - 1;
                groups[bas] = i;
            }
            var grp = outList[i];
            if (job.State == JobState.Pending)
            {
                grp.Array!.Pending += PendingTaskCount(job);
                if (grp.Detail.Length == 0) grp.Detail = job.Detail;
                grp.Reason ??= job.Reason;
            }
            else
            {
                grp.Array!.Running.Add(job);
                grp.State = JobState.Running;
                grp.Elapsed = Math.Max(grp.Elapsed ?? 0, job.Elapsed ?? 0);
            }
        }
        foreach (var j in outList)
            j.Array?.Running.Sort((a, b) => (Text.Int(a.ArrayTask) ?? 0).CompareTo(Text.Int(b.ArrayTask) ?? 0));
        return outList;
    }

    /// sacct -P 输出：JobID|JobName|State|Elapsed|ExitCode
    public static Dictionary<string, SacctFinal> Sacct(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, SacctFinal>();
        foreach (var line in lines)
        {
            var f = line.Split('|');
            if (f.Length < 3) continue;
            var id = f[0].TrimWs();
            var raw = f[2].TrimWs();
            map[id] = new SacctFinal(JobStates.FromSlurm(raw), raw, f.Length > 3 ? SlurmDuration(f[3]) : null, f.Length > 4 ? f[4] : "");
        }
        return map;
    }

    /// 计费用的资源：`alloc` 段（`作业号|分到的资源|每节点申请|整个作业申请|节点数`，PBS 只有前两列）和 sacct / `si_pbs a` 的最后一列（AllocTRES）。认不出的行跳过。
    public static Dictionary<string, JobAlloc> Allocs(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, JobAlloc>();
        foreach (var line in lines)
        {
            var f = line.Split('|');
            if (f.Length < 2) continue;
            var id = f[0].TrimWs();
            string Col(int i) => i < f.Length ? f[i] : "";
            var nodes = Text.Int(new string(Col(4).TakeWhile(char.IsDigit).ToArray()));
            if (id.Length == 0 || JobAlloc.Parse(f[1], Col(2), Col(3), nodes) is not { } a) continue;
            map[id] = a;
        }
        return map;
    }

    public static Dictionary<string, JobAlloc> SacctAllocs(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, JobAlloc>();
        foreach (var line in lines)
        {
            var f = line.Split('|');
            if (f.Length < 6 || !f[^1].Contains('=') || JobAlloc.Parse(f[^1]) is not { } a) continue;
            map[f[0].TrimWs()] = a;
        }
        return map;
    }

    /// `qstat -Q` 的队列名（跳过表头和分隔线）。
    public static List<string> PbsQueues(IEnumerable<string> lines)
    {
        var list = new List<string>();
        foreach (var line in lines)
        {
            var first = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first == null || first == "Queue" || first.StartsWith('-') || first.StartsWith("qstat") || list.Contains(first)) continue;
            list.Add(first);
        }
        return list;
    }

    /// Slurm 时间格式：`[days-]hours:minutes:seconds`、`minutes:seconds`、`UNLIMITED` 等。
    public static double? SlurmDuration(string s)
    {
        var str = s.TrimWs();
        if (str.Length == 0 || !char.IsDigit(str[0])) return null;
        double days = 0;
        var rest = str;
        int dash = str.IndexOf('-');
        if (dash >= 0)
        {
            days = Text.Dbl(str[..dash]) ?? 0;
            rest = str[(dash + 1)..];
        }
        var parts = rest.Split(':', StringSplitOptions.RemoveEmptyEntries).Select(Text.Dbl).Where(v => v != null).Select(v => v!.Value).ToList();
        double secs = parts.Count switch
        {
            3 => parts[0] * 3600 + parts[1] * 60 + parts[2],
            2 => parts[0] * 60 + parts[1],
            1 => days > 0 ? parts[0] * 3600 : parts[0] * 60,   // "1-12" 表示 1 天 12 小时；单独数字为分钟
            _ => double.NaN,
        };
        if (double.IsNaN(secs)) return null;
        return days * 86400 + secs;
    }

    // MARK: - 直连进程

    public static List<RemoteJob> Processes(IEnumerable<string> lines, string filter, string exclude, IReadOnlyDictionary<string, double> gpuMem,
                                            IReadOnlySet<string>? alwaysInclude = null)
    {
        Regex? regex = null, excludeRegex = null;
        try { regex = new Regex(filter, RegexOptions.IgnoreCase); } catch { }
        if (exclude.Length > 0) { try { excludeRegex = new Regex(exclude, RegexOptions.IgnoreCase); } catch { } }
        var jobs = new List<RemoteJob>();
        foreach (var line in lines)
        {
            var parts = Text.SplitMax(line, ' ', 4);
            if (parts.Count != 5 || Text.Int(parts[0]) is not { } pidNum) continue;
            var pid = pidNum.ToString(CultureInfo.InvariantCulture);
            var args = parts[4];
            // 过滤掉我们自己的采集脚本和 ssh 会话。
            if (args.Contains("@@SI:") || args.StartsWith("ps ") || args.StartsWith("head ") || args.Contains("sshd")
                || args == "bash -l -s" || args.StartsWith("bash -l -s")) continue;
            // 系统 / 桌面进程（如 Ubuntu 的 update-manager、gnome-remote-desktop）不算任务。
            if (excludeRegex != null && excludeRegex.IsMatch(args)) continue;
            bool matchesFilter = regex == null || filter.Length == 0 || regex.IsMatch(args);
            bool onGPU = gpuMem.ContainsKey(pid);
            if (!(matchesFilter || onGPU || (alwaysInclude?.Contains(pid) ?? false))) continue;
            jobs.Add(new RemoteJob(pid, PrettyCommand(args), JobState.Running, JobSource.Process)
            {
                Elapsed = Text.Dbl(parts[1]),
                CpuPercent = Text.Dbl(parts[2]),
                MemoryBytes = Text.Dbl(parts[3]) * 1024,
                GpuMemoryMB = gpuMem.TryGetValue(pid, out var g) ? g : null,
                Detail = args,
            });
        }
        // 显存占用多、运行时间长的排前面；最多保留 12 个。
        return jobs.OrderByDescending(j => j.GpuMemoryMB ?? 0).ThenByDescending(j => j.Elapsed ?? 0).Take(12).ToList();
    }

    /// "/usr/bin/python3 -u train.py --lr 3e-4" -> "python3 train.py"
    public static string PrettyCommand(string args)
    {
        var tokens = Text.Words(args);
        if (tokens.Count == 0) return args;
        var exe = Text.LastPathComponent(tokens[0]);
        var script = tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-') && (t.Contains('.') || t.Contains('/')));
        if (script != null) return exe + " " + Text.LastPathComponent(script);
        if (tokens.Count > 2 && tokens[1] == "-m") return exe + " -m " + tokens[2];
        return exe;
    }

    // MARK: - 磁盘

    /// `df -P -k` 输出：Filesystem 1024-blocks Used Available Capacity Mounted-on
    public static List<DiskStat> Disks(IEnumerable<string> lines)
    {
        var list = new List<DiskStat>();
        foreach (var line in lines)
        {
            var f = Text.Words(line);
            if (f.Count < 6 || Text.Dbl(f[1]) is not { } total || Text.Dbl(f[2]) is not { } used || total <= 0) continue;
            var mount = string.Join(" ", f.Skip(5));
            if (list.Any(d => d.Mount == mount)) continue;
            list.Add(new DiskStat { Mount = mount, UsedKB = used, TotalKB = total });
        }
        return list;
    }

    // MARK: - 详情 / 历史

    static readonly Regex KeyRe = new(@"(?:^|\s)([A-Za-z_:/]+)=");

    /// `scontrol show job -o` 的单行 key=value。值里可能有空格（如 Command），按"下一个 key="切分。
    public static Dictionary<string, string> KeyValues(string line)
    {
        var map = new Dictionary<string, string>();
        var matches = KeyRe.Matches(line);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var key = m.Groups[1].Value;
            int start = m.Index + m.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            map[key] = line.Substring(start, Math.Max(0, end - start)).TrimWs();
        }
        return map;
    }

    /// sacct -P：JobID|JobName|State|Elapsed|End|ExitCode
    public static List<HistoryEntry> History(IEnumerable<string> lines)
    {
        var entries = new List<HistoryEntry>();
        foreach (var line in lines)
        {
            var f = line.Split('|');
            if (f.Length < 5 || f[0].Length == 0) continue;
            var state = JobStates.FromSlurm(f[2]);
            if (state.IsActive()) continue;   // 还在跑的不算历史
            var ended = DateTime.TryParseExact(f[4], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : DateTime.Now;
            var dir = f.Length > 6 ? string.Join("|", f[6..]) : "";
            entries.Add(new HistoryEntry { Id = f[0], Name = f[1], State = f[2], Elapsed = SlurmDuration(f[3]), EndedAt = ended, ExitCode = f.Length > 5 ? f[5] : null,
                                           Dir = dir.Length > 0 ? dir : null });
        }
        entries = entries.OrderByDescending(e => e.EndedAt).ToList();

        // 数组元素合并成一条，备注里记 "数组 97✓ 3✗"。
        var outList = new List<HistoryEntry>();
        var counts = new Dictionary<string, (int Ok, int Bad)>();
        foreach (var e in entries)
        {
            int us = e.Id.IndexOf('_');
            if (us < 0) { outList.Add(e); continue; }
            var bas = e.Id[..us];
            bool failed = e.JobState is JobState.Failed or JobState.Timeout;
            var c = counts.GetValueOrDefault(bas);
            if (failed) c.Bad++; else c.Ok++;
            counts[bas] = c;
            var note = L("数组 ", "array ") + $"{c.Ok}✓ {c.Bad}✗";
            var existing = outList.FirstOrDefault(x => x.Id == bas);
            if (existing != null)
            {
                if (failed) existing.State = e.State;
                existing.Elapsed = Math.Max(existing.Elapsed ?? 0, e.Elapsed ?? 0);
                existing.Note = note;
            }
            else
            {
                outList.Add(new HistoryEntry { Id = bas, Name = e.Name, State = e.State, Elapsed = e.Elapsed, EndedAt = e.EndedAt, Note = note, Dir = e.Dir });
            }
        }
        return outList;
    }

    // MARK: - 进度文件

    public sealed record ProgressEntry(string Id, string Name, double? Progress, string? Message, string? JobID, DateTime? Updated, bool Done, bool Failed);

    /// 进度文件格式（字段均可选）：
    /// {"name": "train", "progress": 0.42, "message": "epoch 3/10", "jobid": "12345",
    ///  "current": 42, "total": 100, "updated": 1730000000, "status": "running|done|failed"}
    public static List<ProgressEntry> Progress(IEnumerable<string> lines)
    {
        var entries = new List<ProgressEntry>();
        string? path = null;
        var buffer = new List<string>();

        void Flush()
        {
            var json = string.Join("\n", buffer);
            buffer.Clear();
            if (path == null) return;
            JsonElement obj;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                obj = doc.RootElement.Clone();
            }
            catch { return; }
            var fileName = Text.DeletingPathExtension(Text.LastPathComponent(path));
            double? p = null;
            if (Num(obj, "progress") is { } v) p = v > 1 ? v / 100 : v;
            else if (Num(obj, "current") is { } cur && Num(obj, "total") is { } tot && tot > 0) p = cur / tot;
            var status = Str(obj, "status")?.ToLowerInvariant() ?? "";
            DateTime? updated = Num(obj, "updated") is { } u ? DateTimeOffset.FromUnixTimeMilliseconds((long)(u * 1000)).LocalDateTime : null;
            var jobID = Str(obj, "jobid") ?? (Num(obj, "jobid") is { } jn ? ((long)jn).ToString(CultureInfo.InvariantCulture) : null);
            entries.Add(new ProgressEntry(fileName, Str(obj, "name") ?? fileName, p, Str(obj, "message"), jobID, updated,
                status is "done" or "completed" or "finished", status is "failed" or "error"));
        }

        foreach (var line in lines)
        {
            if (line.StartsWith(RemoteScript.Marker + "FILE ", StringComparison.Ordinal))
            {
                Flush();
                path = line[(RemoteScript.Marker + "FILE ").Length..];
            }
            else buffer.Add(line);
        }
        Flush();
        return entries;
    }

    static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String) return Text.Dbl(v.GetString());
        return null;
    }
}
