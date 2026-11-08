using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ServerIsland.Core;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Settings;

/// 设置 → 计费：总开关、货币、每台调度系统服务器的分区单价（可以从服务器读取分区）。与 Swift 版 BillingSettingsView 对应。
public sealed class BillingPage(SettingsWindow window, SettingsKit k, ConfigStore store, MonitorEngine engine)
{
    static readonly string[] Currencies = ["¥", "$", "€", "£"];
    static readonly Color Tint = Color.Parse("#F59E0B");
    /// 正在读取分区的服务器、读取失败的原因（跨页面重建保留）。
    static readonly HashSet<Guid> Loading = [];
    static readonly Dictionary<Guid, string> Errors = [];

    void Touch() => store.Touch();

    public Control Build()
    {
        var col = new StackPanel();
        col.Children.Add(k.Text(L("计费", "Billing"), 22, FontWeight.Bold).Margin2(2, 0, 0, 16));
        var s = store.Settings;
        var rows = new List<Control?>
        {
            k.ToggleRow(L("估算费用", "Estimate Costs"),
                L("按你填的单价 × 作业分到的资源 × 运行时长估算花了多少钱。只算 Slurm / PBS 作业；运行中的作业会额外查一次分到的资源（每个作业一次）。结果是估算，和计算中心的账单可能有出入。",
                  "Price × allocated resources × run time. Slurm / PBS jobs only; each running job's allocation is queried once. This is an estimate and may differ from your computing center's bill."),
                s.BillingEnabled, v => { s.BillingEnabled = v; Touch(); window.RefreshPage(); }),
        };
        if (s.BillingEnabled)
        {
            rows.Add(k.Row(L("货币", "Currency"), null, k.Combo(Currencies.Select(c => (c, c)), s.Currency, v => { s.Currency = v; Touch(); window.RefreshPage(); }, 80)));
            var sum = new CostSummary(engine.CostEntriesWithRunning());
            rows.Add(k.Row(L("本月 / 上月", "This / last month"), null,
                k.Text(Billing.Money(sum.Month, s.Currency) + "  /  " + Billing.Money(sum.LastMonth, s.Currency), c: k.P.Secondary)));
            rows.Add(k.Row(new Control(), k.Button(L("清除费用记录…", "Clear Cost Records…"), async () =>
            {
                if (!await PromptDialog.Confirm(L("清除全部费用记录？", "Clear all cost records?"),
                        L("本机记下的费用都会删除（单价设置保留）。", "All cost records on this computer will be deleted. Prices are kept."),
                        L("清除", "Clear"), L("取消", "Cancel"))) return;
                engine.Costs.Clear();
                window.RefreshPage();
            })));
        }
        col.Children.Add(k.Section(L("计费", "Billing"), "card", Tint, rows, Billing.Disclaimer));

        if (!s.BillingEnabled) return col;
        var hosts = s.Hosts.Where(h => h.Kind.IsScheduler()).ToList();
        if (hosts.Count == 0)
            col.Children.Add(k.Caption(L("还没有 Slurm / PBS 服务器。直连服务器没有调度系统的分配记录，不计费。",
                                         "No Slurm / PBS servers yet. Direct servers have no scheduler allocation, so they aren't billed.")).Margin2(4, 0, 4, 0));
        foreach (var h in hosts) col.Children.Add(HostSection(h).Margin2(0, 18, 0, 0));
        return col;
    }

