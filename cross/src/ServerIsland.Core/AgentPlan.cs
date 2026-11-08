using System.Text.Json.Nodes;

namespace ServerIsland.Core;

/// AI 编程工具在会话里列的任务清单（给副屏的 Agents 页显示"3/7 · 正在做什么"）。和 Mac 版 AgentPlan.swift 对应。
/// 认这几种写法：
/// - Claude Code 命令行：TodoWrite（每次给出整张清单），或 TaskCreate / TaskUpdate（一条一条加、改状态）；
/// - Claude 桌面 App：update_plan（给整张清单，或者 advance / done / start 这样的增量操作）；
/// - Codex：update_plan（整张清单，字段是 step / status）。
/// 钩子只把操作记成一个小对象（`Op`），App 读事件时再用 `Apply` 得到新的清单（不改原来的）。
public sealed class AgentPlan : IEquatable<AgentPlan>
{
    public sealed record Item(string Text, string Status);  // Status：pending / in_progress / completed（/ deleted）

    public static readonly AgentPlan Empty = new([]);
    public IReadOnlyList<Item> Items { get; }
    public AgentPlan(IReadOnlyList<Item> items) => Items = items;

    public int Done => Items.Count(i => i.Status == "completed");
    /// TaskUpdate 删掉的那几条不算。
    public int Total => Items.Count(i => i.Status != "deleted");
    /// 正在做的那一步；没有就是下一步要做的。
    public string Now => (Items.FirstOrDefault(i => i.Status == "in_progress") ?? Items.FirstOrDefault(i => i.Status == "pending"))?.Text ?? "";

    static string Clip(string s) => s.Length > 80 ? s[..80] : s;
    static string S(JsonObject o, string key) => AgentEvents.Str(o, key) ?? "";

    /// 从工具调用的参数里取出清单操作；不是清单工具时返回 null。
    public static JsonObject? Op(string toolName, JsonObject input)
    {
        static JsonArray Set(JsonArray list, string textKey, string altKey) => new(list.OfType<JsonObject>()
            .Select(o => (JsonNode)new JsonArray(Clip(AgentEvents.Str(o, textKey) ?? AgentEvents.Str(o, altKey) ?? ""), AgentEvents.Str(o, "status") ?? "pending")).ToArray());
        if (toolName == "TodoWrite" && input["todos"] is JsonArray todos) return new JsonObject { ["set"] = Set(todos, "content", "content") };
        if (toolName == "TaskCreate") return new JsonObject { ["add"] = Clip(AgentEvents.Str(input, "subject") ?? S(input, "description")) };
        if (toolName == "TaskUpdate")
        {
            var id = AgentEvents.Str(input, "taskId") ?? (input["taskId"] is JsonValue v && v.TryGetValue<int>(out var n) ? n.ToString() : null);
            if (id == null) return null;
            var o = new JsonObject { ["update"] = id };
            if (AgentEvents.Str(input, "status") is { } st) o["status"] = st;
            if (AgentEvents.Str(input, "subject") is { } sub) o["text"] = Clip(sub);
            return o;
        }
        if (!toolName.EndsWith("update_plan")) return null;
        // Codex：plan[{step, status}]；桌面 App：items[{content, status}]，或者 advance / done / start。
        if ((input["plan"] ?? input["items"]) is JsonArray plan) return new JsonObject { ["set"] = Set(plan, "step", "content") };
        var r = new JsonObject();
        if (input["advance"] is JsonValue a && a.TryGetValue<bool>(out var adv) && adv) r["advance"] = true;
        if (input["done"] is JsonArray d) r["done"] = new JsonArray(d.Select(x => (JsonNode?)JsonValue.Create(x!.GetValue<int>())).ToArray());
        if (input["start"] is JsonValue sv && sv.TryGetValue<int>(out var start)) r["start"] = start;
        return r.Count == 0 ? null : r;
    }

    /// 按操作得到新的清单。序号（done / start）从 1 开始；TaskCreate 的编号按创建顺序从 1 开始。
    public AgentPlan Apply(JsonObject op)
    {
        var items = Items.ToList();
        if (op["set"] is JsonArray set)
            return new(set.OfType<JsonArray>().Where(p => p.Count == 2).Select(p => new Item(p[0]!.GetValue<string>(), p[1]!.GetValue<string>())).ToList());
        if (AgentEvents.Str(op, "add") is { } add)
        {
            items.Add(new Item(add, "pending"));
            return new(items);
        }
        if (AgentEvents.Str(op, "update") is { } id && int.TryParse(id, out var u) && u >= 1 && u <= items.Count)
        {
            if (AgentEvents.Str(op, "status") is { } st) items[u - 1] = items[u - 1] with { Status = st };
            if (AgentEvents.Str(op, "text") is { } t) items[u - 1] = items[u - 1] with { Text = t };
            return new(items);
        }
        if (op["advance"] is JsonValue av && av.TryGetValue<bool>(out var advance) && advance)
        {
            var i = items.FindIndex(x => x.Status == "in_progress");
            if (i >= 0)
            {
                items[i] = items[i] with { Status = "completed" };
                var next = items.FindIndex(i + 1, x => x.Status == "pending");
                if (next >= 0) items[next] = items[next] with { Status = "in_progress" };
            }
        }
        if (op["done"] is JsonArray done)
            foreach (var n in done.Select(x => x!.GetValue<int>()).Where(n => n >= 1 && n <= items.Count))
                items[n - 1] = items[n - 1] with { Status = "completed" };
        if (op["start"] is JsonValue sv && sv.TryGetValue<int>(out var s) && s >= 1 && s <= items.Count)
        {
            for (var i = 0; i < items.Count; i++) if (items[i].Status == "in_progress") items[i] = items[i] with { Status = "pending" };
            items[s - 1] = items[s - 1] with { Status = "in_progress" };
        }
        return new(items);
    }

    public bool Equals(AgentPlan? other) => other != null && Items.SequenceEqual(other.Items);
    public override bool Equals(object? obj) => Equals(obj as AgentPlan);
    public override int GetHashCode() => Items.Count;
}
