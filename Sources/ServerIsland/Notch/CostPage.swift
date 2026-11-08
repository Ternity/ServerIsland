import SwiftUI

/// 费用页（打开计费后，头部的钱币按钮进来）：今天 / 本月 / 上月、按天、按服务器和分区、最贵的作业。全部是估算。
struct CostPage: View {
    @ObservedObject var engine: MonitorEngine
    @ObservedObject var store: ConfigStore
    var onBack: () -> Void
    var onOpenSettings: () -> Void
    /// 预览渲染用：给定账本和"现在"。
    var preloaded: [CostEntry]?
    var now: Date?

    private var cur: String { store.settings.currency }

    var body: some View {
        let s = CostSummary(preloaded ?? engine.costEntriesWithRunning(), now: now ?? Date())
        let rate = engine.totalBurnRate
        VStack(alignment: .leading, spacing: 10) {
            PageHeader(title: L("费用", "Costs"),
                       subtitle: L("估算 · 按你设置的单价和调度系统分配的资源计算", "Estimate · from your prices and the resources the scheduler allocated"),
                       onBack: onBack) {
                IslandIconButton(systemName: "slider.horizontal.3", help: L("设置单价", "Set prices"), action: onOpenSettings)
            }
            HStack(spacing: 8) {
                tile(L("今天", "Today"), Billing.money(s.today, currency: cur), Theme.text)
                tile(L("本月", "This month"), Billing.money(s.month, currency: cur), Theme.mint)
                tile(L("上月", "Last month"), Billing.money(s.lastMonth, currency: cur), Theme.secondary)
                tile(L("运行中", "Running now"), Billing.money(rate, currency: cur) + L("/小时", "/h"), rate > 0 ? Theme.amber : Theme.tertiary)
            }
            dayChart(s.days)
            MaybeScroll(showsIndicators: false) {
                VStack(alignment: .leading, spacing: 5) {
                    if s.groups.isEmpty {
                        Text(L("本月还没有费用：正在跑的任务每次刷新时按已运行的时长算，结束后按当时的单价记账（ServerIsland 开着才记得到）。",
                               "No costs this month yet. Running jobs count up at every refresh; finished jobs are recorded at their price (while ServerIsland is running)."))
                            .font(Theme.rounded(11)).foregroundStyle(Theme.tertiary)
                            .frame(maxWidth: .infinity).padding(.top, 18)
                    } else {
                        sectionTitle(L("按服务器和分区", "By server and partition"))
                        let maxCost = s.groups.first?.cost ?? 1
                        ForEach(s.groups) { groupRow($0, max: maxCost) }
                        sectionTitle(L("本月最贵的作业", "Most expensive jobs this month")).padding(.top, 4)
                        ForEach(s.top, id: \.key) { topRow($0) }
                    }
                }
            }
            .frame(maxHeight: .infinity)
            Label(Billing.disclaimer, systemImage: "info.circle")
                .font(Theme.rounded(9.5)).foregroundStyle(Theme.tertiary)
                .lineLimit(1).truncationMode(.tail).help(Billing.disclaimer)
        }
    }

    private func hostName(_ id: UUID) -> String { store.host(id)?.displayName ?? L("已删除的服务器", "Removed server") }

    private func tile(_ label: String, _ value: String, _ c: Color) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(Theme.rounded(10, .medium)).foregroundStyle(Theme.tertiary)
            Text(value).font(Theme.rounded(16, .semibold).monospacedDigit()).foregroundStyle(c)
                .lineLimit(1).minimumScaleFactor(0.6)
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Theme.card))
    }

    private func dayChart(_ days: [Double]) -> some View {
        let top = max(days.max() ?? 0, 0.0001)
        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(L("本月每天", "Daily this month")).font(Theme.rounded(10, .medium)).foregroundStyle(Theme.tertiary)
                Spacer()
                if (days.max() ?? 0) > 0 {
                    Text(L("最高 ", "peak ") + Billing.money(top, currency: cur)).font(Theme.rounded(9.5).monospacedDigit()).foregroundStyle(Theme.tertiary)
                }
            }
            HStack(alignment: .bottom, spacing: 2) {
                ForEach(Array(days.enumerated()), id: \.offset) { i, v in
                    RoundedRectangle(cornerRadius: 1.5, style: .continuous)
                        .fill(v > 0 ? AnyShapeStyle(Theme.progress) : AnyShapeStyle(Theme.track))
                        .frame(height: max(2, 30 * v / top))
                        .frame(maxWidth: 14)
                        .help(L("\(i + 1) 日：", "Day \(i + 1): ") + Billing.money(v, currency: cur))
                }
                Spacer(minLength: 0)
            }
            .frame(height: 30, alignment: .bottom)
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Theme.card))
    }

    private func sectionTitle(_ t: String) -> some View {
        Text(t).font(Theme.rounded(10.5, .semibold)).foregroundStyle(Theme.secondary).padding(.leading, 2)
    }

    private func groupRow(_ g: CostSummary.Group, max: Double) -> some View {
        HStack(spacing: 10) {
            Text(hostName(g.host)).font(Theme.rounded(11.5, .medium)).foregroundStyle(Theme.text).lineLimit(1)
            Text(g.partition.isEmpty ? "—" : g.partition).font(Theme.mono(10)).foregroundStyle(Theme.tertiary).lineLimit(1)
            Spacer(minLength: 6)
            Text(String(format: "%.1f ", g.hours) + g.unit.perLabel).font(Theme.rounded(10).monospacedDigit()).foregroundStyle(Theme.tertiary)
            SlimBar(fraction: max > 0 ? g.cost / max : 0, fill: AnyShapeStyle(Theme.progress), height: 4).frame(width: 90)
            Text(Billing.money(g.cost, currency: cur)).font(Theme.rounded(11.5, .semibold).monospacedDigit()).foregroundStyle(Theme.text)
                .frame(width: 76, alignment: .trailing)
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.card))
    }

    private func topRow(_ t: CostSummary.TopJob) -> some View {
        HStack(spacing: 10) {
            Text(t.name).font(Theme.rounded(11.5, .medium)).foregroundStyle(Theme.text).lineLimit(1)
            Text(t.id).font(Theme.mono(9.5)).foregroundStyle(Theme.tertiary)
            if t.elements > 0 {
                Text(L("数组 \(t.elements) 个元素", "array · \(t.elements) elements")).font(Theme.rounded(9.5)).foregroundStyle(Theme.tertiary)
            }
            Spacer(minLength: 6)
            Text(hostName(t.host) + (t.partition.isEmpty ? "" : " · " + t.partition)).font(Theme.rounded(10)).foregroundStyle(Theme.tertiary).lineLimit(1)
            Text(Billing.money(t.cost, currency: cur)).font(Theme.rounded(11.5, .semibold).monospacedDigit()).foregroundStyle(Theme.amber)
                .frame(width: 76, alignment: .trailing)
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Theme.card))
    }
}
