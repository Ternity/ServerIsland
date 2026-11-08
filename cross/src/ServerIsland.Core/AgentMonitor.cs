using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// AI 编程工具（Claude Code、Codex、OpenCode）的会话状态，给副屏的 Agents 页面和提醒用（和 Mac 版 AgentMonitor.swift 对应）。
/// - Claude Code / OpenCode：装了钩子 / 插件后，每个事件会运行 `ServerIsland --agent-event <工具>`，
///   往 `agent-events.jsonl` 追加一行（`AgentEvents.Record`）；App 读这个文件。
/// - Codex：它的 notify 只能设一个程序（常被别的工具占用），所以不装东西，直接读 ~/.codex/sessions 里最近的会话记录。
/// 只读这些工具的记录，不改它们的行为；钩子都是异步的，不会拖慢它们，也从不替用户批准或拒绝权限请求。
public static class AgentEvents
{
    public static string FilePath => Path.Combine(AppPaths.ConfigDirectory, "agent-events.jsonl");

    /// 命令行入口：钩子把事件 JSON 从标准输入（OpenCode 插件放在参数里）传进来。整理成一行写进事件文件。
    /// 不往标准输出打印任何东西（对 Claude 的 PermissionRequest 来说，有输出就可能被当成决定）。
    public static void Record(string tool, string? argument)
    {
        string raw;
        try { raw = argument ?? Console.In.ReadToEnd(); } catch { return; }
        JsonObject? obj;
        try { obj = JsonNode.Parse(raw) as JsonObject; } catch { return; }
        if (obj == null) return;
        var line = new JsonObject
        {
            ["tool"] = tool,
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["session"] = Str(obj, "session_id") ?? Str(obj, "sessionID") ?? Str(obj, "session") ?? "",
            ["cwd"] = Str(obj, "cwd") ?? Str(obj, "directory") ?? "",
            ["event"] = Str(obj, "hook_event_name") ?? Str(obj, "type") ?? "",
        };
        if (Str(obj, "notification_type") is { } kind) line["kind"] = kind;
        if (Str(obj, "message") is { } m) line["message"] = Prefix(m, 120);
        if (Str(obj, "tool_name") is { } tn) line["toolName"] = tn;
        if (Str(obj, "status") is { } status) line["status"] = status;
        else if (obj["status"] is JsonObject so && Str(so, "type") is { } st) line["status"] = st;
        var input = obj["tool_input"] as JsonObject ?? obj;
        // 任务清单（TodoWrite、update_plan 等）：只记下操作，不记别的参数。
        if (Str(line, "event") == "PostToolUse" && Str(obj, "tool_name") is { } planTool && AgentPlan.Op(planTool, input) is { } op) line["plan"] = op;
        if (input["questions"] is JsonArray qs && qs.Count > 0 && qs[0] is JsonObject q && (Str(q, "question") ?? Str(q, "header")) is { } question)
            line["message"] = Prefix(question, 120);
        // 只留提问的开头当会话标题（本机文件，不外传）。
        if (Str(obj, "prompt") is { } p)
            line["prompt"] = Prefix(p.Split('\n', '\r').FirstOrDefault() ?? "", 60);
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            // 追加写：多个钩子同时写也不会交错（单次写入很小）。
            using var f = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = Encoding.UTF8.GetBytes(line.ToJsonString() + "\n");
            f.Write(bytes);
        }
        catch { }
    }

    internal static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string Prefix(string s, int n) => s.Length > n ? s[..n] : s;
}

/// 一个会话的状态。
public sealed record AgentSession(string Tool, string Id, string Cwd, string Title, AgentState State, string Message, DateTime ChangedAt)
{
    public AgentPlan Plan { get; init; } = AgentPlan.Empty;
    public DateTime PlanAt { get; init; } = DateTime.MinValue;
}

/// 存储的值和 Mac 版一致（working / attention / done / error）。
public enum AgentState { Working, Attention, Done, Error }

public sealed class AgentMonitor
{
    readonly string _eventsPath;
    readonly string _codexRoot;
    bool _loaded;
    readonly Dictionary<string, DateTime> _activity = [];
    readonly HashSet<string> _codexFiles = [];
    DateTime _lastDiscovery = DateTime.MinValue;
    public AgentMonitor(string? eventsPath = null, string? codexRoot = null)
    {
        _eventsPath = eventsPath ?? AgentEvents.FilePath;
        _codexRoot = codexRoot ?? Path.Combine(AppPaths.Home, ".codex", "sessions");
    }
    readonly Dictionary<string, AgentSession> _sessions = [];
    public IReadOnlyDictionary<string, AgentSession> Sessions => _sessions;
    /// 状态变化（需要你确认 / 回答完了）时回调，用来发提醒。
    public Action<AgentSession>? OnAlert;
    public Action? OnChange;

