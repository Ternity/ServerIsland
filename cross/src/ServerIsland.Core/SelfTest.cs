using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerIsland.Core.Ssh;

namespace ServerIsland.Core;

/// `ServerIsland --selftest`：用样例输出检查解析逻辑（与 Swift 版 SelfTest.swift 的检查项一一对应，另加跨平台版特有的检查）。
/// 需要 bash 的检查在没有 bash 的 Windows 上跳过。
public static class SelfTest
{
    static int _pass, _fail, _skip;

    static void Check(bool cond, string name)
    {
        Console.WriteLine((cond ? "✓ " : "✗ ") + name);
        if (cond) _pass++; else _fail++;
    }

    static void Skip(string name)
    {
        Console.WriteLine("- " + name + "（跳过：没有 bash）");
        _skip++;
    }

    public static string? BashPath =>
        File.Exists("/bin/bash") ? "/bin/bash"
        : OperatingSystem.IsWindows() && File.Exists(@"C:\Program Files\Git\bin\bash.exe") ? @"C:\Program Files\Git\bin\bash.exe"
        : null;

    public static string Bash(string script, params string[] args)
    {
        var psi = new ProcessStartInfo(BashPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        // 和两种 SSH 传输一样，发出去之前统一换成 \n（Windows 上源文件可能是 CRLF）。
        p.StandardInput.Write(script.Replace("\r\n", "\n"));
        p.StandardInput.Close();
        p.WaitForExit();
        return outTask.Result + (args.Contains("-n") ? errTask.Result : "");
    }

    internal static (int Code, string Err) BashSyntax(string script)
    {
        var psi = new ProcessStartInfo(BashPath!) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-n");
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(script.Replace("\r\n", "\n"));
        p.StandardInput.Close();
        p.WaitForExit();
        return (p.ExitCode, errTask.Result);
    }

    public static bool Run()
    {
        Lang.Setting = AppLanguage.Zh;
        _pass = _fail = _skip = 0;
        bool hasBash = BashPath != null;

        Check(Parsers.SlurmDuration("1-02:03:04") == 93784, "slurm 时长 d-hh:mm:ss");
        Check(Parsers.SlurmDuration("45:16") == 2716, "slurm 时长 mm:ss");
        Check(Parsers.SlurmDuration("4:00:00") == 14400, "slurm 时长 h:mm:ss");
        Check(Parsers.SlurmDuration("UNLIMITED") == null, "slurm 时长 UNLIMITED");
        Check(Parsers.SlurmDuration("2-00") == 172800, "slurm 时长 d-hh");

        var output = """
            Welcome to cluster!
            @@SI:BEGIN
            @@SI:SEC cpu
            cpu  100 0 50 800 50 0 0 0 0 0
            64
            6.20 5.80 5.10 3/1200 4242
            @@SI:RC 0
            @@SI:SEC mem
            MemTotal:       263842716 kB
            MemAvailable:   160000000 kB
            @@SI:RC 0
            @@SI:SEC gpu
            0, NVIDIA A100-SXM4-80GB, 97, 61000, 81920, 66
            1, NVIDIA A100-SXM4-80GB, 0, 3, 81920, 31
            @@SI:RC 0
            @@SI:SEC squeue
            4821337|pretrain_7b|RUNNING|4:24:00|2-00:00:00|2|gpu[03-04]|a100
            4821455|eval_mmlu|PENDING|0:00|2:00:00|1|(Priority)|a100
            @@SI:RC 0
            @@SI:SEC progress
            @@SI:FILE /home/u/.serverisland/progress/pretrain.json
            {"name":"pretrain_7b","progress":0.62,"message":"step 12400/20000","jobid":"4821337"}
            @@SI:FILE /home/u/.serverisland/progress/bad.json
            not json
            @@SI:RC 0
            @@SI:END
            """;
        var parsed = RemoteScript.Parse(output);
        Check(parsed.SawBegin && parsed.SawEnd, "识别 BEGIN/END，忽略登录横幅");
        var cpu = Parsers.Cpu(parsed.Get("cpu")?.Lines ?? []);
        Check(cpu.Cores == 64 && cpu.Load1 == 6.2 && cpu.Sample?.Total == 1000, "CPU 解析");
        var mem = Parsers.Memory(parsed.Get("mem")?.Lines ?? []);
        Check(mem != null && Math.Abs(mem.Value.Total - 263842716.0 * 1024) < 1, "内存解析");
        var gpus = Parsers.Gpus(parsed.Get("gpu")?.Lines ?? []);
        Check(gpus.Count == 2 && gpus[0].Utilization == 97 && gpus[1].MemoryTotalMB == 81920, "GPU 解析");
        var jobs = Parsers.Squeue(parsed.Get("squeue")?.Lines ?? []);
        Check(jobs.Count == 2 && jobs[0].State == JobState.Running && jobs[0].Limit == 172800 && jobs[1].State == JobState.Pending, "squeue 解析");
        var prog = Parsers.Progress(parsed.Get("progress")?.Lines ?? []);
        Check(prog.Count == 1 && prog[0].JobID == "4821337" && prog[0].Progress == 0.62, "进度文件解析（跳过坏文件）");

        var procs = Parsers.Processes([
            "  18342  9250 312.0 8123456 /usr/bin/python3 -u train_diffusion.py --lr 3e-4",
            "  20977   400   0.1   20480 -bash",
            "  20999    10   0.0    1024 ps -u yan -o pid=",
        ], "python|torchrun", "", new Dictionary<string, double> { ["18342"] = 61000 });
        Check(procs.Count == 1 && procs[0].Name == "python3 train_diffusion.py" && procs[0].GpuMemoryMB == 61000, "直连进程过滤");

        var sacct = Parsers.Sacct(["4821337|pretrain_7b|COMPLETED|11:42:05|0:0", "4821455|eval_mmlu|OUT_OF_MEMORY|00:42:18|0:125"]);
        Check(sacct["4821337"].State == JobState.Completed && sacct["4821455"].State == JobState.Failed, "sacct 最终状态");

        var script = RemoteScript.Build(new HostConfig("x", kind: HostKind.Slurm));
        Check(script.Contains("squeue") && script.Contains("@@SI:END"), "生成远端脚本");

        if (hasBash)
        {
            // 在本机 bash 上真实执行一遍直连脚本：没有 /proc 的系统应当优雅地产生 warning 而不是中断。
            var direct = new HostConfig("local", kind: HostKind.Direct) { ShowGPU = true };
            var outText = Bash(RemoteScript.Build(direct), "-s");
            var localParsed = RemoteScript.Parse(outText);
            Check(localParsed.SawBegin && localParsed.SawEnd && localParsed.Get("procs") != null,
                  $"脚本可在 bash 中完整执行（各段：{string.Join(",", localParsed.Sections.Keys.Order(StringComparer.Ordinal))}）");
        }
        else Skip("脚本可在 bash 中完整执行");

        var df = Parsers.Disks(["Filesystem 1024-blocks Used Available Capacity Mounted on",
                                "/dev/nvme0n1p2 960000000 912000000 48000000 95% /",
                                "nas:/home 2000000000 1000000000 1000000000 50% /mnt/my home"]);
        Check(df.Count == 2 && Math.Abs(df[0].Fraction - 0.95) < 0.001 && df[1].Mount == "/mnt/my home", "df 解析（含空格挂载点）");

        var kv = Parsers.KeyValues("JobId=4821337 JobName=pretrain 7b UserId=yan(1000) JobState=RUNNING Reason=None StdOut=/home/yan/logs/out-4821337.log WorkDir=/home/yan/proj");
        Check(kv.GetValueOrDefault("JobName") == "pretrain 7b" && kv.GetValueOrDefault("StdOut") == "/home/yan/logs/out-4821337.log" && kv.GetValueOrDefault("JobState") == "RUNNING",
              "scontrol key=value 解析");

        var hist = Parsers.History(["4821001|pretrain|COMPLETED|11:42:05|2026-10-16T09:12:00|0:0",
                                    "4821002|eval|FAILED|00:02:10|2026-10-16T10:00:00|1:0",
                                    "4821003|run|RUNNING|00:10:00|Unknown|0:0"]);
        Check(hist.Count == 2 && hist[0].Id == "4821002" && hist[0].JobState == JobState.Failed, "sacct 历史解析（跳过运行中）");
        var histDir = Parsers.History(["4821004|scf|COMPLETED|00:05:00|2026-10-16T11:00:00|0:0|/home/u/a|b"]);
        Check(histDir.FirstOrDefault()?.Dir == "/home/u/a|b" && hist[0].Dir == null, "sacct 历史：WorkDir 列（老格式没有也能解析）");
        var merged = HistoryStore.Merge(
            [new HistoryEntry { Id = "1", Name = "a", State = "COMPLETED", EndedAt = new DateTime(2026, 1, 1, 0, 5, 0) },
             new HistoryEntry { Id = "2", Name = "b", State = "FAILED", EndedAt = new DateTime(2026, 1, 1, 0, 3, 0) }],
            [new HistoryEntry { Id = "1", Name = "a", State = "ENDED", EndedAt = new DateTime(2026, 1, 1, 0, 4, 0), Dir = "/x" },
             new HistoryEntry { Id = "0", Name = "old", State = "COMPLETED", EndedAt = new DateTime(2026, 1, 1, 0, 1, 0), Dir = "/y" }]);
        Check(string.Join(",", merged.Select(e => e.Id)) == "1,2,0" && merged[0].State == "COMPLETED" && merged[0].Dir == "/x" && merged[2].Dir == "/y",
              "历史：sacct 和本机记录合并（以 sacct 为准，保留目录）");
        {
            var tmp = Path.Combine(Path.GetTempPath(), "si-hist-" + Guid.NewGuid().ToString("N"));
            try
            {
                var hid = Guid.NewGuid();
                var h1 = new HistoryStore(true, tmp);
                var running = new RemoteJob("77", "relax", JobState.Running, JobSource.Slurm) { Workdir = "/w/relax" };
                h1.SetActive(hid, [running, new RemoteJob("file:x", "p", JobState.Running, JobSource.ProgressFile)]);
                h1.Record(hid, new HistoryEntry { Id = "70", Name = "old", State = "COMPLETED", EndedAt = DateTime.Now.AddDays(-3), Dir = "/w/old" });
                h1.Record(hid, new HistoryEntry { Id = "60", Name = "older", State = "COMPLETED", EndedAt = DateTime.Now.AddDays(-10) });
                var h2 = new HistoryStore(true, tmp);   // 模拟重启
                Check(string.Join(",", h2.Entries(hid).Select(e => e.Id)) == "70" && h2.Entries(hid)[0].Dir == "/w/old"
                      && string.Join(",", h2.ActiveJobs(hid).Select(a => a.Id)) == "77" && h2.ActiveJobs(hid)[0].Dir == "/w/relax",
                      "历史：重启后还在（保留 7 天、带目录），记住上次运行中的任务");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        // 生成的详情 / 取消 / 历史脚本在 bash 下语法正确（只做 -n 语法检查，不执行）。
        var slurm = new HostConfig("x", kind: HostKind.Slurm) { AllowCancel = true };
        var sj = new RemoteJob("4821337", "it's \"quoted\"", JobState.Running, JobSource.Slurm);
        var pj = new RemoteJob("18342", "python3 train.py", JobState.Running, JobSource.Process);
        string[] scripts =
        [
            RemoteScript.DetailScript(slurm, sj),
            RemoteScript.DetailScript(new HostConfig("x"), pj),
            RemoteScript.CancelScript(slurm, sj, null, false)!,
            RemoteScript.CancelScript(new HostConfig("x"), pj, 9250, false)!,
            RemoteScript.CancelScript(new HostConfig("x"), pj, 9250, true)!,
            RemoteScript.HistoryScript(slurm),
            RemoteScript.TopProcessesScript(new HostConfig("x")),
            AppDetectors.FinalStateScript("123", "/home/u/it's dir"),
            RemoteScript.Build(new HostConfig("x") { Watched = [new WatchedProcess { Pid = "1" }] }),
        ];
        for (int i = 0; i < scripts.Length; i++)
        {
            if (!hasBash) { Skip($"脚本 {i} bash 语法"); continue; }
            var (code, err) = BashSyntax(scripts[i]);
            Check(code == 0, $"脚本 {i} bash 语法" + (err.Length == 0 ? "" : "：" + err));
        }

        // Job Array
        var arr = Parsers.Squeue([
            "123_5|sweep|lr|RUNNING|1:00|4:00:00|1|gpu01|a100|123|5",
            "123_6|sweep|lr|RUNNING|0:30|4:00:00|1|gpu02|a100|123|6",
            "123_[7-99%10]|sweep|lr|PENDING|0:00|4:00:00|1|(JobArrayTaskLimit)|a100|123|[7-99%10]",
            "200|single|RUNNING|0:10|1:00:00|1|gpu03|a100|200|N/A",
            "300_[1-4]|old|PENDING|0:00|1:00:00|1|(Priority)|a100",
        ]);
        Check(arr.Count == 5 && arr[0].Name == "sweep|lr" && arr[0].ArrayJobID == "123" && arr[2].ArrayTask == "[7-99%10]"
              && arr[3].ArrayJobID == null && arr[4].ArrayJobID == "300", "squeue 数组解析（含名称里的 |、老格式）");
        Check(Parsers.ArrayTaskCount("[7-99%10]") == 93 && Parsers.ArrayTaskCount("[1,3,5-7]") == 5
              && Parsers.ArrayTaskCount("[1-99:2]") == 50, "数组范围计数");
        var grouped = Parsers.GroupArrays(arr);
        var g = grouped.FirstOrDefault(j => j.Id == "123");
        Check(grouped.Count == 3 && g?.Array?.Running.Count == 2 && g?.Array?.Pending == 93 && g?.State == JobState.Running
              && grouped.Sum(j => j.RunningUnits) == 3 && grouped.Sum(j => j.PendingUnits) == 97, "数组合并为一行，按元素计数");
        // %K 不带方括号（"7-99%10"）时也按范围计数；以编号里的 _[...] 为准
        var noBracket = Parsers.GroupArrays(Parsers.Squeue([
            "400_1|scan|RUNNING|1:00|4:00:00|1|node01|cpu|400|1",
            "400_[2-50%4]|scan|PENDING|0:00|4:00:00|1|(JobArrayTaskLimit)|cpu|400|2-50%4",
            "500_[1,3,5-7]|scan2|PENDING|0:00|4:00:00|1|(Priority)|cpu|500|1,3,5-7",
        ]));
        Check(noBracket.FirstOrDefault(j => j.Id == "400")?.Array?.Pending == 49 && noBracket.FirstOrDefault(j => j.Id == "500")?.Array?.Pending == 5
              && Parsers.PendingTaskCount(new RemoteJob("600_[1-10]", "x", JobState.Pending, JobSource.Slurm)) == 10,
              "数组排队数：%K 不带方括号时按范围计数");
        var ah = Parsers.History(["123_1|sweep|COMPLETED|0:10:00|2026-10-16T09:00:00|0:0",
                                  "123_2|sweep|FAILED|0:01:00|2026-10-16T09:05:00|1:0",
                                  "123_3|sweep|COMPLETED|0:09:00|2026-10-16T09:06:00|0:0"]);
        Check(ah.Count == 1 && ah[0].Note == "数组 2✓ 1✗", "历史里的数组元素合并");
        Check(RemoteScript.SafeSlurmID("123_5") == "123_5" && RemoteScript.SafeSlurmID("123;rm") == null, "作业号校验");

        // 计算软件识别
        var appsOut = AppDetectors.Parse([
            "@@SI:DIR 1275823|/home/u/nep/train",
            "kind=nep", "total=100000", "last=   12000 0.12345 0.01 0.002 0.003 0.08 0.01", "mtime=1760600000",
            "@@SI:DIR 4821337_5|/scratch/u/md/300K",
            "kind=gpumd", "total=2000000", "interval=1000",
            "neighbor=Neighbor info at step 1200000: radial(max=120,actual=48), angular(max=60,actual=21).", "mtime=1760600000",
            "@@SI:DIR 99|/scratch/u/md2", "kind=gpumd", "interval=100", "thermo_rows=500", "mtime=1",
        ]);
        var nep = appsOut.GetValueOrDefault("1275823");
        Check(nep?.Current == 12000 && nep?.Fraction == 0.12 && nep?.Losses.FirstOrDefault()?.Label == "Total" && nep?.Losses.Count == 6, "NEP loss.out / nep.in 解析");
        var gm = appsOut.GetValueOrDefault("4821337_5");
        Check(gm?.Current == 1_200_000 && gm?.Fraction == 0.6 && gm?.Neigh?.RadialActual == 48, "GPUMD neighbor.out / run.in 解析");
        Check(appsOut.GetValueOrDefault("99")?.Current == 50_000 && appsOut.GetValueOrDefault("99")?.HasTarget == false, "GPUMD thermo.out 退化路径（无 run 步数）");
        Check(PendingReason.Explain("DependencyNeverSatisfied")?.NeedsAction == true
              && PendingReason.Explain("Dependency")?.Short == "等待依赖作业"
              && PendingReason.Explain("AssocGrpBillingRunMinutes")?.Short == "组计费额度已满"
              && PendingReason.Explain("Job's QOS not permitted to use this partition (cpu-debug allows short-qos)")?.NeedsAction == true,
              "排队原因翻译");
        // sinfo -N -O 的 Gres / GresUsed：NVIDIA 的 gpu、曙光（海光）的 dcu；"dcu:0,gpu:0" 不算一种卡
        var parts = Partitions.Parse(RemoteScript.Parse(string.Join("\n",
            "@@SI:SEC nodes",
            "c01  cpu*  mix    29/3/0/32  (null)              dcu:0,gpu:0",
            "d01  dcu   alloc  32/0/0/32  dcu:Hygon:4(S:0-3)  dcu:Hygon:4(IDX:0-3)",
            "d02  dcu   mix    8/24/0/32  dcu:Hygon:4(S:0-3)  dcu:Hygon:1(IDX:0)",
            "g01  gpu   idle   0/64/0/64  gpu:a100:8(S:0-1)   gpu:0",
            "@@SI:RC 0"))) ?? [];
        var dcuP = parts.FirstOrDefault(p => p.Name == "dcu"); var cpuP = parts.FirstOrDefault(p => p.Name == "cpu"); var gpuP = parts.FirstOrDefault(p => p.Name == "gpu");
        Check(dcuP?.GpuTotal == 8 && dcuP.GpuIdle == 3 && dcuP.GpuLabel == "DCU" && dcuP.GpuModels.SetEquals(["Hygon"])
              && cpuP?.GpuTotal == 0 && cpuP.GpuLabel == "GPU" && cpuP.CpuIdle == 3 && cpuP.IsDefault
              && gpuP?.GpuIdle == 8 && gpuP.GpuLabel == "GPU"
              && Partitions.GpuCount("gpu:v100:2,dcu:Hygon:4").Count == 6
              && PartitionStat.Label(["gpu", "dcu"]) == "DCU/GPU", "分区资源：GPU 和 DCU（Gres）");

        var cancelS = RemoteScript.CancelScript(slurm, sj, null, false)!;
        Check(cancelS.Contains("squeue -h -j 4821337") && cancelS.Contains("scancel 4821337"), "取消前先核对作业");

        if (hasBash) SelfTestApps.Run(Check);
        else Skip("计算软件识别（VASP / LAMMPS / 自定义规则 / 曲线）");
        SelfTestConnection.Run(Check);
        SelfTestPBS.Run(Check, hasBash);
        SelfTestMonitor.Run(Check);
        SelfTestBilling.Run(Check, hasBash);
        SelfTestPush.Run(Check);

        CrossPlatformChecks(hasBash);

        Console.WriteLine($"\n{_pass} 项通过，{_fail} 项失败" + (_skip > 0 ? $"，{_skip} 项跳过" : ""));
        return _fail == 0;
    }

    /// 跨平台版特有的检查：配置文件与 Swift 版互通、~/.ssh/config 解析、known_hosts 校验、脚本与 Swift 版一致。
    static void CrossPlatformChecks(bool hasBash)
    {
        // Swift 版写出的配置（字段、枚举、UUID、日期格式）能直接读，旧默认值会自动升级
        var swiftJson = """
            {
              "autoRefresh" : true, "collapsedStyle" : "auto", "defaultInterval" : 120, "language" : "zh",
              "hosts" : [ {
                "alias" : "hpc", "auth" : "interactive", "commands" : { "squeue" : "squeue -u \"$(id -un)\" -h -o '%i|%j|%T|%M|%l|%D|%R|%P'" },
                "displayName" : "HPC", "id" : "6F1C2F9A-1C3B-4E0B-9B3E-2D7C1A0B9C11", "keepAlive" : "d3", "kind" : "slurm",
                "showCPU" : false, "appProcessNames" : "gpumd|nep|gnep",
                "watched" : [ { "name" : "train", "pid" : "4242", "startedAt" : 781000000 } ]
              } ],
              "progressRules" : [ { "id" : "0D3E6E4B-8C2A-4F7E-A0F1-5B9D2C3E4F50", "name" : "PyTorch", "file" : "*.log", "match" : "regex", "pattern" : "e(\\d+)" } ]
            }
            """;
        var s = JsonSerializer.Deserialize<AppSettings>(swiftJson, Json.Options)!;
        var h = s.Hosts[0];
        Check(s.CollapsedStyle == CollapsedStyle.Auto && s.DefaultInterval == 120 && s.Language == AppLanguage.Zh && s.SoundEnabled
              && h.Kind == HostKind.Slurm && h.Auth == AuthMode.Interactive && h.KeepAlive == KeepAlive.D3 && !h.ShowCPU && h.ShowGPU
              && h.Id == Guid.Parse("6F1C2F9A-1C3B-4E0B-9B3E-2D7C1A0B9C11") && h.Commands.Squeue == CommandSet.Defaults.Squeue
              && h.AppProcessNames == HostConfig.DefaultAppProcessNames && h.Commands.Shell == "bash -l -s"
              && h.Watched[0].StartedAt?.ToUniversalTime() == new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(781000000)
              && s.ProgressRules[0].Match == RuleMatch.Regex, "读取 Swift 版格式的配置（缺字段补默认值、旧默认值升级）");
        var written = JsonSerializer.Serialize(s, Json.Options);
        Check(written.Contains("\"showCPU\": false") && written.Contains("\"keepAlive\": \"d3\"") && written.Contains("6F1C2F9A-1C3B-4E0B-9B3E-2D7C1A0B9C11")
              && written.Contains("\"startedAt\": 781000000") && !written.Contains("\"interval\""), "写出的配置与 Swift 版格式一致");

        // ~/.ssh/config：通配、取第一次出现的值、ProxyJump、Include
        var tmp = Path.Combine(Path.GetTempPath(), "si-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "extra.conf"), "Host gpu-*\n  User extra\n  Port 2200\n");
            var cfg = Path.Combine(tmp, "config");
            File.WriteAllText(cfg, $"""
                Include {Path.Combine(tmp, "*.conf")}
                Host hpc
                    HostName login.example.org
                    User alice
                    ProxyJump bastion
                Host gpu-01 gpu-02
                    HostName %h.lab.example.org
                Host *
                    User fallback
                    IdentityFile ~/.ssh/id_test
                """);
            var hpc = SshConfig.Resolve("hpc", cfg);
            var g1 = SshConfig.Resolve("gpu-01", cfg);
            var other = SshConfig.Resolve("bob@10.0.0.5:2222", cfg);
            var list = SshConfig.Load(cfg);
            Check(hpc.HostName == "login.example.org" && hpc.User == "alice" && hpc.Port == 22 && hpc.ProxyJump == "bastion"
                  && g1.HostName == "gpu-01.lab.example.org" && g1.User == "extra" && g1.Port == 2200
                  && other.User == "bob" && other.HostName == "10.0.0.5" && other.Port == 2222
                  && hpc.IdentityFiles.Count == 1 && hpc.IdentityFiles[0].EndsWith("id_test")
                  && list.Select(e => e.Alias).SequenceEqual(["hpc", "gpu-01", "gpu-02"]) && list[0].Summary == "alice@login.example.org  via bastion",
                  "~/.ssh/config 解析（Include、通配、ProxyJump、user@host:port）");

            // known_hosts：明文、哈希、非 22 端口、指纹变化
            var kh = Path.Combine(tmp, "known_hosts");
            var key = Encoding.ASCII.GetBytes("fake-ed25519-blob");
            var other2 = Encoding.ASCII.GetBytes("another-blob");
            var salt = RandomNumberGenerator.GetBytes(20);
            using var hmac = new HMACSHA1(salt);
            var hashed = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("[gpu.example.org]:2200")))}";
            File.WriteAllText(kh, $"login.example.org,10.0.0.1 ssh-ed25519 {Convert.ToBase64String(key)}\n{hashed} ssh-rsa {Convert.ToBase64String(key)}\n");
            Check(KnownHosts.Check("login.example.org", 22, "ssh-ed25519", key, kh) == KnownHosts.Verdict.Match
                  && KnownHosts.Check("login.example.org", 22, "ssh-ed25519", other2, kh) == KnownHosts.Verdict.Mismatch
                  && KnownHosts.Check("gpu.example.org", 2200, "rsa-sha2-512", key, kh) == KnownHosts.Verdict.Match
                  && KnownHosts.Check("gpu.example.org", 22, "ssh-rsa", key, kh) == KnownHosts.Verdict.Unknown
                  && KnownHosts.Check("new.example.org", 22, "ssh-ed25519", key, kh) == KnownHosts.Verdict.Unknown,
                  "known_hosts 校验（明文 / 哈希 / 端口 / 指纹变化）");
            KnownHosts.Add("new.example.org", 2222, "ssh-ed25519", key, kh);
            Check(KnownHosts.Check("new.example.org", 2222, "ssh-ed25519", key, kh) == KnownHosts.Verdict.Match, "known_hosts 追加新主机");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        // 错误归类：内置 SSH 的报错用 OpenSSH 的措辞，归类结果和系统 ssh 一致
        var keyHost = new HostConfig("x");
        var otpHost = new HostConfig("x") { Auth = AuthMode.Interactive };
        Check(SshCommon.Classify(SshResult.Failure("Permission denied (keyboard-interactive): not logged in"), otpHost)?.Kind == HealthKind.NeedsLogin
              && SshCommon.Classify(SshResult.Failure("Permission denied (publickey): no usable key"), keyHost)?.Message == "密钥登录失败"
              && SshCommon.Classify(SshResult.Failure("Host key verification failed."), keyHost)?.Kind == HealthKind.NeedsLogin
              && SshCommon.Classify(SshResult.Failure("ssh: Could not resolve hostname x"), keyHost)?.Message == "无法解析主机名"
              && SshCommon.Classify(new SshResult("", "", 0, 0, false), keyHost) == null, "SSH 错误归类");

        // 和 Swift 版生成的脚本逐字一致（参考文件由 Swift 版导出，见 cross/README.md）
        var refDir = Environment.GetEnvironmentVariable("SERVERISLAND_SCRIPT_REF");
        if (refDir != null && Directory.Exists(refDir))
        {
            foreach (var (name, text) in ReferenceScripts())
            {
                var f = Path.Combine(refDir, name);
                if (!File.Exists(f)) { Check(false, $"和 Swift 版脚本一致：{name}（缺参考文件）"); continue; }
                var expected = File.ReadAllText(f);
                Check(expected == text, $"和 Swift 版脚本一致：{name}" + (expected == text ? "" : $"（第 {FirstDiffLine(expected, text)} 行不同）"));
            }
        }
    }

    static int FirstDiffLine(string a, string b)
    {
        var la = a.Split('\n'); var lb = b.Split('\n');
        for (int i = 0; i < Math.Min(la.Length, lb.Length); i++) if (la[i] != lb[i]) return i + 1;
        return Math.Min(la.Length, lb.Length) + 1;
    }

    /// 和 Swift 版 `--dump-scripts` 对应的一组脚本（固定的主机配置和规则，便于逐字比较）。
    public static List<(string Name, string Text)> ReferenceScripts()
    {
        var rules = new List<ProgressRule>
        {
            new() { Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), Name = "PyTorch", File = "*.log", Pattern = "epoch", Unit = "epoch", DoneMarker = "Training finished" },
            new() { Id = Guid.Parse("66666666-7777-8888-9999-AAAAAAAAAAAA"), Name = "Solver", File = "run.log", Pattern = "step", TotalFile = "config.ini", TotalKeyword = "max_steps" },
            new() { Id = Guid.Parse("BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF"), Name = "Re", File = "progress.txt", Match = RuleMatch.Regex, Pattern = @"iter=(\d+)" },
        };
        var saved = ProgressRule.Active;
        ProgressRule.Active = rules;
        try
        {
            var slurm = new HostConfig("x", kind: HostKind.Slurm) { SlurmAlsoProcesses = true, AllowCancel = true };
            slurm.Commands.Preamble = "module load slurm";
            var direct = new HostConfig("y") { Watched = [new WatchedProcess { Pid = "4242" }] };
            var sj = new RemoteJob("4821337", "it's \"quoted\"", JobState.Running, JobSource.Slurm);
            var pj = new RemoteJob("18342", "python3 train.py", JobState.Running, JobSource.Process);
            var pbs = new HostConfig("z", kind: HostKind.Pbs) { SlurmAlsoProcesses = true, AllowCancel = true, PathFilter = "~/LLZO\n/scratch/demo" };
            var filtered = new HostConfig("y") { Watched = [new WatchedProcess { Pid = "4242" }], PathFilter = "~/runs" };
            var pbsJob = new RemoteJob("1003_2", "it's \"scan\"", JobState.Running, JobSource.Slurm);
            var cluster = new HostConfig("c", kind: HostKind.Slurm);
            var custom = new HostConfig("c", kind: HostKind.Slurm);
            custom.Commands.Squeue = "squeue -u \"$(id -un)\" -h -o '%i|%j|%T|%M|%l|%D|%R|%P'";
            return
            [
                ("poll-slurm.sh", RemoteScript.Build(slurm)),
                ("poll-direct.sh", RemoteScript.Build(direct)),
                ("detail-slurm.sh", RemoteScript.DetailScript(slurm, sj)),
                ("detail-process.sh", RemoteScript.DetailScript(direct, pj)),
                ("cancel-slurm.sh", RemoteScript.CancelScript(slurm, sj, null, false)!),
                ("cancel-process.sh", RemoteScript.CancelScript(direct, pj, 9250, true)!),
                ("history.sh", RemoteScript.HistoryScript(slurm)),
                ("top.sh", RemoteScript.TopProcessesScript(direct)),
                ("sacct.sh", RemoteScript.SacctScript(slurm, ["1", "2_3"])),
                ("final-state.sh", AppDetectors.FinalStateScript("123", "/home/u/it's dir")),
                ("rule-test.sh", AppDetectors.RuleTestScript("~/runs/exp1", rules[1])),
                ("curve-loss.sh", AppCurves.Script(new AppCurves.Source("/home/u/nep/loss.out", AppCurves.CurveKind.Loss, AppKind.Gnep), 120, "100 0.3")),
                ("curve-nep.sh", AppCurves.Script(new AppCurves.Source("/home/u/nep/loss.out", AppCurves.CurveKind.Loss), 0, "")),
                ("curve-mace.sh", AppCurves.Script(new AppCurves.Source("/home/u/mace/logs/LLZO_run-123.log", AppCurves.CurveKind.Loss, AppKind.Mace), 40, "x")),
                ("curve-vasp.sh", AppCurves.Script(new AppCurves.Source("/home/u/v/OSZICAR", AppCurves.CurveKind.VaspEnergy), 0, "")),
                ("partitions.sh", Partitions.Script(slurm)),
                ("detect.sh", RemoteScript.DetectScript),
                ("path-prelude.sh", SshCommon.PathPrelude),
                ("curve-force.sh", AppCurves.Script(new AppCurves.Source("/home/u/v/OUTCAR", AppCurves.CurveKind.VaspForce), 300, " vasp.6.4.2")),
                ("poll-pbs.sh", RemoteScript.Build(pbs)),
                ("poll-direct-filtered.sh", RemoteScript.Build(filtered)),
                ("detail-pbs.sh", RemoteScript.DetailScript(pbs, pbsJob)),
                ("cancel-pbs.sh", RemoteScript.CancelScript(pbs, pbsJob, null, false)!),
                ("sacct-pbs.sh", RemoteScript.SacctScript(pbs, ["1001", "1003_2"])),
                ("curve-neb.sh", AppCurves.Script(new AppCurves.Source("/home/u/neb run", AppCurves.CurveKind.Neb), 0, "")),
                ("poll-slurm-cached.sh", RemoteScript.Build(slurm, new Dictionary<string, string> { ["/home/u/it's run"] = "1.2 3.4 ", ["/home/u/a"] = "/home/u/a/x.log 1 2 " }, includeDisk: false)),
                ("poll-cluster.sh", RemoteScript.Build(cluster)),
                ("poll-cluster-custom.sh", RemoteScript.Build(custom)),
                ("poll-cluster-billing.sh", RemoteScript.Build(cluster, alloc: new(["1"], ["4821337", "8812300_16", "x;y"]))),
                ("poll-cluster-custom-billing.sh", RemoteScript.Build(custom, alloc: new(["4821337", "8812300_16", "x;y"], ["2"]))),
                ("poll-pbs-billing.sh", RemoteScript.Build(pbs, alloc: RemoteScript.AllocRequest.Empty)),
                ("pbs-queues.sh", RemoteScript.PbsQueuesScript(pbs)),
            ];
        }
        finally { ProgressRule.Active = saved; }
    }
}
