import Foundation
import SQLite3

/// AI 工具的用量统计，给 USB 副屏的"AI 用量"页面用。数据来自 CC Switch 的本地数据库（只读打开，不改任何东西）。
/// 统计口径（去重规则、各家 input token 的含义）移植自 wangjian124/cc-usage-display 的 companion/metrics.py（MIT），
/// 它和 CC Switch 自己的面板保持一致。
enum AIUsage {
    static var databasePath: String { NSHomeDirectory() + "/.cc-switch/cc-switch.db" }

    static var available: Bool { FileManager.default.isReadableFile(atPath: databasePath) }

    /// 这些工具的 input_tokens 包含缓存部分，要扣掉才是"新输入"。
    private static let cacheInclusiveApps = ["codex", "gemini", "grokbuild"]
    private static let sessionSources = ["session_log", "codex_session", "gemini_session", "opencode_session"]

    private static var freshInput: String {
        let apps = cacheInclusiveApps.map { "'\($0)'" }.joined(separator: ", ")
        return """
        CASE WHEN input_token_semantics = 2 THEN input_tokens \
        WHEN app_type IN (\(apps)) AND input_token_semantics = 1 AND input_tokens >= (cache_read_tokens + cache_creation_tokens) \
        THEN input_tokens - cache_read_tokens - cache_creation_tokens \
        WHEN app_type IN (\(apps)) AND input_token_semantics = 0 AND input_tokens >= cache_read_tokens \
        THEN input_tokens - cache_read_tokens ELSE input_tokens END
        """
    }

    private static var realTotal: String { "((\(freshInput)) + output_tokens + cache_read_tokens + cache_creation_tokens)" }

    /// 从本地会话日志导入的行，如果 10 分钟内有一条相同用量的代理请求，就是同一次调用，不重复计。
    private static var effectiveFilter: String {
        let source = "COALESCE(l.data_source, 'proxy')"
        let sessions = sessionSources.map { "'\($0)'" }.joined(separator: ", ")
        let sessionOnly = "'codex_session', 'gemini_session', 'opencode_session'"
        return """
        NOT (\(source) IN (\(sessions)) AND EXISTS (
          SELECT 1 FROM proxy_request_logs p
          WHERE COALESCE(p.data_source, 'proxy') = 'proxy' AND p.status_code >= 200 AND p.status_code < 300
            AND p.app_type IN (l.app_type, CASE WHEN l.app_type = 'claude' THEN 'claude-desktop' ELSE l.app_type END)
            AND p.input_tokens = l.input_tokens AND p.output_tokens = l.output_tokens AND p.cache_read_tokens = l.cache_read_tokens
            AND (p.cache_creation_tokens = l.cache_creation_tokens
                 OR (l.cache_creation_tokens = 0 AND \(source) IN (\(sessionOnly)))
                 OR (p.cache_creation_tokens = 0 AND p.input_token_semantics = 0 AND \(source) = 'codex_session'))
            AND p.created_at BETWEEN l.created_at - 600 AND l.created_at + 600
            AND (LOWER(p.model) = LOWER(l.model) OR LOWER(p.model) = 'unknown' OR LOWER(l.model) = 'unknown')))
        """
    }