    long _offset;
    readonly Dictionary<string, long> _codexOffsets = [];
    readonly Dictionary<string, bool> _codexUserSessions = [];
    readonly Dictionary<string, string> _codexDirectories = [];
    readonly Dictionary<string, string> _codexTurns = [];
    sealed record Question(HashSet<int> Unanswered, bool Asynchronous, string Message);
    readonly Dictionary<string, Dictionary<string, Question>> _codexQuestions = [];

    /// 会话多久没动静就不再显示（已完成的）/ 不再认为在跑（工作中的，可能被强制退出了）。
    public static readonly TimeSpan DoneTTL = TimeSpan.FromHours(2);
    public static readonly TimeSpan StaleWorking = TimeSpan.FromMinutes(30);

    /// 启动时只看最近的事件，不把以前的提醒再发一遍。
    public void LoadInitial()
    {
        var path = _eventsPath;
        try
        {
            // 文件太大就截掉前面的部分（只是缓冲，不需要保留历史）。
            if (!_loaded && File.Exists(path) && new FileInfo(path).Length > 2_000_000)
            {
                var data = File.ReadAllBytes(path);
                File.WriteAllBytes(path, data[^200_000..]);
            }
        }
        catch { }
        _loaded = true;
        ReadEvents(false);
        ScanCodex(false);
        Prune();
    }

    public void Poll()
    {
        var before = Snapshot();
        ReadEvents(true);
        ScanCodex(true);
        Prune();
        if (!Snapshot().SequenceEqual(before)) OnChange?.Invoke();
    }

    List<AgentSession> Snapshot() => _sessions.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();

    /// 发给副屏的列表：需要你的排最前，然后是在跑的，再后是刚完成的。
    public List<AgentSession> List
    {
        get
        {
            static int Order(AgentState s) => s switch { AgentState.Attention => 0, AgentState.Working => 1, AgentState.Error => 2, _ => 3 };
            return _sessions.Values.OrderBy(s => Order(s.State)).ThenByDescending(s => s.ChangedAt).ToList();
        }
    }

