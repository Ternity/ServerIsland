using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace ServerIsland.Core;

/// AI 工具的用量统计，给副屏的"AI 用量"页面用（和 Mac 版 AIUsage.swift 对应）。数据来自 CC Switch 的本地数据库（只读打开，不改任何东西）。
/// 统计口径（去重规则、各家 input token 的含义）移植自 wangjian124/cc-usage-display 的 companion/metrics.py（MIT），
/// 它和 CC Switch 自己的面板保持一致。
public static class AIUsage
{
    public static string DatabasePath => Path.Combine(AppPaths.Home, ".cc-switch", "cc-switch.db");

    public static bool Available => File.Exists(DatabasePath);

    /// 这些工具的 input_tokens 包含缓存部分，要扣掉才是"新输入"。
    static readonly string[] CacheInclusiveApps = ["codex", "gemini", "grokbuild"];
    static readonly string[] SessionSources = ["session_log", "codex_session", "gemini_session", "opencode_session"];

    static string FreshInput
    {
        get
        {
            var apps = string.Join(", ", CacheInclusiveApps.Select(a => $"'{a}'"));
            return "CASE WHEN input_token_semantics = 2 THEN input_tokens " +
                   $"WHEN app_type IN ({apps}) AND input_token_semantics = 1 AND input_tokens >= (cache_read_tokens + cache_creation_tokens) " +
                   "THEN input_tokens - cache_read_tokens - cache_creation_tokens " +
                   $"WHEN app_type IN ({apps}) AND input_token_semantics = 0 AND input_tokens >= cache_read_tokens " +
                   "THEN input_tokens - cache_read_tokens ELSE input_tokens END";
        }
    }

    static string RealTotal => $"(({FreshInput}) + output_tokens + cache_read_tokens + cache_creation_tokens)";

    /// 从本地会话日志导入的行，如果 10 分钟内有一条相同用量的代理请求，就是同一次调用，不重复计。
    static string EffectiveFilter
    {
        get
        {
            const string source = "COALESCE(l.data_source, 'proxy')";
            var sessions = string.Join(", ", SessionSources.Select(a => $"'{a}'"));
            const string sessionOnly = "'codex_session', 'gemini_session', 'opencode_session'";
            return $"""
                NOT ({source} IN ({sessions}) AND EXISTS (
                  SELECT 1 FROM proxy_request_logs p
                  WHERE COALESCE(p.data_source, 'proxy') = 'proxy' AND p.status_code >= 200 AND p.status_code < 300
                    AND p.app_type IN (l.app_type, CASE WHEN l.app_type = 'claude' THEN 'claude-desktop' ELSE l.app_type END)
                    AND p.input_tokens = l.input_tokens AND p.output_tokens = l.output_tokens AND p.cache_read_tokens = l.cache_read_tokens
                    AND (p.cache_creation_tokens = l.cache_creation_tokens
                         OR (l.cache_creation_tokens = 0 AND {source} IN ({sessionOnly}))
                         OR (p.cache_creation_tokens = 0 AND p.input_token_semantics = 0 AND {source} = 'codex_session'))
                    AND p.created_at BETWEEN l.created_at - 600 AND l.created_at + 600
                    AND (LOWER(p.model) = LOWER(l.model) OR LOWER(p.model) = 'unknown' OR LOWER(l.model) = 'unknown')))
                """;
        }
    }