    Control HostSection(HostConfig host)
    {
        var rows = new List<Control?>();
        // "其他分区"总在第一行（还没有时临时补一行，填了价格才写进配置），后面是各分区。
        var fallback = host.Prices.FirstOrDefault(p => p.Partition.Length == 0) ?? new PartitionPrice("", PriceUnit.Gpu, 0);
        rows.Add(PriceRow(host, fallback, isDefault: true));
        foreach (var p in host.Prices.Where(p => p.Partition.Length > 0).ToList()) rows.Add(PriceRow(host, p, isDefault: false));

        var read = k.Button(host.Kind == HostKind.Pbs ? L("从服务器读取队列", "Read Queues from Server") : L("从服务器读取分区", "Read Partitions from Server"),
            () => _ = ReadPartitions(host));
        read.IsEnabled = !Loading.Contains(host.Id);
        ToolTip.SetTip(read, host.Kind == HostKind.Pbs ? L("只读：qstat -Q", "Read-only: qstat -Q") : L("只读：sinfo（和分区页同一个查询）", "Read-only: sinfo (same query as the Partitions page)"));
        var add = k.Button(L("添加分区", "Add Partition"), () =>
        {
            host.Prices.Add(new PartitionPrice(L("新分区", "new") + (host.Prices.Count > 0 ? $"{host.Prices.Count + 1}" : ""), PriceUnit.Gpu, 0));
            Touch();
            window.RefreshPage();
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { read, add } };
        if (Loading.Contains(host.Id)) actions.Children.Add(k.Caption(L("读取中…", "Reading…")).VAlign(VerticalAlignment.Center));
        if (Errors.TryGetValue(host.Id, out var err)) actions.Children.Add(k.Text("⚠ " + err, 11.5, c: Color.Parse("#E8A33D")).VAlign(VerticalAlignment.Center));
        rows.Add(actions);
        return k.Section(host.DisplayName + " · " + (host.Kind == HostKind.Pbs ? "PBS" : "Slurm"), "server", Color.Parse("#AF52DE"), rows,
            L("单价填 0 的分区不计费。没有单独设置的分区按“其他分区”计算。", "Partitions priced at 0 aren't billed. Partitions without their own row use “Other partitions”."));
    }

    Control PriceRow(HostConfig host, PartitionPrice p, bool isDefault)
    {
        // "其他分区"还不在配置里时，第一次修改才加进去
        void Commit()
        {
            if (!host.Prices.Contains(p)) host.Prices.Insert(0, p);
            Touch();
        }
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto,10,Auto,6,80,6,Auto,10,24") };
        Control name;
        if (isDefault) name = k.Text(L("其他分区", "Other partitions"));
        else
        {
            var f = k.Field(p.Partition, v => { p.Partition = v.Trim(); Commit(); }, L("分区名", "Partition"), mono: true);
            name = f;
        }
        var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (p.Note.Length > 0) left.Children.Add(k.Caption(p.Note));
        Place(g, left, 0);
        Place(g, k.Combo(Enum.GetValues<PriceUnit>().Select(u => (u, u.Label())), p.Unit, u => { p.Unit = u; Commit(); window.RefreshPage(); }, 130), 2);
        Place(g, k.Text(store.Settings.Currency, c: k.P.Secondary), 4);
        var price = k.Field(p.Price == 0 ? "" : p.Price.ToString("0.####", CultureInfo.InvariantCulture), v =>
        {
            if (v.Trim().Length == 0) { p.Price = 0; Commit(); return; }
            if (double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0) { p.Price = d; Commit(); }
        }, "0");
        price.TextAlignment = TextAlignment.Right;
        Place(g, price, 6);
        Place(g, k.Text("/" + p.Unit.PerLabel(), c: k.P.Secondary), 8);
        if (!isDefault)
        {
            var remove = k.Button("−", () => { host.Prices.Remove(p); Touch(); window.RefreshPage(); });
            ToolTip.SetTip(remove, L("删除这一行", "Remove this row"));
            Place(g, remove, 10);
        }
        return g;
    }

    static void Place(Grid g, Control c, int col)
    {
        c.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    /// 读到的分区补进列表（已有的只更新说明，不动单价）；有卡的分区默认按卡时，否则按核时。
    async Task ReadPartitions(HostConfig host)
    {
        Loading.Add(host.Id);
        Errors.Remove(host.Id);
        window.RefreshPage();
        var r = await engine.FetchBillingPartitions(host);
        Loading.Remove(host.Id);
        if (r.Error is { } e) Errors[host.Id] = e;
        else if (store.Host(host.Id) is { } h)
        {
            foreach (var item in r.List)
            {
                if (h.Prices.FirstOrDefault(p => p.Partition == item.Name) is { } existing) existing.Note = item.Note;
                else h.Prices.Add(new PartitionPrice(item.Name, item.HasGpu ? PriceUnit.Gpu : PriceUnit.Cpu, 0, item.Note));
            }
            if (r.List.Count == 0) Errors[host.Id] = L("没有读到分区", "No partitions found");
            Touch();
        }
        window.RefreshPage();
    }
}