    void ReadEvents(bool alert)
    {
        byte[] data;
        try
        {
            using var f = new FileStream(_eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length < _offset) _offset = 0;
            f.Seek(_offset, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            f.CopyTo(ms);
            data = ms.ToArray();
        }
        catch { return; }
        // 只处理完整的行，剩下半行下次再读。
        var last = Array.LastIndexOf(data, (byte)'\n');
        if (last < 0) return;
        _offset += last + 1;
        foreach (var line in Encoding.UTF8.GetString(data, 0, last).Split('\n'))
        {
            if (line.Length == 0) continue;
            try { if (JsonNode.Parse(line) is JsonObject o) Apply(o, alert); } catch { }
        }
    }

    public void Apply(JsonObject o, bool alert)
    {
        var tool = AgentEvents.Str(o, "tool") ?? "?";
        var sid = AgentEvents.Str(o, "session") ?? "";
        var key = tool + "|" + sid;
        var at = o["at"] is JsonValue av && av.TryGetValue<double>(out var secs)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(secs * 1000)).LocalDateTime : DateTime.Now;
        var ev = AgentEvents.Str(o, "event") ?? "";
        var known = _sessions.TryGetValue(key, out var existing);
        // 第一次见到的会话先当作空闲：只有看到它开始干活，之后的"完成"才提醒。
        var s = existing ?? new AgentSession(tool, sid, "", "", AgentState.Done, "", at);
        // 钩子是异步跑的，写进文件的顺序可能和发生顺序不同：比上一次状态变化还早的事件（比如晚到的
        // permission_prompt 通知，而工具其实已经批准并跑完了）不能再把状态改回去。
        if (o["plan"] is JsonObject planOp && at >= s.PlanAt) s = s with { Plan = s.Plan.Apply(planOp), PlanAt = at };
        if (known && at < s.ChangedAt && ev != "SessionEnd" && ev != "session.deleted")
        {
            _sessions[key] = s;
            return;
        }
        if (AgentEvents.Str(o, "cwd") is { Length: > 0 } c) s = s with { Cwd = c };
        if (AgentEvents.Str(o, "prompt") is { Length: > 0 } p) s = s with { Title = p };
        var old = s.State;
        var toolName = AgentEvents.Str(o, "toolName");
        var message = AgentEvents.Str(o, "message");
        switch (tool, ev)
        {
            case ("claude", "SessionEnd") or ("opencode", "session.deleted"):
                _sessions.Remove(key);
                return;
            case ("claude", "UserPromptSubmit" or "PostToolUse" or "PostToolUseFailure" or "SessionStart" or "ElicitationResult")
                or ("opencode", "question.replied" or "question.rejected" or "permission.replied"):
                s = s with { State = ev == "SessionStart" ? AgentState.Done : AgentState.Working, Message = "" };
                break;
            case ("opencode", "session.status"):
                var status = AgentEvents.Str(o, "status") ?? "";
                if (status == "idle") s = s with { State = AgentState.Done, Message = "" };
                else if (status is "busy" or "retry") s = s with { State = AgentState.Working, Message = "" };
                else return;
                break;
            case ("claude", "PreToolUse"):
                if (toolName is not ("AskUserQuestion" or "ExitPlanMode")) return;
                s = s with { State = AgentState.Attention, Message = message ?? L("有问题等你回答", "Waiting for your answer") };
                break;
            case ("claude", "Elicitation") or ("opencode", "question.asked"):
                s = s with { State = AgentState.Attention, Message = message ?? L("有问题等你回答", "Waiting for your answer") };
                break;
            case ("claude", "PermissionRequest") or ("opencode", "permission.asked"):
                s = s with { State = AgentState.Attention, Message = toolName != null ? L($"要用 {toolName}，等你确认", $"Wants to use {toolName}") : L("等你确认", "Needs your OK") };
                break;
            case ("claude", "Notification"):
                var kind = AgentEvents.Str(o, "kind") ?? "";
                if (kind is "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input")
                    s = s with { State = AgentState.Attention, Message = message ?? L("等你确认", "Needs your OK") };
                else if (kind is "elicitation_complete" or "elicitation_response")
                    s = s with { State = AgentState.Working, Message = "" };
                else if (kind == "idle_prompt")
                    s = s with { State = AgentState.Done };
                break;
            case ("claude", "Stop") or ("opencode", "session.idle"):
                s = s with { State = AgentState.Done, Message = "" };
                break;
            case ("claude", "StopFailure") or ("opencode", "session.error"):
                s = s with { State = AgentState.Error, Message = L("出错了", "Error") };
                break;
            default:
                return;
        }
        if (s.State != old || !known) s = s with { ChangedAt = at };
        _sessions[key] = s;
        _activity[key] = _activity.GetValueOrDefault(key) > at ? _activity[key] : at;
        // 提醒：变成"需要你确认"，或者从"工作中"变成"完成 / 出错"。
        if (alert && s.State != old && (s.State == AgentState.Attention || (old == AgentState.Working && s.State is AgentState.Done or AgentState.Error)))
            OnAlert?.Invoke(s);
    }

    /// 只接收用户会话；子 agent 和内部辅助任务的完成不代表用户这一轮结束。
    public static bool IsCodexUserSession(JsonObject metadata)
    {
        if (AgentEvents.Str(metadata, "parent_thread_id") is { Length: > 0 }) return false;
        if (AgentEvents.Str(metadata, "source") is not { Length: > 0 } source) return false;
        return source != "subagent";
    }

