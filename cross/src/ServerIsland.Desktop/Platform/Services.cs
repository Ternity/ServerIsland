using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Platform;

/// 打开网址 / 文件 / 终端。
public static class Shell
{
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch (Exception e) { DebugLog.Log("打开失败：" + e.Message); }
    }

    public static void OpenFile(string path) => OpenUrl(path);

    /// 在文件管理器里显示（并选中）这个文件。
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else Process.Start("xdg-open", Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception e) { DebugLog.Log("打开失败：" + e.Message); }
    }

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, exe)));

    /// 在终端里运行一条命令（argv 形式）。Windows 优先用 Windows Terminal；Linux 依次尝试常见终端。
    public static bool RunInTerminal(IReadOnlyList<string> argv, string title = "ServerIsland")
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (OnPath("wt.exe"))
                {
                    var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                    psi.ArgumentList.Add("--title"); psi.ArgumentList.Add(title);
                    foreach (var a in argv) psi.ArgumentList.Add(a);
                    Process.Start(psi);
                    return true;
                }
                // 没有 Windows Terminal：新开一个控制台窗口
                var exe = argv[0] == "ssh" ? OpenSshTransport.SshPath : argv[0];
                var psi2 = new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = string.Join(" ", argv.Skip(1).Select(WinQuote)) };
                Process.Start(psi2);
                return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                var file = Path.Combine(Path.GetTempPath(), $"serverisland-{Guid.NewGuid().ToString("N")[..8]}.command");
                File.WriteAllText(file, "#!/bin/bash\n" + string.Join(" ", argv.Select(RemoteScript.ShellQuote)) + "\n");
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Process.Start("open", file);
                return true;
            }
            (string Exe, string[] Prefix)[] terminals =
            [
                ("x-terminal-emulator", ["-e"]), ("gnome-terminal", ["--"]), ("konsole", ["-e"]), ("xfce4-terminal", ["-x"]),
                ("kitty", []), ("alacritty", ["-e"]), ("wezterm", ["start", "--"]), ("tilix", ["-e"]), ("xterm", ["-e"]),
            ];
            foreach (var (term, prefix) in terminals)
            {
                if (!OnPath(term)) continue;
                var psi = new ProcessStartInfo(term) { UseShellExecute = false };
                foreach (var a in prefix) psi.ArgumentList.Add(a);
                foreach (var a in argv) psi.ArgumentList.Add(a);
                Process.Start(psi);
                return true;
            }
        }
        catch (Exception e) { DebugLog.Log("打开终端失败：" + e.Message); }
        return false;
    }

    /// Windows 命令行参数的引号规则（CommandLineToArgvW）。
    static string WinQuote(string a)
    {
        if (a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '"')) return a;
        var sb = new System.Text.StringBuilder("\"");
        int bs = 0;
        foreach (var c in a)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
            sb.Append('\\', bs); bs = 0; sb.Append(c);
        }
        sb.Append('\\', bs * 2).Append('"');
        return sb.ToString();
    }

    /// 终端里 ssh 到这台机器的参数。Linux：私有配置 + 复用主连接；Windows 的系统 ssh 没有私有配置，
    /// 手动填写的地址和跳板机直接写成 -p / -i / -J。
    static List<string> TargetArgs(HostConfig host)
    {
        if (OpenSshTransport.SupportsControlMaster)
            return [.. OpenSshTransport.ConfigArgs(host), "-o", $"ControlPath={OpenSshTransport.ControlPathFor(host)}", OpenSshTransport.TargetFor(host)];
        var args = new List<string>();
        static string Spec(HostConfig h)
        {
            if (!h.IsManual) return h.Alias;
            var (user, addr) = SshHosts.SplitAddress(h);
            return (user.Length > 0 ? user + "@" : "") + addr + (h.Port is { } p && p != 22 ? ":" + p : "");
        }
        var chain = SshHosts.JumpChain(host);
        if (chain.Count > 0) args.AddRange(["-J", string.Join(",", Enumerable.Reverse(chain).Select(Spec))]);
        if (!host.IsManual) { args.Add(host.Alias); return args; }
        var (u, a) = SshHosts.SplitAddress(host);
        if (host.Port is { } port) args.AddRange(["-p", port.ToString()]);
        if (host.IdentityFile.Trim().Length > 0) args.AddRange(["-i", SshConfig.ExpandTilde(host.IdentityFile.Trim())]);
        args.Add((u.Length > 0 ? u + "@" : "") + a);
        return args;
    }

    /// 在终端里 ssh 到某个目录（详情页"在终端中打开"）。目录来自服务器返回的数据，用单引号转义。
    /// directory 为 null 时直接登录（卡片上的终端按钮）。
    public static void OpenShellInTerminal(HostConfig host, string? directory = null)
    {
        var argv = new List<string> { "ssh", "-t" };
        argv.AddRange(TargetArgs(host));
        if (directory != null) argv.Add("cd " + RemoteScript.ShellQuote(directory) + " && exec $SHELL -l");
        RunInTerminal(argv, host.DisplayName);
    }

    /// 在终端里登录一次（只用于系统 ssh + ControlMaster），验证成功后主连接转入后台并保持。
    public static void OpenLoginInTerminal(HostConfig host)
    {
        OpenSshTransport.EnsureControlDirectory();
        var ssh = string.Join(" ", OpenSshTransport.ConfigArgs(host).Concat(OpenSshTransport.KeepAliveOptions(host)).Select(RemoteScript.ShellQuote))
                  + $" -o ControlMaster=yes -o ControlPath={RemoteScript.ShellQuote(OpenSshTransport.ControlPathFor(host))} -fN {RemoteScript.ShellQuote(OpenSshTransport.TargetFor(host))}";
        var script = $"""
            clear
            echo {RemoteScript.ShellQuote("ServerIsland · " + L("登录", "Log in to") + " " + host.DisplayName)}
            echo {RemoteScript.ShellQuote(L($"验证成功后连接会在后台保持（{host.KeepAlive.Label()}），ServerIsland 将复用它查询状态。", $"After you log in, the connection is kept for {host.KeepAlive.Label()} and reused by ServerIsland."))}
            echo
            if ssh {ssh}; then
              echo; echo {RemoteScript.ShellQuote("✓ " + L("已连接，可以关闭这个窗口。", "Connected. You can close this window."))}
            else
              echo; echo {RemoteScript.ShellQuote("✗ " + L("登录失败。", "Login failed."))}
            fi
            read -r -p {RemoteScript.ShellQuote(L("按回车关闭", "Press Enter to close"))} _
            """;
        RunInTerminal(["bash", "-c", script], host.DisplayName);
    }
}

