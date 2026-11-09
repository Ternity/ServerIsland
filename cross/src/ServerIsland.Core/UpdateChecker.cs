using System.Net.Http;
using System.Text.Json;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// 检查更新：读 GitHub 上最新的 Release（`releases/latest`，不含草稿和预发布），和当前版本号比较（和 Mac 版 UpdateChecker.swift 一致）。
/// 只读一个公开的接口，不上传任何信息。用户点"更新"后由 UpdateInstaller 下载、核对、替换并重新启动；做不到时改为打开下载页面。
public static class UpdateChecker
{
    /// Digest：安装包的 SHA-256（GitHub 给每个附件的 `digest`，"sha256:…" 去掉前缀）。没有时不自动安装。
    public sealed record Release(string Version, string PageUrl, string DownloadUrl, string Notes, string? Digest = null, long? Size = null);

    /// Downloading / Installing / InstallFailed：自动更新（下载并核对中 / 已替换，马上重新启动 / 失败，Message 是原因）。
    public enum StateKind { Idle, Checking, UpToDate, Available, Failed, Downloading, Installing, InstallFailed }

    public static StateKind State { get; private set; } = StateKind.Idle;
    /// UpToDate 时是 GitHub 上的最新版本号；Failed / InstallFailed 时是原因。
    public static string Message { get; private set; } = "";
    public static Release? Latest { get; private set; }
    public static DateTime? LastChecked { get; private set; }
    /// 下载进度（0～1；不知道总大小时为 null）。
    public static double? Progress { get; private set; }
    /// 状态变化（可能在后台线程上触发）。
    public static event Action? Changed;

    public const string ApiUrl = "https://api.github.com/repos/Ternity/ServerIsland/releases/latest";
    /// 当前版本，由程序启动时设置（`AppInfo.Version`）。
    public static string CurrentVersion { get; set; } = "dev";

    /// 当前平台的安装包后缀。
    public static string AssetSuffix =>
        OperatingSystem.IsWindows() ? "win-x64.zip" : OperatingSystem.IsLinux() ? "linux-x64.tar.gz" : ".dmg";

    static void Set(StateKind s, string message = "", Release? r = null)
    {
        State = s; Message = message; Latest = r;
        Changed?.Invoke();
    }

    /// 有新版本时返回它。
    public static async Task<Release?> CheckAsync()
    {
        if (State is StateKind.Downloading or StateKind.Installing) return null;
        if (CurrentVersion == "dev" || CurrentVersion.StartsWith("0.0.0"))
        {
            Set(StateKind.Failed, L("开发版本，不检查更新", "Development build; not checking"));
            return null;
        }
        Set(StateKind.Checking);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ServerIsland/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync(ApiUrl);
            LastChecked = DateTime.Now;
            var code = (int)resp.StatusCode;
            if (code != 200)
            {
                Set(StateKind.Failed, code == 404 ? L("还没有发布任何版本", "No releases yet")
                                    : code == 403 ? L("GitHub 暂时限制了访问，请稍后再试", "GitHub rate limit; try again later")
                                    : L($"GitHub 返回 {code}", $"GitHub returned {code}"));
                return null;
            }
            if (Parse(await resp.Content.ReadAsStringAsync(), AssetSuffix) is not { } r)
            {
                Set(StateKind.Failed, L("看不懂 GitHub 的返回", "Unexpected response from GitHub"));
                return null;
            }
            if (Compare(r.Version, CurrentVersion) > 0)
            {
                DebugLog.Log($"检查更新：有新版本 {r.Version}（当前 {CurrentVersion}）");
                Set(StateKind.Available, "", r);
                return r;
            }
            Set(StateKind.UpToDate, r.Version);
            return null;
        }
        catch (Exception e)
        {
            Set(StateKind.Failed, L("连不上 GitHub：", "Can't reach GitHub: ") + e.Message);
            return null;
        }
    }

    /// GitHub 的 Release JSON：tag_name（"v0.2.0"）、html_url、assets[].browser_download_url、body。
    public static Release? Parse(string json, string assetSuffix)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) return null;
            if (!root.TryGetProperty("html_url", out var pageEl) || pageEl.GetString() is not { } page) return null;
            var version = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
            var download = page;
            string? digest = null;
            long? size = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && n.GetString()?.ToLowerInvariant().EndsWith(assetSuffix) == true
                        && a.TryGetProperty("browser_download_url", out var u) && u.GetString() is { } url)
                    {
                        download = url;
                        if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { } ds && ds.StartsWith("sha256:"))
                            digest = ds[7..].ToLowerInvariant();
                        if (a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes)) size = bytes;
                        break;
                    }
            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            return new Release(version, page, download, Summary(body), digest, size);
        }
        catch { return null; }
    }

    /// 只给离屏预览图用。
    public static void SetPreviewState(StateKind s, string message = "", Release? r = null, double? progress = null)
    {
        Progress = progress;
        Set(s, message, r);
    }

    /// 自动更新到这个版本。成功时返回新程序的路径，由界面启动它（带上 `--after-update <本进程 PID>`）并退出。
    public static async Task<string?> InstallAsync(Release r)
    {
        if (State is StateKind.Downloading or StateKind.Installing) return null;
        if (UpdateInstaller.UnsupportedReason(r) is { } why) { Set(StateKind.InstallFailed, why, r); return null; }
        Progress = null;
        Set(StateKind.Downloading, "", r);
        try
        {
            // 进度每变 1% 才通知一次（界面和托盘菜单会重画）
            var staged = await UpdateInstaller.DownloadAndStageAsync(r, p =>
            {
                var changed = p is { } v && (Progress is not { } old || (int)(v * 100) != (int)(old * 100));
                Progress = p;
                if (changed) Changed?.Invoke();
            });
            var exe = UpdateInstaller.Apply(staged, UpdateInstaller.InstallDirectory!);
            Set(StateKind.Installing, "", r);
            return exe;
        }
        catch (Exception e)
        {
            DebugLog.Log("自动更新失败：" + e.Message);
            Set(StateKind.InstallFailed, e.Message, r);
            return null;
        }
    }

    /// 更新说明摘要：优先取列表项（- / *），跳过标题、HTML、图片和空行，最多 4 行。
    public static string Summary(string body)
    {
        var lines = body.Replace("\r", "").Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('<') && !l.StartsWith("![") && !l.StartsWith("---")).ToList();
        var bullets = lines.Where(l => l.StartsWith("- ") || l.StartsWith("* ")).ToList();
        return string.Join("\n", (bullets.Count > 0 ? bullets : lines).Take(4));
    }

    /// 按数字逐段比较版本号："0.10.0" > "0.9.1"；"0.2" == "0.2.0"；"-beta" 之类的后缀忽略。
    public static int Compare(string a, string b)
    {
        static int[] Parts(string s) => s.Split('-')[0].Split('.')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        var x = Parts(a); var y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p < q ? -1 : 1;
        }
        return 0;
    }
}