    /// Codex：读最近 6 小时有改动的用户会话，按 turn_id 匹配开始和结束。
    void ScanCodex(bool alert)
    {
        var root = _codexRoot;
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.Now.AddHours(-6);
        // 旧会话可能仍在使用：每 10 分钟发现一次，其余轮询只检查已知文件和最近三天。
        if (DateTime.Now - _lastDiscovery >= TimeSpan.FromMinutes(10))
        {
            _lastDiscovery = DateTime.Now;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                    if (File.GetLastWriteTime(f) > cutoff) _codexFiles.Add(f);
            }
            catch { }
        }
        for (var back = 0; back <= 2; back++)
        {
            var dir = Path.Combine(root, DateTime.Now.AddDays(-back).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture).Replace('/', Path.DirectorySeparatorChar));
            IEnumerable<string> files;
            try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jsonl").ToList() : []; } catch { continue; }
            _codexFiles.UnionWith(files);
        }
        foreach (var f in _codexFiles.ToArray())
        {
            try { if (File.GetLastWriteTime(f) <= cutoff) { _codexFiles.Remove(f); continue; } }
            catch { _codexFiles.Remove(f); continue; }
            ReadCodex(f, alert);
        }
    }

    static readonly string[] CodexMarkers = ["\"session_meta\"", "\"task_started\"", "\"task_complete\"", "\"turn_aborted\"", "\"user_message\"",
                                             "\"function_call\"", "\"function_call_output\""];

    public void ReadCodex(string path, bool alert)
    {
        FileStream h;
        try { h = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); } catch { return; }
        using var _ = h;
        // 元数据在文件头；增量读取从尾部开始时也必须先检查，不能漏掉子任务标记。
        if (!_codexUserSessions.ContainsKey(path))
        {
            var header = new byte[Math.Min(256_000, h.Length)];
            var n = h.Read(header, 0, header.Length);
            var nl = Array.IndexOf(header, (byte)'\n', 0, n);
            if (nl < 0) return;
            JsonObject? meta;
            try { meta = JsonNode.Parse(Encoding.UTF8.GetString(header, 0, nl)) as JsonObject; } catch { return; }
            if (meta == null || AgentEvents.Str(meta, "type") != "session_meta" || meta["payload"] is not JsonObject metadata) return;
            _codexUserSessions[path] = IsCodexUserSession(metadata);
            if (AgentEvents.Str(metadata, "cwd") is { } cwd) _codexDirectories[path] = cwd;
        }
        if (_codexUserSessions[path] != true) return;
        var end = h.Length;
        var start = _codexOffsets.TryGetValue(path, out var o0) ? o0 : (end > 400_000 ? end - 400_000 : 0);
        if (end < start) start = 0;
        h.Seek(start, SeekOrigin.Begin);
        var data = new byte[end - start];
        var read = 0;
        while (read < data.Length) { var r = h.Read(data, read, data.Length - read); if (r <= 0) break; read += r; }
        var last = Array.LastIndexOf(data, (byte)'\n', Math.Max(0, read - 1));
        if (read == 0 || last < 0) return;
        _codexOffsets[path] = start + last + 1;
        var key = "codex|" + Path.GetFileNameWithoutExtension(path);
        var lineStart = 0;
        for (var i = 0; i <= last; i++)
        {
            if (data[i] != (byte)'\n') continue;
            var len = i - lineStart;
            var from = lineStart;
            lineStart = i + 1;
            // 先粗筛，避免每行都解析大段 JSON。
            if (len <= 0 || len >= 200_000) continue;
            var text = Encoding.UTF8.GetString(data, from, len);
            if (!CodexMarkers.Any(text.Contains) && !(text.Contains("\"message\"") && text.Contains("\"user\""))) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(text) as JsonObject; } catch { continue; }
            if (o == null) continue;
            ApplyCodex(key, path, o, alert);
        }
    }

    void ApplyCodex(string key, string path, JsonObject o, bool alert)
    {
        var payload = o["payload"] as JsonObject ?? [];
        var at = AgentEvents.Str(o, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.LocalDateTime : DateTime.Now;
        var known = _sessions.TryGetValue(key, out var existing);
        var s = existing ?? new AgentSession("codex", key, _codexDirectories.GetValueOrDefault(path, ""), "", AgentState.Done, "", at);
        var old = existing?.State ?? (_codexTurns.ContainsKey(key) ? AgentState.Working : s.State);
        var newQuestion = false;
        var questions = _codexQuestions.TryGetValue(key, out var qd) ? qd : null;
        bool NoQuestions() => !_codexQuestions.TryGetValue(key, out var q) || q.Count == 0;
        var type = AgentEvents.Str(o, "type");
        var ptype = AgentEvents.Str(payload, "type");
        switch (type, ptype)
        {
            case ("session_meta", _):
                s = s with { Cwd = AgentEvents.Str(payload, "cwd") ?? s.Cwd };
                break;
            case ("event_msg", "user_message"):
                if (AgentEvents.Str(payload, "message") is { } m) s = s with { Title = FirstLine(m) };
                break;
            case ("event_msg", "task_started"):
                if (AgentEvents.Str(payload, "turn_id") is not { } turn) return;
                _codexTurns[key] = turn;
                s = s with { State = NoQuestions() ? AgentState.Working : AgentState.Attention };
                break;
            case ("event_msg", "task_complete"):
                if (AgentEvents.Str(payload, "turn_id") is not { } done || done != _codexTurns.GetValueOrDefault(key)) return;
                _codexTurns.Remove(key);
                s = s with { State = NoQuestions() ? AgentState.Done : AgentState.Attention };
                break;
            case ("event_msg", "turn_aborted"):
                if (AgentEvents.Str(payload, "turn_id") is not { } aborted || aborted != _codexTurns.GetValueOrDefault(key)) return;
                _codexTurns.Remove(key);
                _codexQuestions.Remove(key);
                s = s with { State = AgentState.Done };
                break;
            case ("response_item", "function_call") when (AgentEvents.Str(payload, "name") ?? "").EndsWith("update_plan"):
            {
                JsonObject? args;
                try { args = JsonNode.Parse(AgentEvents.Str(payload, "arguments") ?? "") as JsonObject; } catch { return; }
                if (args == null || AgentPlan.Op("update_plan", args) is not { } op) return;
                s = s with { Plan = s.Plan.Apply(op) };
                break;
            }
            case ("response_item", "function_call"):
            {
                var name = (AgentEvents.Str(payload, "name") ?? "").Split('.').Last();
                if (name is not ("request_user_input" or "request_user_input_async")) return;
                if (AgentEvents.Str(payload, "call_id") is not { } call || AgentEvents.Str(payload, "arguments") is not { } arguments) return;
                JsonArray? qs;
                try { qs = (JsonNode.Parse(arguments) as JsonObject)?["questions"] as JsonArray; } catch { return; }
                if (qs == null || qs.Count == 0) return;
                var first = qs[0] as JsonObject;
                var message = AgentEvents.Prefix((first != null ? AgentEvents.Str(first, "title") ?? AgentEvents.Str(first, "question") : null)
                                                 ?? L("有问题等你回答", "Waiting for your answer"), 120);
                if (!_codexQuestions.TryGetValue(key, out var map)) _codexQuestions[key] = map = [];
                newQuestion = !map.ContainsKey(call);
                map[call] = new Question([.. Enumerable.Range(0, qs.Count)], name.EndsWith("_async"), message);
                s = s with { State = AgentState.Attention, Message = message };
                break;
            }
            case ("response_item", "function_call_output"):
            {
                if (AgentEvents.Str(payload, "call_id") is not { } call || questions == null || !questions.TryGetValue(call, out var q) || q.Asynchronous) return;
                questions.Remove(call);
                s = s with { State = NoQuestions() ? (_codexTurns.ContainsKey(key) ? AgentState.Working : AgentState.Done) : AgentState.Attention, Message = "" };
                break;
            }
            case ("response_item", "message") when AgentEvents.Str(payload, "role") == "user":
            {
                var body = string.Join("\n", (payload["content"] as JsonArray ?? []).OfType<JsonObject>()
                                                 .Select(c => AgentEvents.Str(c, "text")).Where(t => t != null));
                const string open = "<send_user_message_question_reply>", close = "</send_user_message_question_reply>";
                var b = body.IndexOf(open, StringComparison.Ordinal);
                var e = body.IndexOf(close, StringComparison.Ordinal);
                if (b >= 0 && e >= b + open.Length)
                {
                    try
                    {
                        if (JsonNode.Parse(body[(b + open.Length)..e].Trim()) is JsonArray replies)
                            foreach (var reply in replies.OfType<JsonObject>())
                            {
                                if (AgentEvents.Str(reply, "questionItemId") is not { } raw || JsonNode.Parse(raw) is not JsonArray id || id.Count < 3) continue;
                                if (id[1] is not JsonValue cv || !cv.TryGetValue<string>(out var call)) continue;
                                if (id[2] is not JsonValue iv || !iv.TryGetValue<int>(out var index)) continue;
                                if (questions == null || !questions.TryGetValue(call, out var q)) continue;
                                q.Unanswered.Remove(index);
                                if (q.Unanswered.Count == 0) questions.Remove(call);
                            }
                    }
                    catch { }
                }
                else if (body.Trim().Length > 0)
                {
                    _codexQuestions.Remove(key);
                    s = s with { Title = FirstLine(body) };
                }
                s = s with
                {
                    State = NoQuestions() ? (_codexTurns.ContainsKey(key) ? AgentState.Working : AgentState.Done) : AgentState.Attention,
                    Message = _codexQuestions.TryGetValue(key, out var left) && left.Count > 0 ? left.Values.First().Message : "",
                };
                break;
            }
            default:
                return;
        }
        if (s.State != old || !known || newQuestion) s = s with { ChangedAt = at };
        _sessions[key] = s;
        _activity[key] = _activity.GetValueOrDefault(key) > at ? _activity[key] : at;
        if (alert && newQuestion && Math.Abs((DateTime.Now - at).TotalSeconds) < 30) OnAlert?.Invoke(s);
        // 中断不报“回答完了”，也不补报几小时前的历史完成事件。
        if (alert && ptype == "task_complete" && s.State == AgentState.Done && old == AgentState.Working && (DateTime.Now - at).TotalSeconds < 30)
            OnAlert?.Invoke(s);
    }

    static string FirstLine(string m) => AgentEvents.Prefix(m.Split('\n', '\r').FirstOrDefault(x => x.Length > 0) ?? "", 60);

    void Prune()
    {
        var now = DateTime.Now;
        foreach (var (k, s) in _sessions.ToList())
        {
            var ttl = s.State == AgentState.Working ? StaleWorking : DoneTTL;
            var since = s.State == AgentState.Working ? _activity.GetValueOrDefault(k, s.ChangedAt) : s.ChangedAt;
            if (s.State == AgentState.Working && _codexTurns.ContainsKey(k)) ttl = TimeSpan.FromHours(6);
            if (now - since >= ttl) _sessions.Remove(k);
        }
    }
}

