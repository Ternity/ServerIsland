using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ServerIsland.Core.Ssh;

/// "记住密码"只存进系统自带的凭据库，配置文件和日志里都不会出现密码：
/// - Windows：凭据管理器（普通凭据，目标名 ServerIsland:服务器UUID）；
/// - Linux：Secret Service（GNOME 钥匙圈 / KWallet，通过 secret-tool）；没有 secret-tool 时不能记住；
/// - macOS（只用于开发调试）：登录钥匙串（security 命令）。
public static class CredentialStore
{
    const string Service = "ServerIsland";

    static string Target(Guid id) => Service + ":" + id.ToString("D").ToUpperInvariant();

    /// 这台电脑能不能记住密码（界面据此决定是否显示"记住密码"）。
    public static bool Available
    {
        get
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return true;
            return _secretTool ??= Which("secret-tool") != null;
        }
    }
    static bool? _secretTool;

    public static string? Get(Guid id)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WinCred.Read(Target(id));
            if (OperatingSystem.IsMacOS())
                return Run("/usr/bin/security", ["find-generic-password", "-s", Service, "-a", id.ToString("D").ToUpperInvariant(), "-w"], null) is (0, var o) ? o.TrimEnd('\n') : null;
            return Available && Run("secret-tool", ["lookup", "service", Service, "host", id.ToString("D").ToUpperInvariant()], null) is (0, var s) && s.Length > 0 ? s.TrimEnd('\n') : null;
        }
        catch (Exception e) { DebugLog.Log("读取凭据失败：" + e.Message); return null; }
    }

    public static bool Has(Guid id) => Get(id) != null;

    public static bool Save(Guid id, string password, string label)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WinCred.Write(Target(id), password, "ServerIsland – " + label);
            var acct = id.ToString("D").ToUpperInvariant();
            if (OperatingSystem.IsMacOS())
                return Run("/usr/bin/security", ["add-generic-password", "-U", "-s", Service, "-a", acct, "-l", "ServerIsland – " + label, "-w", password], null).Code == 0;
            // secret-tool 从 stdin 读密码，不出现在命令行参数里
            return Available && Run("secret-tool", ["store", "--label=ServerIsland – " + label, "service", Service, "host", acct], password).Code == 0;
        }
        catch (Exception e) { DebugLog.Log("保存凭据失败：" + e.Message); return false; }
    }

    public static void Delete(Guid id)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { WinCred.Delete(Target(id)); return; }
            var acct = id.ToString("D").ToUpperInvariant();
            if (OperatingSystem.IsMacOS()) { Run("/usr/bin/security", ["delete-generic-password", "-s", Service, "-a", acct], null); return; }
            if (Available) Run("secret-tool", ["clear", "service", Service, "host", acct], null);
        }
        catch { }
    }

    // —— 其它密钥（如手机推送的 Webhook key），账户名是固定的字符串 ——

    public static string? GetSecret(string account)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WinCred.Read(Service + ":" + account);
            if (OperatingSystem.IsMacOS())
                return Run("/usr/bin/security", ["find-generic-password", "-s", Service, "-a", account, "-w"], null) is (0, var o) ? o.TrimEnd('\n') : null;
            return Available && Run("secret-tool", ["lookup", "service", Service, "host", account], null) is (0, var s) && s.Length > 0 ? s.TrimEnd('\n') : null;
        }
        catch (Exception e) { DebugLog.Log("读取凭据失败：" + e.Message); return null; }
    }

    public static bool SaveSecret(string account, string value, string label)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WinCred.Write(Service + ":" + account, value, "ServerIsland – " + label);
            if (OperatingSystem.IsMacOS())
                return Run("/usr/bin/security", ["add-generic-password", "-U", "-s", Service, "-a", account, "-l", "ServerIsland – " + label, "-w", value], null).Code == 0;
            return Available && Run("secret-tool", ["store", "--label=ServerIsland – " + label, "service", Service, "host", account], value).Code == 0;
        }
        catch (Exception e) { DebugLog.Log("保存凭据失败：" + e.Message); return false; }
    }

    public static void DeleteSecret(string account)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { WinCred.Delete(Service + ":" + account); return; }
            if (OperatingSystem.IsMacOS()) { Run("/usr/bin/security", ["delete-generic-password", "-s", Service, "-a", account], null); return; }
            if (Available) Run("secret-tool", ["clear", "service", Service, "host", account], null);
        }
        catch { }
    }

    static (int Code, string Out) Run(string exe, string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin != null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return (-1, ""); }
        return (p.ExitCode, o);
    }

    static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    /// Windows 凭据管理器（advapi32 CredRead / CredWrite / CredDelete）。
    static class WinCred
    {
        const int CRED_TYPE_GENERIC = 1;
        const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string? Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref CREDENTIAL credential, int flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, int type, int flags);
        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr buffer);

        public static string? Read(string target)
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
            try
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0) return "";
                return Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
            }
            finally { CredFree(ptr); }
        }

        public static bool Write(string target, string secret, string comment)
        {
            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var c = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    Comment = comment,
                    CredentialBlobSize = bytes.Length,
                    CredentialBlob = blob,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = "ServerIsland",
                };
                return CredWrite(ref c, 0);
            }
            finally
            {
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
            }
        }

        public static void Delete(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);
    }
}