    /// 读一次，返回发给副屏的 `ai` 消息（副屏协议，固件暂不在仓库里）。没有数据库或读失败时返回 nil。
    static func snapshot(now: Date = Date()) -> [String: Any]? {
        guard available else { return nil }
        var db: OpaquePointer?
        guard sqlite3_open_v2(databasePath, &db, SQLITE_OPEN_READONLY, nil) == SQLITE_OK, let db else {
            sqlite3_close(db)
            return nil
        }
        defer { sqlite3_close(db) }
        sqlite3_busy_timeout(db, 2000)
        // 三分钟问一次，但大多数时候没有新请求：记录数、最新一行和日期都没变就直接用上次的结果。
        let fingerprint = (rows(db, "SELECT COUNT(*), COALESCE(MAX(rowid), 0) FROM proxy_request_logs").first ?? []).joined(separator: "|")
            + "|" + String(Int(Calendar.current.startOfDay(for: now).timeIntervalSince1970))
        cacheLock.lock()
        if let c = cache, c.key == fingerprint, now.timeIntervalSince(c.at) < 600 { cacheLock.unlock(); return c.msg }
        cacheLock.unlock()

        let cal = Calendar.current
        let todayStart = Int(cal.startOfDay(for: now).timeIntervalSince1970)
        let weekStart = todayStart - 6 * 86400
        let monthStart = todayStart - 29 * 86400
        let end = Int(now.timeIntervalSince1970)
        let filter = effectiveFilter

        func aggregate(_ start: Int) -> [Double] {
            let sql = """
            SELECT COUNT(*),
              COALESCE(SUM(CASE WHEN l.status_code >= 200 AND l.status_code < 300 THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN l.status_code >= 400 THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(\(realTotal)), 0), COALESCE(SUM(\(freshInput)), 0), COALESCE(SUM(l.output_tokens), 0),
              COALESCE(SUM(l.cache_read_tokens), 0), COALESCE(SUM(l.cache_creation_tokens), 0),
              COALESCE(SUM(CAST(l.total_cost_usd AS REAL)), 0),
              COALESCE(AVG(CASE WHEN l.first_token_ms > 0 THEN l.first_token_ms END), 0)
            FROM proxy_request_logs l WHERE l.created_at >= \(start) AND l.created_at <= \(end) AND \(filter)
            """
            return rows(db, sql).first.map { $0.map { Double($0) ?? 0 } } ?? Array(repeating: 0, count: 10)
        }
        let today = aggregate(todayStart)
        let month = aggregate(monthStart)
        guard today.count == 10, month.count == 10 else { return nil }
        let week = aggregate(weekStart)

        // 今天每小时、最近 7 天每天的 token 数（画小柱状图）。
        var hours = Array(repeating: 0.0, count: 24)
        for r in rows(db, "SELECT (l.created_at - \(todayStart)) / 3600, SUM(\(realTotal)) FROM proxy_request_logs l WHERE l.created_at >= \(todayStart) AND l.created_at <= \(end) AND \(filter) GROUP BY 1") {
            if let h = Int(r[0]), h >= 0, h < 24 { hours[h] = Double(r[1]) ?? 0 }
        }
        var days = Array(repeating: 0.0, count: 7)
        for r in rows(db, "SELECT (l.created_at - \(weekStart)) / 86400, SUM(\(realTotal)) FROM proxy_request_logs l WHERE l.created_at >= \(weekStart) AND l.created_at <= \(end) AND \(filter) GROUP BY 1") {
            if let d = Int(r[0]), d >= 0, d < 7 { days[d] = Double(r[1]) ?? 0 }
        }
        // 今天各个工具（claude / codex / gemini …）的调用和 token。
        let apps = rows(db, "SELECT l.app_type, COUNT(*), SUM(\(realTotal)) FROM proxy_request_logs l WHERE l.created_at >= \(todayStart) AND l.created_at <= \(end) AND \(filter) GROUP BY l.app_type ORDER BY 3 DESC LIMIT 4")
            .map { ["app": $0[0], "calls": Int($0[1]) ?? 0, "tokens": Int64(Double($0[2]) ?? 0)] as [String: Any] }
        let latest = rows(db, "SELECT l.model, l.status_code, l.latency_ms, l.error_message, l.created_at FROM proxy_request_logs l WHERE \(filter) ORDER BY l.created_at DESC, l.rowid DESC LIMIT 1").first
        let top = rows(db, "SELECT l.model, COUNT(*) FROM proxy_request_logs l WHERE l.created_at >= \(monthStart) AND l.created_at <= \(end) AND l.model <> '' AND \(filter) GROUP BY l.model ORDER BY 2 DESC, 1 LIMIT 1").first
        let sessions = rows(db, "SELECT COUNT(DISTINCT CASE WHEN l.session_id <> '' THEN l.session_id END) FROM proxy_request_logs l WHERE l.created_at >= \(monthStart) AND l.created_at <= \(end) AND \(filter)").first.flatMap { Int($0[0]) } ?? 0

        func clip(_ s: String) -> String { s.count > 40 ? String(s.prefix(39)) + "…" : s }
        let cacheable = month[4] + month[6] + month[7]
        let todayCacheable = today[4] + today[6] + today[7]
        var msg: [String: Any] = [
            "t": "ai", "src": "cc-switch",
            "today": ["calls": Int(today[0]), "ok": Int(today[1]), "err": Int(today[2]), "tokens": Int64(today[3]),
                      "cost": (today[8] * 1000).rounded() / 1000, "ttft": Int(today[9]),
                      "cache": todayCacheable > 0 ? Int((today[6] * 1000 / todayCacheable).rounded()) : 0],
            "week": ["tokens": Int64(week[3]), "cost": (week[8] * 100).rounded() / 100],
            "month": ["calls": Int(month[0]), "tokens": Int64(month[3]), "in": Int64(month[4]), "out": Int64(month[5]),
                      "cr": Int64(month[6]), "cw": Int64(month[7]), "cost": (month[8] * 100).rounded() / 100, "sessions": sessions,
                      "cache": cacheable > 0 ? Int((month[6] * 1000 / cacheable).rounded()) : 0],
            "hours": hours.map { Int64($0) }, "days": days.map { Int64($0) }, "apps": apps,
        ]
        if let l = latest {
            let status = Int(l[1]) ?? 0
            msg["latest"] = ["model": clip(l[0]), "status": status, "latency": Int(l[2]) ?? 0,
                             "failed": !l[3].isEmpty || status < 200 || status >= 400, "ago": max(0, end - (Int(l[4]) ?? end))]
        }
        if let t = top { msg["top"] = ["model": clip(t[0]), "calls": Int(t[1]) ?? 0] }
        msg["tools"] = tools(db, todayStart: todayStart, weekStart: weekStart, end: end, filter: filter)
        cacheLock.lock()
        cache = (fingerprint, now, msg)
        cacheLock.unlock()
        return msg
    }