/// 一键安装 / 卸载钩子。只追加、只删除我们自己的那几条（命令里带 `--agent-event`），不动用户原有的配置；
/// 改之前先备份。
public static class AgentHooks
{
    public static string ClaudeSettings => Path.Combine(AppPaths.Home, ".claude", "settings.json");
    public static string OpenCodePlugin => Path.Combine(AppPaths.Home, ".config", "opencode", "plugins", "serverisland.js");
    public static readonly string[] ClaudeEvents = ["SessionStart", "UserPromptSubmit", "PermissionRequest", "Notification", "PreToolUse", "PostToolUse",
                                                    "PostToolUseFailure", "Elicitation", "ElicitationResult", "Stop", "StopFailure", "SessionEnd"];

    /// 钩子里调用的程序：正在运行的 ServerIsland 本身。
    public static string Executable => Environment.ProcessPath ?? "ServerIsland";

    public static bool ClaudeAvailable => Directory.Exists(Path.Combine(AppPaths.Home, ".claude"));
    public static bool OpenCodeAvailable => Directory.Exists(Path.Combine(AppPaths.Home, ".config", "opencode"));
    public static bool CodexAvailable => Directory.Exists(Path.Combine(AppPaths.Home, ".codex", "sessions"));

    static bool IsOurs(JsonNode? group) =>
        group is JsonObject g && g["hooks"] is JsonArray hooks &&
        hooks.OfType<JsonObject>().Any(h => AgentEvents.Str(h, "command")?.Contains("--agent-event claude") == true);

