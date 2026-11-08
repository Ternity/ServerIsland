import Foundation

/// AI 编程工具在会话里列的任务清单（给副屏的 Agents 页显示"3/7 · 正在做什么"）。和 C# 版 AgentPlan.cs 对应。
/// 认这几种写法：
/// - Claude Code 命令行：TodoWrite（每次给出整张清单），或 TaskCreate / TaskUpdate（一条一条加、改状态）；
/// - Claude 桌面 App：update_plan（给整张清单，或者 advance / done / start 这样的增量操作）；
/// - Codex：update_plan（整张清单，字段是 step / status）。
/// 钩子只把操作记成一个小字典（`op`），App 读事件时再用 `apply` 更新清单。
struct AgentPlan: Equatable {
    struct Item: Equatable {
        var text: String
        var status: String  // pending / in_progress / completed
    }
    var items: [Item] = []

    var done: Int { items.filter { $0.status == "completed" }.count }
    /// TaskUpdate 删掉的那几条不算。
    var total: Int { items.filter { $0.status != "deleted" }.count }
    /// 正在做的那一步；没有就是下一步要做的。
    var now: String {
        (items.first { $0.status == "in_progress" } ?? items.first { $0.status == "pending" })?.text ?? ""
    }

    nonisolated static func clip(_ s: String) -> String { String(s.prefix(80)) }

    /// 从工具调用的参数里取出清单操作；不是清单工具时返回 nil。
    nonisolated static func op(toolName: String, input: [String: Any]) -> [String: Any]? {
        if toolName == "TodoWrite", let todos = input["todos"] as? [[String: Any]] {
            return ["set": todos.map { [clip($0["content"] as? String ?? ""), $0["status"] as? String ?? "pending"] }]
        }
        if toolName == "TaskCreate" {
            return ["add": clip((input["subject"] ?? input["description"]) as? String ?? "")]
        }
        if toolName == "TaskUpdate", let id = input["taskId"] as? String ?? (input["taskId"] as? Int).map(String.init) {
            var o: [String: Any] = ["update": id]
            if let s = input["status"] as? String { o["status"] = s }
            if let s = input["subject"] as? String { o["text"] = clip(s) }
            return o
        }
        guard toolName.hasSuffix("update_plan") else { return nil }
        // Codex：plan[{step, status}]；桌面 App：items[{content, status}]，或者 advance / done / start。
        if let plan = (input["plan"] ?? input["items"]) as? [[String: Any]] {
            return ["set": plan.map { [clip(($0["step"] ?? $0["content"]) as? String ?? ""), $0["status"] as? String ?? "pending"] }]
        }
        var o: [String: Any] = [:]
        if input["advance"] as? Bool == true { o["advance"] = true }
        if let d = input["done"] as? [Int] { o["done"] = d }
        if let s = input["start"] as? Int { o["start"] = s }
        return o.isEmpty ? nil : o
    }

    /// 按操作更新清单。序号（done / start）从 1 开始；TaskCreate 的编号按创建顺序从 1 开始。
    mutating func apply(_ op: [String: Any]) {
        if let set = op["set"] as? [[String]] {
            items = set.compactMap { $0.count == 2 ? Item(text: $0[0], status: $0[1]) : nil }
            return
        }
        if let text = op["add"] as? String {
            items.append(Item(text: text, status: "pending"))
            return
        }
        if let id = op["update"] as? String, let i = Int(id), i >= 1, i <= items.count {
            if let s = op["status"] as? String { items[i - 1].status = s }
            if let t = op["text"] as? String { items[i - 1].text = t }
            return
        }
        if op["advance"] as? Bool == true, let i = items.firstIndex(where: { $0.status == "in_progress" }) {
            items[i].status = "completed"
            if let next = items[(i + 1)...].firstIndex(where: { $0.status == "pending" }) { items[next].status = "in_progress" }
        }
        for d in op["done"] as? [Int] ?? [] where d >= 1 && d <= items.count { items[d - 1].status = "completed" }
        if let s = op["start"] as? Int, s >= 1, s <= items.count {
            for i in items.indices where items[i].status == "in_progress" { items[i].status = "pending" }
            items[s - 1].status = "in_progress"
        }
    }
}