    /// 读一次，返回发给副屏的 `ai` 消息。没有数据库或读失败时返回 null。
    public static JsonObject? Snapshot(DateTime? nowArg = null)
    {
        if (!Available) return null;
        var now = nowArg ?? DateTime.Now;
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2,
            }.ToString());
            db.Open();
            // 三分钟问一次，但大多数时候没有新请求：记录数、最新一行和日期都没变就直接用上次的结果。
            var fingerprint = string.Join("|", Rows(db, "SELECT COUNT(*), COALESCE(MAX(rowid), 0) FROM proxy_request_logs").FirstOrDefault() ?? []) + "|" + now.Date.Ticks;
            lock (CacheLock)
                if (_cache is { } c && c.Key == fingerprint && now - c.At < TimeSpan.FromMinutes(10)) return (JsonObject)c.Msg.DeepClone();
            var todayStart = new DateTimeOffset(now.Date).ToUnixTimeSeconds();
            var weekStart = todayStart - 6 * 86400;
            var monthStart = todayStart - 29 * 86400;
            var end = new DateTimeOffset(now).ToUnixTimeSeconds();
            var filter = EffectiveFilter;
            var real = RealTotal;

            double[] Aggregate(long start)
            {
                var sql = $"""
                    SELECT COUNT(*),
                      COALESCE(SUM(CASE WHEN l.status_code >= 200 AND l.status_code < 300 THEN 1 ELSE 0 END), 0),
                      COALESCE(SUM(CASE WHEN l.status_code >= 400 THEN 1 ELSE 0 END), 0),
                      COALESCE(SUM({real}), 0), COALESCE(SUM({FreshInput}), 0), COALESCE(SUM(l.output_tokens), 0),
                      COALESCE(SUM(l.cache_read_tokens), 0), COALESCE(SUM(l.cache_creation_tokens), 0),
                      COALESCE(SUM(CAST(l.total_cost_usd AS REAL)), 0),
                      COALESCE(AVG(CASE WHEN l.first_token_ms > 0 THEN l.first_token_ms END), 0)
                    FROM proxy_request_logs l WHERE l.created_at >= {start} AND l.created_at <= {end} AND {filter}
                    """;
                return Rows(db, sql).FirstOrDefault()?.Select(Num).ToArray() ?? new double[10];
            }
            var today = Aggregate(todayStart);
            var month = Aggregate(monthStart);
            if (today.Length != 10 || month.Length != 10) return null;
            var week = Aggregate(weekStart);

            // 今天每小时、最近 7 天每天的 token 数（画小柱状图）。
            var hours = new double[24];
            foreach (var r in Rows(db, $"SELECT (l.created_at - {todayStart}) / 3600, SUM({real}) FROM proxy_request_logs l WHERE l.created_at >= {todayStart} AND l.created_at <= {end} AND {filter} GROUP BY 1"))
                if (int.TryParse(r[0], out var h) && h is >= 0 and < 24) hours[h] = Num(r[1]);
            var days = new double[7];
            foreach (var r in Rows(db, $"SELECT (l.created_at - {weekStart}) / 86400, SUM({real}) FROM proxy_request_logs l WHERE l.created_at >= {weekStart} AND l.created_at <= {end} AND {filter} GROUP BY 1"))
                if (int.TryParse(r[0], out var dd) && dd is >= 0 and < 7) days[dd] = Num(r[1]);
            // 今天各个工具（claude / codex / gemini …）的调用和 token。
            var apps = new JsonArray(Rows(db, $"SELECT l.app_type, COUNT(*), SUM({real}) FROM proxy_request_logs l WHERE l.created_at >= {todayStart} AND l.created_at <= {end} AND {filter} GROUP BY l.app_type ORDER BY 3 DESC LIMIT 4")
                .Select(r => (JsonNode)new JsonObject { ["app"] = r[0], ["calls"] = (int)Num(r[1]), ["tokens"] = (long)Num(r[2]) }).ToArray());
            var latest = Rows(db, $"SELECT l.model, l.status_code, l.latency_ms, l.error_message, l.created_at FROM proxy_request_logs l WHERE {filter} ORDER BY l.created_at DESC, l.rowid DESC LIMIT 1").FirstOrDefault();
            var top = Rows(db, $"SELECT l.model, COUNT(*) FROM proxy_request_logs l WHERE l.created_at >= {monthStart} AND l.created_at <= {end} AND l.model <> '' AND {filter} GROUP BY l.model ORDER BY 2 DESC, 1 LIMIT 1").FirstOrDefault();
            var sessions = Rows(db, $"SELECT COUNT(DISTINCT CASE WHEN l.session_id <> '' THEN l.session_id END) FROM proxy_request_logs l WHERE l.created_at >= {monthStart} AND l.created_at <= {end} AND {filter}").FirstOrDefault() is { } sr ? (int)Num(sr[0]) : 0;

            static string Clip(string s) => s.Length > 40 ? s[..39] + "…" : s;
            var cacheable = month[4] + month[6] + month[7];
            var todayCacheable = today[4] + today[6] + today[7];
            var msg = new JsonObject
            {
                ["t"] = "ai", ["src"] = "cc-switch",
                ["today"] = new JsonObject
                {
                    ["calls"] = (int)today[0], ["ok"] = (int)today[1], ["err"] = (int)today[2], ["tokens"] = (long)today[3],
                    ["cost"] = Math.Round(today[8] * 1000) / 1000, ["ttft"] = (int)today[9],
                    ["cache"] = todayCacheable > 0 ? (int)Math.Round(today[6] * 1000 / todayCacheable) : 0,
                },
                ["week"] = new JsonObject { ["tokens"] = (long)week[3], ["cost"] = Math.Round(week[8] * 100) / 100 },
                ["month"] = new JsonObject
                {
                    ["calls"] = (int)month[0], ["tokens"] = (long)month[3], ["in"] = (long)month[4], ["out"] = (long)month[5],
                    ["cr"] = (long)month[6], ["cw"] = (long)month[7], ["cost"] = Math.Round(month[8] * 100) / 100, ["sessions"] = sessions,
                    ["cache"] = cacheable > 0 ? (int)Math.Round(month[6] * 1000 / cacheable) : 0,
                },
                ["hours"] = new JsonArray(hours.Select(x => (JsonNode)(long)x).ToArray()),
                ["days"] = new JsonArray(days.Select(x => (JsonNode)(long)x).ToArray()),
                ["apps"] = apps,
            };
            if (latest != null)
            {
                var status = (int)Num(latest[1]);
                msg["latest"] = new JsonObject
                {
                    ["model"] = Clip(latest[0]), ["status"] = status, ["latency"] = (int)Num(latest[2]),
                    ["failed"] = latest[3].Length > 0 || status < 200 || status >= 400,
                    ["ago"] = Math.Max(0, end - (long)(latest[4].Length > 0 ? Num(latest[4]) : end)),
                };
            }
            if (top != null) msg["top"] = new JsonObject { ["model"] = Clip(top[0]), ["calls"] = (int)Num(top[1]) };
            msg["tools"] = Tools(db, todayStart, weekStart, end, filter);
            lock (CacheLock) _cache = (fingerprint, now, (JsonObject)msg.DeepClone());
            return msg;
        }
        catch (Exception e)
        {
            DebugLog.Log("AI 用量：读取失败 " + e.Message);
            return null;
        }
    }

    /// 副屏 AI 页点进去看的各工具详情（Claude 桌面版算进 Claude）：今天、最近 7 天、每天的 token、缓存命中（‰）、常用模型。
    /// 按 7 天用量从多到少，最多 4 个。
    static JsonArray Tools(SqliteConnection db, long todayStart, long weekStart, long end, string filter)
    {
        const string tool = "(CASE WHEN l.app_type = 'claude-desktop' THEN 'claude' ELSE l.app_type END)";
        // 最后三列是速度：平均首字时间（毫秒），以及有首字时间的请求的输出 token 和生成用时（总耗时减首字时间）。
        const string streamed = "l.latency_ms > l.first_token_ms AND l.first_token_ms > 0 AND l.output_tokens > 0";
        var sums = $"COUNT(*), COALESCE(SUM({RealTotal}), 0), COALESCE(SUM(CAST(l.total_cost_usd AS REAL)), 0), "
                   + $"COALESCE(SUM({FreshInput}), 0), COALESCE(SUM(l.cache_read_tokens), 0), COALESCE(SUM(l.cache_creation_tokens), 0), "
                   + "COALESCE(AVG(CASE WHEN l.first_token_ms > 0 THEN l.first_token_ms END), 0), "
                   + $"COALESCE(SUM(CASE WHEN {streamed} THEN l.output_tokens END), 0), COALESCE(SUM(CASE WHEN {streamed} THEN l.latency_ms - l.first_token_ms END), 0)";
        Dictionary<string, double[]> ByTool(long start) =>
            Rows(db, $"SELECT {tool}, {sums} FROM proxy_request_logs l WHERE l.created_at >= {start} AND l.created_at <= {end} AND {filter} GROUP BY 1")
                .ToDictionary(r => r[0], r => r.Skip(1).Select(Num).ToArray());
        var today = ByTool(todayStart);
        var week = ByTool(weekStart);
        var days = new Dictionary<string, long[]>();
        foreach (var r in Rows(db, $"SELECT {tool}, (l.created_at - {weekStart}) / 86400, SUM({RealTotal}) FROM proxy_request_logs l WHERE l.created_at >= {weekStart} AND l.created_at <= {end} AND {filter} GROUP BY 1, 2"))
        {
            var d = (int)Num(r[1]);
            if (d < 0 || d >= 7) continue;
            if (!days.TryGetValue(r[0], out var arr)) days[r[0]] = arr = new long[7];
            arr[d] = (long)Num(r[2]);
        }
        var list = new JsonArray();
        foreach (var (name, w) in week.OrderByDescending(kv => kv.Value[1]).Take(4))
        {
            var t = today.GetValueOrDefault(name) ?? new double[9];
            var cacheable = t[3] + t[4] + t[5];
            var o = new JsonObject
            {
                ["app"] = name,
                ["today"] = new JsonObject
                {
                    ["calls"] = (int)t[0], ["tokens"] = (long)t[1], ["cost"] = Math.Round(t[2] * 100) / 100,
                    ["cache"] = cacheable > 0 ? (int)Math.Round(t[4] * 1000 / cacheable) : -1,
                    ["ttft"] = (int)t[6], ["tps"] = t[8] > 0 ? (int)Math.Round(t[7] * 1000 / t[8]) : 0,
                },
                ["week"] = new JsonObject { ["tokens"] = (long)w[1], ["cost"] = Math.Round(w[2] * 100) / 100 },
                ["days"] = new JsonArray((days.GetValueOrDefault(name) ?? new long[7]).Select(x => (JsonNode)x).ToArray()),
            };
            var q = name.Replace("'", "''");
            if (Rows(db, $"SELECT l.model, COUNT(*) FROM proxy_request_logs l WHERE {tool} = '{q}' AND l.created_at >= {weekStart} AND l.created_at <= {end} AND l.model <> '' AND {filter} GROUP BY l.model ORDER BY 2 DESC, 1 LIMIT 1").FirstOrDefault() is { } m)
                o["model"] = m[0].Length > 30 ? m[0][..29] + "…" : m[0];
            // Claude 的 5 小时窗口（估算）：最近 10 小时的调用按时间排好，套用窗口规则。
            if (name == "claude")
            {
                var entries = Rows(db, $"SELECT l.created_at, {RealTotal} FROM proxy_request_logs l WHERE {tool} = 'claude' AND l.created_at >= {end - 36000} AND l.created_at <= {end} AND {filter}")
                    .Select(r => (DateTimeOffset.FromUnixTimeSeconds((long)Num(r[0])).UtcDateTime, (long)Num(r[1]))).ToList();
                if (Window(entries, DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime) is { } win)
                    o["window"] = new JsonObject { ["tokens"] = win.Tokens, ["reset"] = new DateTimeOffset(win.Reset).ToUnixTimeSeconds() };
            }
            list.Add(o);
        }
        return list;
    }

    /// Claude 订阅的 5 小时窗口（估算，和社区工具 ccusage 的算法一样）：从第一次调用所在的整点开始；
    /// 过了 5 小时、或者中间空了 5 小时以上，就开新窗口。现在已经不在窗口里时返回 null。时间都用 UTC。
    public static (long Tokens, DateTime Reset)? Window(IReadOnlyList<(DateTime At, long Tokens)> entries, DateTime now)
    {
        var block = TimeSpan.FromHours(5);
        DateTime? start = null, last = null;
        long tokens = 0;
        foreach (var e in entries.OrderBy(e => e.At))
        {
            if (start is { } s && last is { } l && e.At < s + block && e.At - l < block) tokens += e.Tokens;
            else
            {
                start = new DateTime(e.At.Year, e.At.Month, e.At.Day, e.At.Hour, 0, 0, DateTimeKind.Utc);
                tokens = e.Tokens;
            }
            last = e.At;
        }
        return start is { } st && now < st + block ? (tokens, st + block) : null;
    }

    // 上一次的结果（指纹、时间、消息）；快照在后台线程里取，所以加锁。超过 10 分钟也重新算（"多久之前"会变）。
    static readonly object CacheLock = new();
    static (string Key, DateTime At, JsonObject Msg)? _cache;

    static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// 执行一条查询，每行按列转成字符串（NULL 变成空串）。
    static List<string[]> Rows(SqliteConnection db, string sql)
    {
        var out_ = new List<string[]>();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new string[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++)
                    row[i] = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                out_.Add(row);
            }
        }
        catch (Exception e) { DebugLog.Log("AI 用量：查询失败 " + e.Message); }
        return out_;
    }
}