    static JsonObject? ReadHooks(string path)
    {
        try { return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["hooks"] as JsonObject; } catch { return null; }
    }

    public static bool ClaudeInstalled(string? path = null) =>
        ReadHooks(path ?? ClaudeSettings) is { } hooks && ClaudeEvents.All(ev => hooks[ev] is JsonArray a && a.Any(IsOurs));

    /// 钩子调用的就是现在这个程序（挪动或更新了程序位置后要"更新"一下）。
    public static bool ClaudePointsHere(string? path = null) =>
        ReadHooks(path ?? ClaudeSettings) is { } hooks && ClaudeEvents.All(ev => hooks[ev] is JsonArray a &&
            a.OfType<JsonObject>().Where(IsOurs).SelectMany(g => (g["hooks"] as JsonArray ?? []).OfType<JsonObject>())
             .Any(h => AgentEvents.Str(h, "command") == HookCommand(Executable)));

    public static bool ClaudePresent(string? path = null) =>
        ReadHooks(path ?? ClaudeSettings) is { } hooks && hooks.Any(kv => kv.Value is JsonArray a && a.Any(IsOurs));

    /// 钩子命令：Windows 上用双引号（Claude Code 在 Windows 上可能经 cmd 或 Git Bash 运行，双引号两边都认），其它系统和 Mac 版一样用单引号。
    public static string HookCommand(string exe) =>
        (OperatingSystem.IsWindows() ? $"\"{exe}\"" : $"'{exe}'") + " --agent-event claude";

