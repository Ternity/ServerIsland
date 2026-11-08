using System.Text.Json.Nodes;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// 副屏上的截止日期和天气（和 Mac 版 DisplayExtras.swift 对应；都只在打开"外接副屏"后才用到）。
/// - 日程：Mac 版读系统日历；Windows / Linux 没有统一的日历接口，这里只发截止日期。
/// - 截止日期：用户在设置里填。
/// - 天气：Open-Meteo（免费、不要密钥），只有填了城市才请求。
public sealed class DisplayExtras
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    JsonObject? _weather;
    string _weatherCity = "";
    DateTime? _weatherAt;
    int _weatherRequest;

    /// `cal` 消息：截止日期（最多 8 个）；日程为空。
    public static JsonObject CalendarMessage(AppSettings settings) => new()
    {
        ["t"] = "cal",
        ["events"] = new JsonArray(),
        ["deadlines"] = new JsonArray(settings.Deadlines.Where(d => d.Date != null).OrderBy(d => d.Date).Take(8)
            .Select(d => (JsonNode)new JsonObject
            {
                ["title"] = d.Title.Length > 30 ? d.Title[..30] : d.Title,
                ["at"] = new DateTimeOffset(d.Date!.Value).ToUnixTimeSeconds(),
            }).ToArray()),
    };

    /// 天气：城市变了或超过 30 分钟就重新查，查完回调 `weather` 消息（回调在调用者的线程上，见 await）。
    public async Task RefreshWeather(string city, bool force, Action<JsonObject> send)
    {
        city = city.Trim();
        if (city.Length == 0)
        {
            _weatherRequest++;
            _weather = null; _weatherCity = ""; _weatherAt = null;
            send(new JsonObject { ["t"] = "weather", ["clear"] = true });
            return;
        }
        if (!force && city == _weatherCity && _weatherAt is { } at && DateTime.Now - at < TimeSpan.FromMinutes(30) && _weather != null)
        {
            send((JsonObject)_weather.DeepClone());
            return;
        }
        if (city != _weatherCity)
        {
            _weather = null; _weatherAt = null;
            send(new JsonObject { ["t"] = "weather", ["clear"] = true });
        }
        _weatherCity = city;
        var request = ++_weatherRequest;
        var msg = await FetchWeather(city);
        if (msg == null || request != _weatherRequest) return;
        _weather = msg;
        _weatherAt = DateTime.Now;
        send((JsonObject)msg.DeepClone());
    }

    /// 先用城市名查经纬度，再查当前温度、天气代码和今天的最高最低温。
    static async Task<JsonObject?> FetchWeather(string city)
    {
        try
        {
            var lang = IsEnglish ? "en" : "zh";
            var geo = JsonNode.Parse(await Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language={lang}"));
            if (geo?["results"] is not JsonArray results || results.Count == 0 || results[0] is not JsonObject place
                || place["latitude"]?.GetValue<double>() is not { } lat || place["longitude"]?.GetValue<double>() is not { } lon)
            {
                DebugLog.Log("天气：找不到城市 " + city);
                return null;
            }
            var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code&hourly=precipitation_probability,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");
            var obj = JsonNode.Parse(await Http.GetStringAsync(url));
            if (obj?["current"] is not JsonObject cur || obj["daily"] is not JsonObject daily)
            {
                DebugLog.Log("天气：查询失败");
                return null;
            }
            static int Round(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d, MidpointRounding.AwayFromZero) : 0;
            var code = cur["weather_code"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : -1;
            static List<T> Arr<T>(JsonNode? n) => n is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<T>(out var t) ? t : default!).ToList() : [];
            var hourly = obj["hourly"] as JsonObject;
            var rain = RainAhead(Arr<string>(hourly?["time"]), Arr<int>(hourly?["weather_code"]), Arr<int>(hourly?["precipitation_probability"]),
                                 cur["time"]?.GetValue<string>() ?? "");
            var msg = new JsonObject
            {
                ["t"] = "weather",
                ["date"] = (cur["time"]?.GetValue<string>() ?? "").Split('T')[0], ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["city"] = place["name"]?.GetValue<string>() ?? city,
                ["temp"] = Round(cur["temperature_2m"]),
                ["max"] = Round((daily["temperature_2m_max"] as JsonArray)?.FirstOrDefault()),
                ["min"] = Round((daily["temperature_2m_min"] as JsonArray)?.FirstOrDefault()),
                ["desc"] = Describe(code),
                ["code"] = code,
            };
            if (rain is { } r) msg["rain"] = new JsonObject { ["from"] = r.From, ["prob"] = r.Prob };
            return msg;
        }
        catch (Exception e)
        {
            DebugLog.Log("天气：查询失败 " + e.Message);
            return null;
        }
    }

    /// 今天接下来会不会下雨：从当前这个小时起，第一个下雨（天气代码是雨 / 雷雨，或降水概率 ≥ 60%）的整点，
    /// 以及这些小时里最大的降水概率。时间都是当地时间的 "2026-11-01T14:00"，按字符串比较。
    public static (int From, int Prob)? RainAhead(IReadOnlyList<string> times, IReadOnlyList<int> codes, IReadOnlyList<int> probs, string now)
    {
        var hour = now.Length >= 13 ? now[..13] : now;
        int? from = null;
        var prob = 0;
        for (var i = 0; i < times.Count; i++)
        {
            var t = times[i] ?? "";
            if (t.Length < 13 || string.CompareOrdinal(t[..13], hour) < 0 || t[..10] != (hour.Length >= 10 ? hour[..10] : hour)) continue;
            var c = i < codes.Count ? codes[i] : -1;
            var p = i < probs.Count ? probs[i] : 0;
            var wet = c is (>= 51 and <= 67) or (>= 80 and <= 82) or (>= 95 and <= 99) || p >= 60;
            if (!wet) continue;
            from ??= int.Parse(t.Substring(11, 2));
            prob = Math.Max(prob, p);
        }
        return from is { } f ? (f, prob) : null;
    }

    /// WMO 天气代码 → 一两个字的描述。
    public static string Describe(int code) => code switch
    {
        0 => L("晴", "Clear"),
        1 or 2 => L("多云", "Partly cloudy"),
        3 => L("阴", "Overcast"),
        45 or 48 => L("雾", "Fog"),
        >= 51 and <= 57 => L("毛毛雨", "Drizzle"),
        (>= 61 and <= 67) or (>= 80 and <= 82) => L("雨", "Rain"),
        (>= 71 and <= 77) or 85 or 86 => L("雪", "Snow"),
        >= 95 and <= 99 => L("雷雨", "Thunderstorm"),
        _ => "-",
    };
}
