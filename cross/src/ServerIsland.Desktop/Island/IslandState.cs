using ServerIsland.Core;

namespace ServerIsland.Desktop.Island;

public enum PageKind { List, Job, History, Picker, Jobs, Partitions, Costs }

/// 展开面板里的二级页面。
public sealed record IslandPage(PageKind Kind, Guid Host = default, string Job = "")
{
    public static readonly IslandPage List = new(PageKind.List);
}

/// 刘海的界面状态（悬停、当前页面、固定……），以及各页面需要跨重建保存的状态。
public sealed class IslandState
{
    public bool Hovering;
    public bool Pinned;
    /// 合并的空闲机器是否临时展开。
    public bool ShowIdle;
    public NotchGeometry Geometry = NotchGeometry.Virtual;
    /// 桌宠模式：面板浮在小章鱼旁边，不和屏幕顶边衔接。
    public bool Floating;
    /// 打开详情时的任务快照（任务结束后仍能看到详情）。
    public RemoteJob? PinnedJob;
    /// 菜单 / 右键菜单打开期间鼠标会移到形状外，此时不要收起。
    public int MenusOpen;
    /// 卡片列表可以滚动时的位置（重建后恢复）。
    public Avalonia.Vector ListOffset;
    /// 全部任务页的筛选条件（打开详情再返回时保留）。
    public JobsFilter Jobs = new();
    /// 详情页"返回"回到哪里（从全部任务页打开时回到那里）。
    public IslandPage? ReturnPage;

    /// 打开全部任务页：从卡片进来时只看那台服务器，从头部进来时看全部。
    public void OpenAllJobs(Guid? host = null)
    {
        Jobs = new JobsFilter(Host: host);
        Page = new IslandPage(PageKind.Jobs);
    }

    IslandPage _page = IslandPage.List;
    public IslandPage Page
    {
        get => _page;
        set
        {
            if (_page == value) return;
            _page = value;
            PageState?.Dispose();
            PageState = null;
            PageChanged?.Invoke();
        }
    }

    /// 当前二级页面的状态（详情页的日志、历史列表……），换页时释放。
    public PageStateBase? PageState;

    public event Action? PageChanged;
    public Action OpenSettings = () => { };
    /// 打开设置并切到某一页（如费用页的"设置单价"："billing"）。
    public Action<string> OpenSettingsPage = _ => { };
    public Action Unpinned = () => { };
    public Action RequestRebuild = () => { };

    public double ContentHeight(List<(HostConfig Host, HostSnapshot Snap)> hosts, bool fold) =>
        Page.Kind == PageKind.List ? IslandLayout.ContentHeight(hosts, fold, ShowIdle) : IslandLayout.PageHeight;

    public T StateFor<T>(Func<T> create) where T : PageStateBase
    {
        if (PageState is T t) return t;
        PageState?.Dispose();
        var s = create();
        PageState = s;
        return s;
    }
}

public abstract class PageStateBase : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;
    public bool Disposed => _cts.IsCancellationRequested;
    public virtual void Dispose() => _cts.Cancel();
}

public enum CancelStageKind { Idle, Confirm, Running, Result }

public sealed class JobDetailState : PageStateBase
{
    public JobDetail? Detail;
    public bool Loading;
    public bool ShowStderr;
    /// 详情页的小曲线（VASP 结构优化有能量和最大力两张，并排显示）。
    public List<AppCurve> Curves = [];
    public CancelStageKind Stage = CancelStageKind.Idle;
    public bool Force;
    public CancelOutcome? Outcome;
    public bool WasTerm;
    public bool Started;
    public int LastLogCount = -1;
    public Avalonia.Vector LogOffset;
}

public sealed class HistoryState : PageStateBase
{
    public List<HistoryEntry>? Entries;
    public bool FromServer;
    public bool Started;
}

public sealed class PickerState : PageStateBase
{
    public List<RemoteJob>? Procs;
    public bool Started;
}

public sealed class PartitionsState : PageStateBase
{
    public List<PartitionStat>? Stats;
    public DateTime At = DateTime.Now;
    public string? Error;
    public bool Loading;
    public bool Started;
}

public enum JobsStatus { All, Running, Pending, Array }

/// 全部任务页的筛选条件。放在 IslandState 里：从这里打开详情再返回时保持不变。
public sealed record JobsFilter(string Query = "", Guid? Host = null, JobsStatus Status = JobsStatus.All, bool StarredOnly = false)
{
    public bool Matches(RemoteJob j, bool starred, string hostName)
    {
        if (StarredOnly && !starred) return false;
        switch (Status)
        {
            case JobsStatus.Running when j.RunningUnits == 0: return false;
            case JobsStatus.Pending when j.PendingUnits == 0: return false;
            case JobsStatus.Array when j.Array == null: return false;
        }
        var q = Query.Trim().ToLowerInvariant();
        if (q.Length == 0) return true;
        return j.Name.ToLowerInvariant().Contains(q) || j.Id.ToLowerInvariant().Contains(q) || j.Detail.ToLowerInvariant().Contains(q)
            || hostName.ToLowerInvariant().Contains(q) || (j.Workdir?.ToLowerInvariant().Contains(q) ?? false);
    }
}

public sealed class AllJobsState : PageStateBase
{
    public Avalonia.Controls.TextBox? SearchBox;
    public Avalonia.Vector ScrollOffset;
}