/// 提示音：成功 / 失败 / 提醒三种（与 Mac 版同一组 WAV）。音量通过直接缩放采样实现。
public static class Sound
{
    static DateTime _lastPlayed = DateTime.MinValue;
    static readonly Dictionary<string, byte[]> Cache = [];
    static byte[]? _playing;   // Windows 异步播放期间要保持缓冲区存活

    public static void Play(string kind, double volume)
    {
        // 短时间内多个事件只响一次。
        if ((DateTime.Now - _lastPlayed).TotalSeconds <= 1.2) return;
        _lastPlayed = DateTime.Now;
        PlayNow(kind, volume);
    }

    /// 设置页试听，不受节流限制。
    public static void Preview(string kind, double volume)
    {
        _lastPlayed = DateTime.MinValue;
        Play(kind, volume);
    }

    static byte[]? Load(string kind)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(kind, out var b)) return b;
            try
            {
                using var s = AssetLoader.Open(new Uri($"avares://ServerIsland/Assets/{kind}.wav"));
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                Cache[kind] = b = ms.ToArray();
                return b;
            }
            catch { return null; }
        }
    }

    /// 16 位 PCM：把 data 段的每个采样乘以音量。
    static byte[] Scaled(byte[] wav, double volume)
    {
        var outBytes = (byte[])wav.Clone();
        volume = Math.Clamp(volume, 0, 1);
        int pos = 12;
        int bits = 16;
        while (pos + 8 <= outBytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(outBytes, pos, 4);
            int size = BitConverter.ToInt32(outBytes, pos + 4);
            if (id == "fmt ") bits = BitConverter.ToInt16(outBytes, pos + 8 + 14);
            if (id == "data")
            {
                if (bits != 16) break;
                int end = Math.Min(outBytes.Length, pos + 8 + size);
                for (int i = pos + 8; i + 1 < end; i += 2)
                {
                    var v = (short)(BitConverter.ToInt16(outBytes, i) * volume);
                    outBytes[i] = (byte)(v & 0xFF);
                    outBytes[i + 1] = (byte)((v >> 8) & 0xFF);
                }
                break;
            }
            pos += 8 + size + (size & 1);
        }
        return outBytes;
    }

    [DllImport("winmm.dll", SetLastError = true)] static extern bool PlaySound(byte[] data, IntPtr hmod, uint flags);
    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    static void PlayNow(string kind, double volume)
    {
        if (Load(kind) is not { } wav) return;
        var data = Scaled(wav, volume);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _playing = data;
                PlaySound(_playing, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
                return;
            }
            var file = Path.Combine(Path.GetTempPath(), $"serverisland-{kind}.wav");
            File.WriteAllBytes(file, data);
            if (OperatingSystem.IsMacOS()) { Process.Start("afplay", file); return; }
            foreach (var player in new[] { "paplay", "pw-play", "aplay" })
            {
                try
                {
                    var psi = new ProcessStartInfo(player) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                    if (player == "aplay") psi.ArgumentList.Add("-q");
                    psi.ArgumentList.Add(file);
                    Process.Start(psi);
                    return;
                }
                catch { }
            }
        }
        catch (Exception e) { DebugLog.Log("播放提示音失败：" + e.Message); }
    }
}

