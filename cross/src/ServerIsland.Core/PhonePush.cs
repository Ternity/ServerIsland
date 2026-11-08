using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerIsland.Core.Ssh;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// 手机推送：任务结束、失败等提醒发到企业微信群机器人（Webhook），手机上的企业微信 / 微信就能收到。和 Mac 版 PhonePush.swift 对应。
///
/// - 只在电脑上做：不在手机上连服务器，手机不耗电；电脑睡眠时不推送。
/// - 过滤：按"推送哪些任务"（默认只推送关注的任务）、事件种类、勿扰时段；静音的服务器不推送。
/// - 先攒 10 秒：1～2 条分别发，更多合并成一条；每分钟最多 15 条（企业微信的上限是 20）。
/// - key 只存凭据库；日志里打码。推送失败不影响查询，也不弹窗。
public sealed class PhonePush
{
    public const string CredentialAccount = "wecom-webhook";
    public const string Endpoint = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=";
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    public const int PerMinute = 15;
    /// 企业微信文本消息上限 2048 字节，留一点余量。
    public const int MaxBytes = 2000;

    public sealed record Item(string Text, string Line, Guid? HostID, bool Failure);
    public sealed record PushStatus(DateTime Date, bool Ok, string Message);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    readonly ConfigStore _store;
    readonly bool _demo;
    string? _key;
    bool _keyLoaded;
    readonly List<Item> _pending = [];
    bool _flushScheduled;
    readonly List<DateTime> _sentAt = [];

    /// 状态变了（发送结果、key 被拒绝）：设置页刷新。
    public event Action? Changed;
    public PushStatus? Status { get; private set; }
    /// key 被企业微信拒绝（93000）：暂停推送，直到改 key 或测试成功。
    public bool KeyRejected { get; private set; }
    public bool HasKey { get; private set; }

    /// 演示模式默认不推送；`SERVERISLAND_PUSH_DEMO=1` 时用凭据库里的 key 推送演示事件（开发时试效果）。
    public static bool DemoPush => Environment.GetEnvironmentVariable("SERVERISLAND_PUSH_DEMO") == "1";

    public PhonePush(ConfigStore store, bool demo)
    {
        _store = store;
        _demo = demo;
        HasKey = (!demo || DemoPush) && store.Persist && Key != null;
    }

    PhonePushSettings Settings => _store.Settings.PhonePush;

    string? Key
    {
        get
        {
            if (!_keyLoaded) { _key = CredentialStore.GetSecret(CredentialAccount); _keyLoaded = true; }
            return _key;
        }
    }

    // —— key ——

