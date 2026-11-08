import AppKit
import EventKit
import Foundation

/// 副屏上的日程、截止日期和天气（都只在打开"外接副屏"后才用到）。
/// - 日程：macOS 日历（EventKit），要用户授权，默认关。
/// - 截止日期：用户在设置里填。
/// - 天气：Open-Meteo（免费、不要密钥），只有填了城市才请求。
@MainActor
final class DisplayExtras {
    private let eventStore = EKEventStore()
    private var weather: [String: Any]?
    private var weatherCity = ""
    private var weatherAt: Date?
    private var weatherRequest = 0

    /// 日历授权状态（设置页显示用）。
    static var calendarAuthorized: Bool { EKEventStore.authorizationStatus(for: .event) == .fullAccess }

    /// 打开"显示日程"时调用：没授权就弹系统授权框。
    func requestCalendarAccess(_ done: @escaping @MainActor (Bool) -> Void) {
        eventStore.requestFullAccessToEvents { granted, _ in
            Task { @MainActor in done(granted) }
        }
    }

    /// `cal` 消息：今天起 31 天的日程（最多 40 条）+ 截止日期。
    func calendarMessage(settings: AppSettings) -> [String: Any] {
        var events: [[String: Any]] = []
        if settings.displayCalendar, Self.calendarAuthorized {
            let start = Calendar.current.startOfDay(for: Date())
            let end = start.addingTimeInterval(31 * 86400)
            let pred = eventStore.predicateForEvents(withStart: start, end: end, calendars: nil)
            for e in eventStore.events(matching: pred).sorted(by: { $0.startDate < $1.startDate }).prefix(40) {
                var o: [String: Any] = ["title": String((e.title ?? "").prefix(40)),
                                        "start": Int(e.startDate.timeIntervalSince1970),
                                        "end": Int(e.endDate.timeIntervalSince1970)]
                if e.isAllDay { o["allDay"] = true }
                if let c = e.calendar?.cgColor.flatMap({ NSColor(cgColor: $0)?.usingColorSpace(.sRGB) }) {
                    o["color"] = String(format: "#%02X%02X%02X", Int(c.redComponent * 255), Int(c.greenComponent * 255), Int(c.blueComponent * 255))
                }
                events.append(o)
            }
        }
        let deadlines: [[String: Any]] = settings.deadlines.sorted { $0.date < $1.date }.prefix(8).map {
            ["title": String($0.title.prefix(30)), "at": Int($0.date.timeIntervalSince1970)]
        }
        return ["t": "cal", "events": events, "deadlines": deadlines]
    }

    /// 天气：城市变了或超过 30 分钟就重新查，查完回调 `weather` 消息。
    func refreshWeather(city: String, force: Bool = false, send: @escaping @MainActor ([String: Any]) -> Void) {
        let city = city.trimmingCharacters(in: .whitespaces)
        guard !city.isEmpty else {
            weatherRequest += 1
            weather = nil
            weatherCity = ""
            weatherAt = nil
            send(["t": "weather", "clear": true])
            return
        }
        if !force, city == weatherCity, let at = weatherAt, Date().timeIntervalSince(at) < 1800, let w = weather {
            send(w)
            return
        }
        if city != weatherCity {
            weather = nil
            weatherAt = nil
            send(["t": "weather", "clear": true])
        }
        weatherCity = city
        weatherRequest += 1
        let request = weatherRequest
        Task.detached(priority: .utility) {
            guard let msg = await Self.fetchWeather(city: city) else { return }
            await MainActor.run { [weak self] in
                guard let self, self.weatherRequest == request else { return }
                self.weather = msg
                self.weatherAt = Date()
                send(msg)
            }
        }
    }

    /// 先用城市名查经纬度，再查当前温度、天气代码和今天的最高最低温。
    nonisolated private static func fetchWeather(city: String) async -> [String: Any]? {
        let lang = Lang.isEnglish ? "en" : "zh"
        guard let q = city.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let geoURL = URL(string: "https://geocoding-api.open-meteo.com/v1/search?name=\(q)&count=1&language=\(lang)"),
              let (gd, _) = try? await URLSession.shared.data(from: geoURL),
              let geo = (try? JSONSerialization.jsonObject(with: gd)) as? [String: Any],
              let place = (geo["results"] as? [[String: Any]])?.first,
              let lat = place["latitude"] as? Double, let lon = place["longitude"] as? Double else {
            DebugLog.log("天气：找不到城市 \(city)")
            return nil
        }
        guard let url = URL(string: "https://api.open-meteo.com/v1/forecast?latitude=\(lat)&longitude=\(lon)&current=temperature_2m,weather_code&hourly=precipitation_probability,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1"),
              let (d, _) = try? await URLSession.shared.data(from: url),
              let obj = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any],
              let cur = obj["current"] as? [String: Any], let daily = obj["daily"] as? [String: Any] else {
            DebugLog.log("天气：查询失败")
            return nil
        }
        let code = cur["weather_code"] as? Int ?? -1
        var msg: [String: Any] = ["t": "weather", "city": place["name"] as? String ?? city,
                                  "date": String((cur["time"] as? String ?? "").prefix(10)), "at": Int(Date().timeIntervalSince1970),
                                  "temp": Int(((cur["temperature_2m"] as? Double) ?? 0).rounded()),
                                  "max": Int((((daily["temperature_2m_max"] as? [Double])?.first) ?? 0).rounded()),
                                  "min": Int((((daily["temperature_2m_min"] as? [Double])?.first) ?? 0).rounded()),
                                  "desc": describe(code), "code": code]
        if let hourly = obj["hourly"] as? [String: Any],
           let rain = rainAhead(times: hourly["time"] as? [String] ?? [], codes: hourly["weather_code"] as? [Int] ?? [],
                                probs: hourly["precipitation_probability"] as? [Int] ?? [], now: cur["time"] as? String ?? "") {
            msg["rain"] = ["from": rain.from, "prob": rain.prob]
        }
        return msg
    }

    /// 今天接下来会不会下雨：从当前这个小时起，第一个下雨（天气代码是雨 / 雷雨，或降水概率 ≥ 60%）的整点，
    /// 以及这些小时里最大的降水概率。时间都是当地时间的 "2026-11-01T14:00"，按字符串比较。
    nonisolated static func rainAhead(times: [String], codes: [Int], probs: [Int], now: String) -> (from: Int, prob: Int)? {
        let hour = String(now.prefix(13))
        var from: Int?, prob = 0
        for (i, t) in times.enumerated() where t.prefix(13) >= hour && t.prefix(10) == hour.prefix(10) {
            let c = i < codes.count ? codes[i] : -1, p = i < probs.count ? probs[i] : 0
            let wet = (51...67).contains(c) || (80...82).contains(c) || (95...99).contains(c) || p >= 60
            guard wet else { continue }
            if from == nil { from = Int(t.dropFirst(11).prefix(2)) }
            prob = max(prob, p)
        }
        return from.map { ($0, prob) }
    }

    /// WMO 天气代码 → 一两个字的描述。
    nonisolated static func describe(_ code: Int) -> String {
        switch code {
        case 0: return L("晴", "Clear")
        case 1, 2: return L("多云", "Partly cloudy")
        case 3: return L("阴", "Overcast")
        case 45, 48: return L("雾", "Fog")
        case 51...57: return L("毛毛雨", "Drizzle")
        case 61...67, 80...82: return L("雨", "Rain")
        case 71...77, 85, 86: return L("雪", "Snow")
        case 95...99: return L("雷雨", "Thunderstorm")
        default: return "-"
        }
    }
}