/// 登录时自动启动。Windows：注册表 Run 键；Linux：~/.config/autostart。
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string Exe => Environment.ProcessPath ?? "ServerIsland";
    static string DesktopFile => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(AppPaths.Home, ".config"), "autostart", "serverisland.desktop");

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("ServerIsland") is string;
                if (OperatingSystem.IsLinux()) return File.Exists(DesktopFile);
            }
            catch { }
            return false;
        }
    }

    public static string? Set(bool on)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) key.SetValue("ServerIsland", $"\"{Exe}\"");
                else key.DeleteValue("ServerIsland", false);
                return null;
            }
            if (OperatingSystem.IsLinux())
            {
                if (on)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
                    File.WriteAllText(DesktopFile, $"[Desktop Entry]\nType=Application\nName=ServerIsland\nExec=\"{Exe}\"\nX-GNOME-Autostart-enabled=true\n");
                }
                else File.Delete(DesktopFile);
                return null;
            }
            return L("这个系统上不支持", "Not supported on this system");
        }
        catch (Exception e) { return e.Message; }
    }
}

/// 系统通知。Linux 用 notify-send；Windows 上未打包的程序发不了原生通知，用刘海弹窗 + 提示音代替。
public static class Notifier
{
    public static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static void Post(IslandEvent ev, bool silent)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
                psi.ArgumentList.Add("--app-name=ServerIsland");
                if (ev.Kind == EventKind.Failed) psi.ArgumentList.Add("--urgency=critical");
                psi.ArgumentList.Add(ev.Title);
                psi.ArgumentList.Add(ev.HostName + (ev.Subtitle.Length > 0 ? " · " + ev.Subtitle : ""));
                Process.Start(psi);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var script = $"display notification {AppleQuote(ev.HostName + " · " + ev.Subtitle)} with title {AppleQuote(ev.Title)}";
                Process.Start("osascript", ["-e", script]);
            }
        }
        catch { }
    }

    static string AppleQuote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// 电脑是否锁屏（副屏跟着熄屏用）。在后台线程上调用，每 3 秒一次，要轻。
/// Windows：锁屏时有 LogonUI.exe 在运行；Linux：logind 会话的 LockedHint（GNOME、KDE 等会设置）；macOS 试用版：不检测。
public static class LockState
{
    public static bool IsLocked()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("LogonUI");
                foreach (var p in procs) p.Dispose();
                return procs.Length > 0;
            }
            if (OperatingSystem.IsLinux())
            {
                var session = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
                if (string.IsNullOrEmpty(session)) return false;
                var psi = new System.Diagnostics.ProcessStartInfo("loginctl", ["show-session", session, "-p", "LockedHint", "--value"])
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2000);
                return output == "yes";
            }
        }
        catch { }
        return false;
    }
}
