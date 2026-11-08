using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ServerIsland.Core;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;
using static ServerIsland.Desktop.Ui.Kit;

namespace ServerIsland.Desktop.Island;

/// 费用页（打开计费后，头部的钱币按钮进来）：今天 / 本月 / 上月、按天、按服务器和分区、最贵的作业。全部是估算。
/// 与 Swift 版 CostPage.swift 对应。
public sealed partial class IslandPages
{
    /// 预览图用：固定"现在"，让每天的柱子和截图日期无关。
    public static DateTime? CostPreviewNow;

    string Cur => engine.Store.Settings.Currency;

    public Control CostPage()
    {
        var s = new CostSummary(engine.CostEntriesWithRunning(), CostPreviewNow);
        var rate = engine.TotalBurnRate;
        var header = PageHeader(L("费用", "Costs"),
            L("估算 · 按你设置的单价和调度系统分配的资源计算", "Estimate · from your prices and the resources the scheduler allocated"), Back,
            IconButton("sliders", L("设置单价", "Set prices"), () => st.OpenSettingsPage("billing")));

        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,*,8,*") };
        Control Tile(string label, string value, Color c, int col)
        {
            var v = V(2, T(label, 10, FontWeight.Medium, Hue.Tertiary), T(value, 16, FontWeight.SemiBold, c));
            var card = RoundedCard(v, 10, Hue.Card, 0).WithPadding(10, 7);
            Grid.SetColumn(card, col);
            tiles.Children.Add(card);
            return card;
        }
        Tile(L("今天", "Today"), Billing.Money(s.Today, Cur), Hue.Text, 0);
        Tile(L("本月", "This month"), Billing.Money(s.Month, Cur), Hue.Mint, 2);
        Tile(L("上月", "Last month"), Billing.Money(s.LastMonth, Cur), Hue.Secondary, 4);
        Tile(L("运行中", "Running now"), Billing.Money(rate, Cur) + L("/小时", "/h"), rate > 0 ? Hue.Amber : Hue.Tertiary, 6);

        var list = V(5);
        if (s.Groups.Count == 0)
            list.Children.Add(Centered(L("本月还没有费用：正在跑的任务每次刷新时按已运行的时长算，结束后按当时的单价记账（ServerIsland 开着才记得到）。",
                                         "No costs this month yet. Running jobs count up at every refresh; finished jobs are recorded at their price (while ServerIsland is running)."), 18));
        else
        {
            list.Children.Add(SectionTitle(L("按服务器和分区", "By server and partition")));
            var max = s.Groups[0].Cost;
            foreach (var g in s.Groups) list.Children.Add(GroupRow(g, max));
            list.Children.Add(SectionTitle(L("本月最贵的作业", "Most expensive jobs this month")).Margin(0, 4, 0, 0));
            foreach (var t in s.Top) list.Children.Add(TopRow(t));
        }
        var note = H(5, new Icon("info", 10, Hue.Tertiary.B(), 2), T(Billing.Disclaimer, 9.5, color: Hue.Tertiary)).Tip(Billing.Disclaimer);
        return Rows("Auto,10,Auto,10,Auto,10,*,6,Auto", header, tiles, DayChart(s.Days), Scroll(list), note);
    }

    string HostName(Guid id) => engine.Store.Host(id)?.DisplayName ?? L("已删除的服务器", "Removed server");

    static Control SectionTitle(string t) => T(t, 10.5, FontWeight.SemiBold, Hue.Secondary).Margin(2, 0, 0, 0);

    Control DayChart(List<double> days)
    {
        var top = Math.Max(days.Count > 0 ? days.Max() : 0, 0.0001);
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 30, VerticalAlignment = VerticalAlignment.Bottom };
        for (int i = 0; i < days.Count; i++)
        {
            var v = days[i];
            bars.Children.Add(new Border
            {
                Width = 14, Height = Math.Max(2, 30 * v / top), CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Bottom,
                Background = v > 0 ? Hue.Progress : Hue.Track.B(),
            }.Tip(L($"{i + 1} 日：", $"Day {i + 1}: ") + Billing.Money(v, Cur)));
        }
        var head = Row(6, T(L("本月每天", "Daily this month"), 10, FontWeight.Medium, Hue.Tertiary), Spacer,
            days.Count > 0 && days.Max() > 0 ? T(L("最高 ", "peak ") + Billing.Money(top, Cur), 9.5, color: Hue.Tertiary) : null);
        return RoundedCard(V(4, head, bars), 10, Hue.Card, 0).WithPadding(10, 7);
    }

    Control GroupRow(CostSummary.Group g, double max)
    {
        var amount = T(Billing.Money(g.Cost, Cur), 11.5, FontWeight.SemiBold).W(76);
        amount.TextAlignment = TextAlignment.Right;
        var row = Row(10, T(HostName(g.Host), 11.5, FontWeight.Medium), T(g.Partition.Length == 0 ? "—" : g.Partition, 10, color: Hue.Tertiary, mono: true),
            Spacer, T($"{g.Hours:0.0} " + g.Unit.PerLabel(), 10, color: Hue.Tertiary),
            new SlimBar { Fraction = max > 0 ? g.Cost / max : 0, BarHeight = 4 }.W(90), amount);
        return RoundedCard(row, 9, Hue.Card, 0).WithPadding(10, 6);
    }

    Control TopRow(CostSummary.TopJob t)
    {
        var amount = T(Billing.Money(t.Cost, Cur), 11.5, FontWeight.SemiBold, Hue.Amber).W(76);
        amount.TextAlignment = TextAlignment.Right;
        var left = new ShrinkRow(10, 0, T(t.Name, 11.5, FontWeight.Medium), T(t.Id, 9.5, color: Hue.Tertiary, mono: true),
            t.Elements > 0 ? T(L($"数组 {t.Elements} 个元素", $"array · {t.Elements} elements"), 9.5, color: Hue.Tertiary) : null);
        var row = Row(10, Fill(left), T(HostName(t.Host) + (t.Partition.Length == 0 ? "" : " · " + t.Partition), 10, color: Hue.Tertiary), amount);
        return RoundedCard(row, 9, Hue.Card, 0).WithPadding(10, 6);
    }

    /// 详情页的费用："约 ¥12.40 · ¥4.00/小时 · 4 卡 × ¥1.00/卡时"。没打开计费、没有单价或不知道资源量时为 null。
    string? CostText(HostConfig host, RemoteJob job)
    {
        if (engine.JobCost(host, job) is not { } c) return null;
        var parts = new List<string> { L("约 ", "≈ ") + Billing.Money(c.Spent, Cur) };
        if (c.PerHour > 0) parts.Add(Billing.Money(c.PerHour, Cur) + L("/小时", "/h"));
        if (c.Detail.Length > 0) parts.Add(c.Detail);
        return string.Join(" · ", parts);
    }
}