    // 上一次的结果（指纹、时间、消息）。快照在后台线程里取，所以加锁。时间超过 10 分钟也重新算（"多久之前"会变）。
    nonisolated(unsafe) private static var cache: (key: String, at: Date, msg: [String: Any])?
    private static let cacheLock = NSLock()

    /// 副屏 AI 页点进去看的各工具详情（Claude 桌面版算进 Claude）：今天、最近 7 天、每天的 token、缓存命中（‰）、常用模型。
    /// 按 7 天用量从多到少，最多 4 个。
    private static func tools(_ db: OpaquePointer, todayStart: Int, weekStart: Int, end: Int, filter: String) -> [[String: Any]] {
        let tool = "(CASE WHEN l.app_type = 'claude-desktop' THEN 'claude' ELSE l.app_type END)"
        // 最后三列是速度：平均首字时间（毫秒），以及有首字时间的请求的输出 token 和生成用时（总耗时减首字时间）。
        let streamed = "l.latency_ms > l.first_token_ms AND l.first_token_ms > 0 AND l.output_tokens > 0"
        let sums = "COUNT(*), COALESCE(SUM(\(realTotal)), 0), COALESCE(SUM(CAST(l.total_cost_usd AS REAL)), 0), "
            + "COALESCE(SUM(\(freshInput)), 0), COALESCE(SUM(l.cache_read_tokens), 0), COALESCE(SUM(l.cache_creation_tokens), 0), "
            + "COALESCE(AVG(CASE WHEN l.first_token_ms > 0 THEN l.first_token_ms END), 0), "
            + "COALESCE(SUM(CASE WHEN \(streamed) THEN l.output_tokens END), 0), COALESCE(SUM(CASE WHEN \(streamed) THEN l.latency_ms - l.first_token_ms END), 0)"
        func byTool(_ start: Int) -> [String: [Double]] {
            var out: [String: [Double]] = [:]
            for r in rows(db, "SELECT \(tool), \(sums) FROM proxy_request_logs l WHERE l.created_at >= \(start) AND l.created_at <= \(end) AND \(filter) GROUP BY 1") {
                out[r[0]] = r.dropFirst().map { Double($0) ?? 0 }
            }
            return out
        }
        let today = byTool(todayStart), week = byTool(weekStart)
        var days: [String: [Int64]] = [:]
        for r in rows(db, "SELECT \(tool), (l.created_at - \(weekStart)) / 86400, SUM(\(realTotal)) FROM proxy_request_logs l WHERE l.created_at >= \(weekStart) AND l.created_at <= \(end) AND \(filter) GROUP BY 1, 2") {
            if let d = Int(r[1]), d >= 0, d < 7 { days[r[0], default: Array(repeating: 0, count: 7)][d] = Int64(Double(r[2]) ?? 0) }
        }
        return week.sorted { $0.value[1] > $1.value[1] }.prefix(4).map { name, w in
            let t = today[name] ?? Array(repeating: 0, count: 9)
            let cacheable = t[3] + t[4] + t[5]
            var o: [String: Any] = [
                "app": name,
                "today": ["calls": Int(t[0]), "tokens": Int64(t[1]), "cost": (t[2] * 100).rounded() / 100,
                          "cache": cacheable > 0 ? Int((t[4] * 1000 / cacheable).rounded()) : -1,
                          "ttft": Int(t[6]), "tps": t[8] > 0 ? Int((t[7] * 1000 / t[8]).rounded()) : 0],
                "week": ["tokens": Int64(w[1]), "cost": (w[2] * 100).rounded() / 100],
                "days": days[name] ?? Array(repeating: 0, count: 7),
            ]
            let q = name.replacingOccurrences(of: "'", with: "''")
            if let m = rows(db, "SELECT l.model, COUNT(*) FROM proxy_request_logs l WHERE \(tool) = '\(q)' AND l.created_at >= \(weekStart) AND l.created_at <= \(end) AND l.model <> '' AND \(filter) GROUP BY l.model ORDER BY 2 DESC, 1 LIMIT 1").first {
                o["model"] = m[0].count > 30 ? String(m[0].prefix(29)) + "…" : m[0]
            }
            // Claude 的 5 小时窗口（估算）：最近 10 小时的调用按时间排好，套用窗口规则。
            if name == "claude" {
                let entries = rows(db, "SELECT l.created_at, \(realTotal) FROM proxy_request_logs l WHERE \(tool) = 'claude' AND l.created_at >= \(end - 36000) AND l.created_at <= \(end) AND \(filter)")
                    .map { (at: Date(timeIntervalSince1970: Double($0[0]) ?? 0), tokens: Int(Double($0[1]) ?? 0)) }
                if let w = window(entries, now: Date(timeIntervalSince1970: Double(end))) {
                    o["window"] = ["tokens": w.tokens, "reset": Int64(w.reset.timeIntervalSince1970)]
                }
            }
            return o
        }
    }