    /// 安装或卸载 Claude Code 的钩子。返回 null 表示成功，否则是错误说明。
    public static string? SetClaude(bool installed, string? path = null, string? executable = null)
    {
        path ??= ClaudeSettings;
        var obj = new JsonObject();
        if (File.Exists(path))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) throw new JsonException();
                obj = o;
            }
            catch { return L("settings.json 不是合法的 JSON，没有修改", "settings.json isn't valid JSON; left unchanged"); }
            try { File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "settings.json.serverisland-backup"), overwrite: true); } catch { }
        }
        var hooks = obj["hooks"] as JsonObject ?? [];
        obj.Remove("hooks");
        hooks = (JsonObject)hooks.DeepClone();
        foreach (var ev in ClaudeEvents)
        {
            var groups = new JsonArray();
            if (hooks[ev] is JsonArray existing)
                foreach (var g in existing)
                {
                    var copy = g?.DeepClone();
                    if (copy is JsonObject group && group["hooks"] is JsonArray entries)
                    {
                        for (var i = entries.Count - 1; i >= 0; i--)
                            if (entries[i] is JsonObject entry && (AgentEvents.Str(entry, "command") ?? "").Contains("--agent-event claude")) entries.RemoveAt(i);
                        if (entries.Count == 0) continue;
                    }
                    groups.Add(copy);
                }
            if (installed)
            {
                var group = new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command", ["command"] = HookCommand(executable ?? Executable), ["async"] = true, ["timeout"] = 10,
                    }),
                };
                if (ev == "PreToolUse") group["matcher"] = "AskUserQuestion|ExitPlanMode";
                groups.Add(group);
            }
            hooks.Remove(ev);
            if (groups.Count > 0) hooks[ev] = groups;
        }
        if (hooks.Count > 0) obj["hooks"] = hooks;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, SortKeys(obj)!.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { return e.Message; }
        return null;
    }

    /// 和 Mac 版一样按键名排序写出（两边装出来的文件一致，diff 也好看）。
    static JsonNode? SortKeys(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                        .Select(kv => KeyValuePair.Create(kv.Key, SortKeys(kv.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => SortKeys(x?.DeepClone())).ToArray()),
        _ => n?.DeepClone(),
    };

    public static bool OpenCodeInstalled => File.Exists(OpenCodePlugin);
    public static bool OpenCodeCurrent
    {
        get
        {
            try
            {
                var js = File.ReadAllText(OpenCodePlugin);
                return js.Contains("ServerIsland hooks v2") && js.Contains(new JsonArray(Executable).ToJsonString());
            }
            catch { return false; }
        }
    }

    public static string? SetOpenCode(bool installed, string? exe = null)
    {
        if (!installed)
        {
            try { File.Delete(OpenCodePlugin); } catch { }
            return null;
        }
        // 插件把关心的事件原样交给 ServerIsland（参数里的 JSON），出错也不影响 OpenCode。
        var exeJson = new JsonArray(exe ?? Executable).ToJsonString();
        var js = $$"""
            // ServerIsland hooks v2：只通知状态，不回答问题、不批准权限。
            const EXE = {{exeJson}}[0]
            const WATCH = new Set(["session.idle", "session.status", "session.error", "session.deleted", "permission.asked", "permission.replied", "question.asked", "question.replied", "question.rejected"])
            export const ServerIsland = async ({ $, directory }) => ({
              event: async ({ event }) => {
                if (!WATCH.has(event.type)) return
                const p = event.properties ?? {}
                const payload = JSON.stringify({ type: event.type, sessionID: p.sessionID ?? p.info?.id ?? "", directory,
                  status: p.status?.type, message: p.questions?.[0]?.question ?? p.questions?.[0]?.header })
                try { await $`${EXE} --agent-event opencode ${payload}`.quiet() } catch {}
              },
            })
            """;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OpenCodePlugin)!);
            File.WriteAllText(OpenCodePlugin, js.Replace("\r\n", "\n") + "\n");
        }
        catch (Exception e) { return e.Message; }
        return null;
    }
}