    /// 从粘贴的内容里取出 key：可以是整个 Webhook 地址，也可以只是 key。认不出时返回 null。
    public static string? ExtractKey(string input)
    {
        var s = input.Trim();
        var i = s.IndexOf("key=", StringComparison.Ordinal);
        if (i >= 0)
        {
            s = s[(i + 4)..];
            var amp = s.IndexOf('&');
            if (amp >= 0) s = s[..amp];
        }
        s = s.Trim();
        if (s.Length < 16 || s.Length > 64 || !s.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '-'))) return null;
        return s;
    }

    /// 日志、界面上只显示 key 的最后 4 位。
    public static string Mask(string key) => "****" + (key.Length > 4 ? key[^4..] : key);

    public string? MaskedKey => HasKey && Key is { } k ? Mask(k) : null;

    public bool SetKey(string input)
    {
        if (ExtractKey(input) is not { } k || !CredentialStore.SaveSecret(CredentialAccount, k, L("企业微信推送", "WeCom push"))) return false;
        _key = k; _keyLoaded = true;
        HasKey = true;
        KeyRejected = false;
        Status = null;
        DebugLog.Log("手机推送：已保存 key " + Mask(k));
        Changed?.Invoke();
        return true;
    }

    public void RemoveKey()
    {
        CredentialStore.DeleteSecret(CredentialAccount);
        _key = null; _keyLoaded = true;
        HasKey = false;
        KeyRejected = false;
        Status = null;
        _pending.Clear();
        DebugLog.Log("手机推送：已删除 key");
        Changed?.Invoke();
    }

    /// 预览图用：假装已经保存了 key、发过一条消息。
    public void PreviewState(string? key, PushStatus? status)
    {
        _key = key; _keyLoaded = true;
        HasKey = key != null;
        Status = status;
    }

    // —— 收集事件 ——

    /// 由 MonitorEngine.Flush 调用，传入这一轮的全部任务事件（还没按刘海的提醒设置过滤）。
    public void Consider(IEnumerable<IslandEvent> batch, HostConfig host, HashSet<string>? watchedPids = null)
    {
        if (!Settings.Enabled || !HasKey || KeyRejected || (_demo && !DemoPush)) return;
        if (host.Muted) return;
        var all = _store.Settings;
        var quiet = Settings.FollowQuietHours && all.IsQuiet();
        foreach (var e in batch)
        {
            if (!Wants(e, all, watchedPids)) continue;
            if (quiet && !(Settings.FailuresDuringQuiet && e.Kind == EventKind.Failed))
            {
                DebugLog.Log("手机推送：勿扰时段跳过 " + e.Title);
                continue;
            }
            Enqueue(ItemFor(e, host, Settings.Detailed));
        }
    }

    /// 这条事件要不要推送（不含静音、勿扰）。
    public static bool Wants(IslandEvent e, AppSettings all, HashSet<string>? watchedPids = null)
    {
        var s = all.PhonePush;
        if (e.JobID is not { } job || e.HostID is not { } host)
            // 服务器级提醒：磁盘快满等。
            return e.Kind == EventKind.Warning && s.ServerWarnings;
        var on = e.Kind switch
        {
            EventKind.Started => s.Started,
            EventKind.Finished => s.Finished,
            EventKind.Failed => s.Failed,
            EventKind.Info => s.Unknown,
            _ => true,
        };
        if (!on) return false;
        // 手动监听的进程是用户点名要看的：任何范围都推送。
        if (watchedPids?.Contains(job) == true) return true;
        return s.Scope switch
        {
            PushScope.All => true,
            PushScope.Starred => all.IsStarred(host, job),
            // 刘海的种类开关和关注范围也要满足
            _ => all.AllowsAlert(e),
        };
    }

    /// Agent 会话的提醒（"需要确认 / 回答完了 / 出错"时由 DisplayLink 调用）。
    public void Agent(AgentSession s)
    {
        if (!Settings.WantsAgents || !HasKey || KeyRejected || _demo) return;
        var attention = s.State == AgentState.Attention;
        if (attention ? !Settings.AgentAttention : !Settings.AgentDone) return;
        if (Settings.FollowQuietHours && _store.Settings.IsQuiet()) return;
        Enqueue(AgentItem(s, Settings.Detailed));
    }

    void Enqueue(Item item)
    {
        _pending.Add(item);
        DebugLog.Log("手机推送：待发送 " + item.Line);
        if (_flushScheduled) return;
        _flushScheduled = true;
        _ = FlushLater(Window);
    }

    async Task FlushLater(TimeSpan delay)
    {
        await Task.Delay(delay);
        _flushScheduled = false;
        await Flush();
    }

    async Task Flush()
    {
        if (_pending.Count == 0) return;
        // 限速：这一分钟发满了，就等下一分钟合并发送。
        var now = DateTime.Now;
        _sentAt.RemoveAll(d => (now - d).TotalSeconds > 60);
        var room = PerMinute - _sentAt.Count;
        if (room <= 0)
        {
            DebugLog.Log($"手机推送：一分钟内已发 {_sentAt.Count} 条，稍后合并发送");
            if (!_flushScheduled) { _flushScheduled = true; _ = FlushLater(TimeSpan.FromSeconds(30)); }
            return;
        }
        var items = _pending.ToList();
        _pending.Clear();
        var messages = items.Count <= 2 && room >= items.Count ? items.Select(i => i.Text).ToList() : [Merged(items, _store.Settings.Hosts)];
        foreach (var m in messages) await Send(m, retry: true);
    }

    // —— 发送 ——

    /// 设置页的"发送测试消息"。
    public Task SendTest() => Send(TestMessage(), retry: false, test: true);

    async Task Send(string text, bool retry, bool test = false)
    {
        if (Key is not { } key) return;
        _sentAt.Add(DateTime.Now);
        var content = Truncate(text);
        var title = content.Split('\n')[0];
        var body = new JsonObject { ["msgtype"] = "text", ["text"] = new JsonObject { ["content"] = content } };
        try
        {
            using var resp = await Http.PostAsync(Endpoint + key, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            int code = -1;
            string msg = raw.Length > 200 ? raw[..200] : raw;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("errcode", out var c) && c.TryGetInt32(out var ci)) code = ci;
                if (doc.RootElement.TryGetProperty("errmsg", out var m)) msg = m.GetString() ?? msg;
            }
            catch { }
            if (code == 0)
            {
                KeyRejected = false;
                Status = new PushStatus(DateTime.Now, true, test ? L("测试消息已发送", "Test message sent") : title);
                DebugLog.Log("手机推送：已发送 " + title);
                Changed?.Invoke();
                return;
            }
            Status = new PushStatus(DateTime.Now, false, Explain(code, msg));
            DebugLog.Log($"手机推送失败（{code}）：{Scrub(msg, key)} · {title}");
            if (code is 93000 or 40001 or 40014) KeyRejected = true;
            Changed?.Invoke();
            if (code == 45009 && retry)
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                await Send(text, retry: false);
            }
        }
        catch (Exception e)
        {
            var m = Scrub(e.Message, key);
            Status = new PushStatus(DateTime.Now, false, L("网络错误：", "Network error: ") + m);
            DebugLog.Log($"手机推送失败（网络）：{m} · {title}" + (retry ? "，30 秒后重试" : ""));
            Changed?.Invoke();
            if (retry)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                await Send(text, retry: false);
            }
        }
    }

    public static string Explain(int code, string message) => code switch
    {
        93000 or 40001 or 40014 => L("key 无效：请重新复制机器人的 Webhook 地址", "Invalid key: copy the bot's webhook URL again"),
        45009 => L("发送太频繁，已被企业微信限流", "Rate-limited by WeCom"),
        40058 or 44004 => L($"消息内容有误（{code}）", $"Bad message ({code})"),
        _ => $"{code} {message}",
    };

    public static string Scrub(string s, string key) => key.Length == 0 ? s : s.Replace(key, Mask(key));

    /// 按 UTF-8 字节截断（不切开一个字）。
    public static string Truncate(string s, int maxBytes = MaxBytes)
    {
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        var sb = new StringBuilder();
        int bytes = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            var n = Encoding.UTF8.GetByteCount(el);
            if (bytes + n > maxBytes - 4) break;
            sb.Append(el);
            bytes += n;
        }
        return sb + "…";
    }

    // —— 消息格式（纯文本：微信插件里也能显示）——

    public static string Emoji(EventKind kind, bool hasFailures = false) => kind switch
    {
        EventKind.Finished => hasFailures ? "⚠️" : "✅",
        EventKind.Failed => "❌",
        EventKind.Started => "▶️",
        EventKind.Info => "❔",
        _ => "⚠️",
    };

    public static string HostLine(HostConfig host)
    {
        var kind = host.Kind switch { HostKind.Slurm => "Slurm", HostKind.Pbs => "PBS", _ => L("直连", "direct") };
        return L("服务器：", "Server: ") + host.DisplayName + L($"（{kind}）", $" ({kind})");
    }

    /// 目录只显示最后两层，避免路径里带出用户名：home / Users 后面那一层（用户名）和它之前的都去掉，主目录本身显示成 ~。
    public static string ShortDir(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool underHome = false;
        var i = parts.FindLastIndex(p => p == "home" || p == "Users");
        if (i >= 0 && i + 1 < parts.Count)
        {
            parts.RemoveRange(0, i + 2);
            underHome = true;
        }
        if (parts.Count == 0) return underHome ? "~" : path;
        if (parts.Count <= 2) return (underHome ? "~/" : "/") + string.Join('/', parts);
        return "…/" + string.Join('/', parts.TakeLast(2));
    }

    /// 时长：3 小时 12 分 / 42 分 / 26 小时 / 2 天 2 小时（两天以内用小时，时间上限常写成 24 / 48 小时）。
    public static string Duration(double t)
    {
        var s = Math.Max(0, (long)Math.Round(t));
        long d = s / 86400, dh = s % 86400 / 3600, m = s % 3600 / 60;
        if (d >= 2) return dh > 0 ? L($"{d} 天 {dh} 小时", $"{d}d {dh}h") : L($"{d} 天", $"{d}d");
        var h = s / 3600;
        if (h > 0) return m > 0 ? L($"{h} 小时 {m} 分", $"{h}h {m}m") : L($"{h} 小时", $"{h}h");
        if (m > 0) return L($"{m} 分", $"{m}m");
        return L($"{s} 秒", $"{s}s");
    }

    /// 钟点：今天只写 14:32，昨天写"昨天 14:32"，更早写日期。
    public static string Clock(DateTime d, DateTime now)
    {
        var t = d.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (d.Date == now.Date) return t;
        if (d.Date == now.Date.AddDays(-1)) return L("昨天 ", "yesterday ") + t;
        return d.ToString("M-d HH:mm", CultureInfo.InvariantCulture);
    }

    public static string? Resources(PushInfo p)
    {
        var parts = new List<string>();
        if (p.Nodes is { } n && n > 0) parts.Add(L($"{n} 节点", $"{n} node" + (n == 1 ? "" : "s")));
        if (p.Cpus is { } c && c > 0) parts.Add(L($"{c} 核", $"{c} CPU" + (c == 1 ? "" : "s")));
        if (p.Gpus is { } g && g > 0) parts.Add($"{g} GPU");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// 一条事件的完整消息。
    public static Item ItemFor(IslandEvent e, HostConfig host, bool detailed, DateTime? now = null)
    {
        var p = e.Push;
        var failures = p?.FailedElements.Count > 0;
        var head = Emoji(e.Kind, failures) + " " + e.Title;
        var lines = new List<string> { head, HostLine(host) };
        if (detailed) lines.AddRange(Body(e, now ?? e.Date));
        // 合并消息里的一行：结果 + 名字 + 一两个关键数字
        var shortParts = new List<string> { head };
        if (p != null)
        {
            if (e.Kind == EventKind.Failed && p.State is { } st && !e.Title.Contains(st)) shortParts.Add(st);
            if (p.Elapsed is { } el && e.Kind != EventKind.Started) shortParts.Add(Duration(el));
            if (p.Array is { } a) shortParts.Add(a.Split(" / ")[0]);
            if (p.Cost is { } c) shortParts.Add(c);
        }
        else if (e.Subtitle.Length > 0) shortParts.Add(e.Subtitle);
        return new Item(string.Join('\n', lines), string.Join(" · ", shortParts), host.Id, e.Kind == EventKind.Failed);
    }

    public static List<string> Body(IslandEvent e, DateTime now)
    {
        if (e.Push is not { } p) return e.Subtitle.Length == 0 ? [] : [e.Subtitle];
        var lines = new List<string>();
        if (p.Id is { } id)
        {
            var l = p.Source switch
            {
                JobSource.Process => L("进程：", "Process: ") + id + (p.Watched ? L("（手动监听）", " (watched)") : ""),
                JobSource.ProgressFile => L("进度条目：", "Progress entry: ") + id,
                _ => L("作业号：", "Job ID: ") + id,
            };
            if (!string.IsNullOrEmpty(p.Partition)) l += L(" · 分区 ", " · partition ") + p.Partition;
            lines.Add(l);
        }
        if (Resources(p) is { } r) lines.Add(L("资源：", "Resources: ") + r);
        if (p.NodeList is { } nl) lines.Add(L("节点：", "Nodes: ") + nl);
        // 失败原因
        if (e.Kind is EventKind.Failed or EventKind.Info && (p.State != null || p.ExitCode != null))
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(p.State)) parts.Add(p.State);
            if (!string.IsNullOrEmpty(p.ExitCode) && p.ExitCode != "0:0") parts.Add(L("退出码 ", "exit code ") + p.ExitCode);
            if (parts.Count > 0) lines.Add(L("状态：", "State: ") + string.Join(" · ", parts));
        }
        // 用时、上限、起止时间
        if (e.Kind == EventKind.Started)
        {
            if (p.Limit is { } lim) lines.Add(L("时间上限：", "Time limit: ") + Duration(lim));
        }
        else if (p.Elapsed is { } el)
        {
            var l = (p.ElapsedAtLeast ? L("运行了至少 ", "Ran at least ") : L("用时：", "Run time: ")) + Duration(el);
            if (p.Limit is { } lim && lim > 0)
            {
                if (el >= lim * 0.995) l += L("（已到时间上限）", " (hit the time limit)");
                else
                {
                    var pct = (int)Math.Round(el / lim * 100);
                    l += L($"（上限 {Duration(lim)}，用了 {pct}%）", $" (limit {Duration(lim)}, {pct}% used)");
                }
            }
            lines.Add(l);
            if (!p.ElapsedAtLeast)
                lines.Add(L("时间：", "Time: ") + Clock(now.AddSeconds(-el), now) + L(" 开始 → ", " start → ") + Clock(now, now) + L(" 结束", " end"));
        }
        if (p.Array is { } a) lines.Add(L("结果：", "Result: ") + a);
        if (p.FailedElements.Count > 0)
        {
            var shown = string.Join(L("、", ", "), p.FailedElements.Take(10));
            var more = p.FailedElements.Count > 10 ? L($" 等 {p.FailedElements.Count} 个", $" and {p.FailedElements.Count - 10} more") : "";
            lines.Add(L("失败的元素：", "Failed elements: ") + shown + more);
        }
        if (p.App is { } app) lines.Add(app);
        if (p.AppNote is { } an) lines.Add(an);
        if (p.Sample is { } sm) lines.Add(L("最后一次采样：", "Last sample: ") + sm);
        if (!string.IsNullOrEmpty(p.Note)) lines.Add(p.Note);
        if (!string.IsNullOrEmpty(p.Dir)) lines.Add(L("目录：", "Directory: ") + ShortDir(p.Dir));
        if (p.Cost is { } cost) lines.Add(L("费用：约 ", "Cost: about ") + cost);
        return lines;
    }

    public static Item AgentItem(AgentSession s, bool detailed)
    {
        var name = s.Tool switch { "claude" => "Claude Code", "codex" => "Codex", "opencode" => "OpenCode", _ => s.Tool };
        var (head, detail) = s.State switch
        {
            AgentState.Attention => ("🙋 " + L($"{name} 需要你确认", $"{name} needs you"), s.Message),
            AgentState.Error => ("❌ " + L($"{name} 出错了", $"{name} hit an error"), s.Title),
            _ => ("💬 " + L($"{name} 回答完了", $"{name} is done"), s.Title),
        };
        var project = s.Cwd.Length == 0 ? "" : Text.LastPathComponent(s.Cwd);
        var lines = new List<string> { head };
        if (project.Length > 0) lines.Add(L("项目：", "Project: ") + project);
        if (detailed && detail.Length > 0) lines.Add(detail.Length > 300 ? detail[..300] : detail);
        return new Item(string.Join('\n', lines), head + (project.Length > 0 ? " · " + project : ""), null, s.State == AgentState.Error);
    }

    /// 合并成一条：同一台服务器时标题写服务器名，每行省略服务器；多台时每行带上服务器名。
    public static string Merged(List<Item> items, List<HostConfig> hosts)
    {
        string? Name(Guid? id) => id is { } i ? hosts.FirstOrDefault(h => h.Id == i)?.DisplayName : null;
        var ids = items.Select(i => i.HostID).Distinct().ToList();
        List<string> lines;
        if (ids.Count == 1 && Name(items[0].HostID) is { } n)
        {
            lines = ["📦 " + L($"{n}：{items.Count} 条提醒", $"{n}: {items.Count} updates")];
            lines.AddRange(items.Take(12).Select(i => i.Line));
        }
        else
        {
            lines = ["📦 " + L($"ServerIsland：{items.Count} 条提醒", $"ServerIsland: {items.Count} updates")];
            lines.AddRange(items.Take(12).Select(i => i.Line + (Name(i.HostID) is { } hn ? " · " + hn : "")));
        }
        if (items.Count > 12) lines.Add(L($"……还有 {items.Count - 12} 条", $"…and {items.Count - 12} more"));
        return string.Join('\n', lines);
    }

    public static string TestMessage() =>
        L("✅ ServerIsland 测试消息\n能看到这条，说明手机推送已经设置好了。\n之后任务结束、失败时，这里会收到提醒。",
          "✅ ServerIsland test message\nIf you can read this, phone push is set up.\nYou'll get alerts here when jobs finish or fail.");

    // —— 示例（--push-preview 和自检用；和 Mac 版一致）——

    public static List<(IslandEvent Event, HostConfig Host)> Samples(DateTime? at = null)
    {
        var now = at ?? DateTime.Now;
        var slurm = new HostConfig("cluster-a", L("集群A", "Cluster A"), HostKind.Slurm);
        var direct = new HostConfig("ws", L("工作站", "Workstation"), HostKind.Direct);
        var outList = new List<(IslandEvent, HostConfig)>();
        void Ev(EventKind k, string t, HostConfig h, string? job, PushInfo? p, string sub = "") =>
            outList.Add((new IslandEvent(k, t, sub, h.DisplayName, h.Id, job, p) { Date = now }, h));

        Ev(EventKind.Finished, L("gnep-train 已完成", "gnep-train finished"), slurm, "12345", new PushInfo
        {
            Id = "12345", Partition = "gpu", Nodes = 1, Cpus = 32, Gpus = 4, Elapsed = 3 * 3600 + 12 * 60, Limit = 24 * 3600,
            Dir = "/home/demo/nep/train-03", App = L("GNEP 训练：200/200 epoch · loss 1.23e-2", "GNEP training: 200/200 epoch · loss 1.23e-2"), Cost = "¥38.40",
        });
        Ev(EventKind.Failed, L("relax-Si 超时", "relax-Si Timed out"), slurm, "12346", new PushInfo
        {
            Id = "12346", Partition = "cpu", Nodes = 2, Cpus = 128, State = "TIMEOUT", ExitCode = "0:0", Elapsed = 24 * 3600, Limit = 24 * 3600,
            Dir = "/home/demo/vasp/relax-Si", App = L("VASP 结构优化：87 离子步", "VASP relaxation: 87 ionic steps"),
        });
        Ev(EventKind.Failed, L("NEP 训练提前结束", "NEP training ended early"), direct, "8812", new PushInfo
        {
            Source = JobSource.Process, Id = "8812", Elapsed = 5 * 3600 + 40 * 60, ElapsedAtLeast = true,
            App = L("NEP 训练：12,000/50,000 代 · L1 4.51e-2", "NEP training: 12,000/50,000 gen · L1 4.51e-2"),
            AppNote = L("输出文件最后更新于 3分钟前", "output last updated 3m ago"),
            Sample = L("CPU 98% · 内存 3.2 GB · 显存 18.2 GB", "CPU 98% · mem 3.2 GB · GPU mem 18.2 GB"), Dir = "/data/demo/nep/train-03",
        });
        Ev(EventKind.Failed, L("scan 数组已全部结束", "scan: array finished"), slurm, "12400", new PushInfo
        {
            Id = "12400", Partition = "cpu", Array = L("98 个成功 · 2 个失败 / 共 100 个", "98 done · 2 failed / 100 total"),
            FailedElements = ["#17", "#52"], Dir = "/home/demo/scan",
        });
        Ev(EventKind.Started, L("md-run 开始运行", "md-run started"), slurm, "12410", new PushInfo
        {
            Id = "12410", Partition = "gpu", NodeList = "gpu07", Limit = 24 * 3600,
        });
        Ev(EventKind.Warning, L("磁盘快满了：/home", "Disk almost full: /home"), slurm, null, null, L("已用 92% · 剩余 41 GB", "92% used · 41 GB free"));
        return outList;
    }
}