    /// Claude 订阅的 5 小时窗口（估算，和社区工具 ccusage 的算法一样）：从第一次调用所在的整点开始；
    /// 过了 5 小时、或者中间空了 5 小时以上，就开新窗口。现在已经不在窗口里时返回 nil。
    nonisolated static func window(_ entries: [(at: Date, tokens: Int)], now: Date) -> (tokens: Int, reset: Date)? {
        let block: TimeInterval = 5 * 3600
        var start: Date?, last: Date?, tokens = 0
        for e in entries.sorted(by: { $0.at < $1.at }) {
            if let s = start, let l = last, e.at < s.addingTimeInterval(block), e.at.timeIntervalSince(l) < block {
                tokens += e.tokens
            } else {
                start = Date(timeIntervalSince1970: floor(e.at.timeIntervalSince1970 / 3600) * 3600)
                tokens = e.tokens
            }
            last = e.at
        }
        guard let s = start, now < s.addingTimeInterval(block) else { return nil }
        return (tokens, s.addingTimeInterval(block))
    }

    /// 执行一条查询，每行按列转成字符串（NULL 变成空串）。
    private static func rows(_ db: OpaquePointer, _ sql: String) -> [[String]] {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else {
            DebugLog.log("AI 用量：查询失败 \(String(cString: sqlite3_errmsg(db)))")
            return []
        }
        defer { sqlite3_finalize(stmt) }
        var out: [[String]] = []
        let n = sqlite3_column_count(stmt)
        while sqlite3_step(stmt) == SQLITE_ROW {
            out.append((0..<n).map { i in sqlite3_column_text(stmt, i).map { String(cString: $0) } ?? "" })
        }
        return out
    }
}
