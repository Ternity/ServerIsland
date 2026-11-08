using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// `--demo` 模式和预览图用的假数据，不会连接任何服务器。与 Swift 版 DemoData.swift 一致。
/// 注意：这里的用户名、路径、主机名都是虚构的（用户名 demo），不要写进真实环境的信息。
public static class DemoData
{
    public static AppSettings Settings()
    {
        var s = new AppSettings();
        var a = new HostConfig("hpc-login", L("HPC 集群", "HPC Cluster"), HostKind.Slurm)
        {
            Auth = AuthMode.Interactive,
            Prices = [new PartitionPrice("", PriceUnit.Cpu, 0.03),
                      new PartitionPrice("cpu", PriceUnit.Cpu, 0.04, L("64 节点 · 共 4096 核", "64 nodes · 4096 cores")),
                      new PartitionPrice("gpu-v100", PriceUnit.Gpu, 2.5, L("16 节点 · V100 共 64 张GPU", "16 nodes · V100 64 GPUs"))],
        };
        var b = new HostConfig("gpu-box", L("4090 工作站", "4090 Workstation"), HostKind.Direct);
        s.Hosts = [a, b];
        return s;
    }

    /// 费用页的演示账本：本月和上月在 HPC 集群上结束的作业（虚构）。
    public static List<CostEntry> CostEntries(Guid host, DateTime? at = null)
    {
        var now = at ?? DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var today = now.Day;
        var list = new List<CostEntry>();
        // (名称, 分区, 计价, 资源量, 小时, 单价, 几天前)
        (string, string, PriceUnit, double, double, double, int)[] rows =
        [
            ("aimd_LGPS_1200K", "cpu", PriceUnit.Cpu, 128, 48, 0.04, 1),
            ("relax_Li3PS4_pbe", "cpu", PriceUnit.Cpu, 64, 2.1, 0.04, 0),
            ("phonon_LiCoO2_222", "cpu", PriceUnit.Cpu, 64, 0.7, 0.04, 0),
            ("nep_train_LiF", "gpu-v100", PriceUnit.Gpu, 4, 24, 2.5, 3),
            ("gpumd_kappa_250K", "gpu-v100", PriceUnit.Gpu, 1, 11.5, 2.5, 2),
            ("scf_LiCoO2_kmesh", "cpu", PriceUnit.Cpu, 32, 0.9, 0.04, 4),
            ("neb_Li_vacancy", "cpu", PriceUnit.Cpu, 128, 6, 0.04, 6),
            ("gpumd_kappa_300K_old", "gpu-v100", PriceUnit.Gpu, 1, 9, 2.5, 8),
            ("md_HEA_npt", "gpu-v100", PriceUnit.Gpu, 2, 14, 2.5, 11),
            ("relax_LiCoO2_pbe", "cpu", PriceUnit.Cpu, 64, 5, 0.04, 13),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.Item7 >= today) continue;
            var end = now.Date.AddDays(-r.Item7).AddSeconds(3600 + i * 600);
            if (end > now.AddMinutes(-10)) end = now.AddMinutes(-10);
            list.Add(new CostEntry { Host = host, Id = $"88119{10 + i}", Name = r.Item1, Partition = r.Item2, Unit = r.Item3, Amount = r.Item4,
                                     Seconds = r.Item5 * 3600, Price = r.Item6, EndedAt = end });
        }
        // 数组的 14 个元素
        for (int k = 0; k < 14 && today > 1; k++)
            list.Add(new CostEntry { Host = host, Id = $"8812300_{k}", ArrayBase = "8812300", Name = "eos_scan", Partition = "cpu", Unit = PriceUnit.Cpu,
                                     Amount = 32, Seconds = 2400, Price = 0.04, EndedAt = now.Date.AddSeconds(1800 + k * 300) });
        // 上月
        for (int k = 0; k < 8; k++)
            list.Add(new CostEntry { Host = host, Id = $"8800{100 + k}", Name = $"train_run_{k}", Partition = "gpu-v100", Unit = PriceUnit.Gpu,
                                     Amount = 2, Seconds = 20 * 3600, Price = 2.5, EndedAt = monthStart.AddDays(-(k + 1) * 3) });
        return list;
    }

    public static readonly HashSet<string> Aliases = ["hpc-login", "gpu-box"];
    public static bool IsDemoHost(HostConfig h) => Aliases.Contains(h.Alias);

    /// 演示用的工作目录前缀。
    public const string Home = "/home/demo/projects";

    public static HostSnapshot Snapshot(HostConfig host, int tick)
    {
        var s = new HostSnapshot { Health = HostHealth.Ok, LastUpdate = DateTime.Now, Latency = 0.08 + tick % 5 * 0.01 };
        double t = tick;
        double Wave(double phase) => (Math.Sin(t / 3 + phase) + 1) / 2;

        RemoteJob AppJob(string id, string name, JobSource source, AppProgress app) => new(id, name, JobState.Running, source)
        {
            App = app, Progress = app.Fraction, Eta = app.Eta, Message = app.Title + " · " + app.Summary,
        };

        if (host.Kind == HostKind.Slurm)
        {
            s.Metrics = new HostMetrics { CpuPercent = 18 + Wave(0) * 12, Load1 = 6.2, Cores = 64, MemoryUsed = 92e9 + Wave(1) * 8e9, MemoryTotal = 256e9 };

            // VASP 从头算分子动力学：NSW 已知，显示百分比
            var aimd = new AppProgress(AppKind.Vasp, Home + "/Li3PS4/aimd-900K")
            { Vasp = VaspMode.Md, Current = 1_240 + t, Total = 5_000, HasTarget = true, Rate = 0.034, UpdatedAt = DateTime.Now, EndMarker = false };
            var md = AppJob("8812044", "aimd_Li3PS4_900K", JobSource.Slurm, aimd);
            md.Elapsed = 36_420 + t * 2; md.Limit = 48 * 3600; md.Detail = "cn[012-013] · cpu";
            md.Partition = "cpu"; md.Nodes = 2; md.Alloc = new JobAlloc(128, 0, 2);

            // VASP 结构优化：不知道总步数，只显示离子步
            var relaxApp = new AppProgress(AppKind.Vasp, Home + "/LiCoO2/relax-hse")
            { Vasp = VaspMode.Relax, Current = 17, HasTarget = false, UpdatedAt = DateTime.Now, EndMarker = false };
            var relax = AppJob("8812101", "relax_LiCoO2_hse", JobSource.Slurm, relaxApp);
            relax.Elapsed = 11_530 + t * 2; relax.Limit = 24 * 3600; relax.Detail = "cn027 · cpu";
            relax.Partition = "cpu"; relax.Nodes = 1; relax.Alloc = new JobAlloc(64, 0, 1);

            // GPUMD 热导率
            var kappa = new AppProgress(AppKind.Gpumd, Home + "/graphene/kappa-300K")
            {
                Current = 1_240_000 + t * 3_000, Total = 2_000_000, Rate = 1_520, UpdatedAt = DateTime.Now,
                Neigh = new AppProgress.Neighbor(48, 120, 21, 60),
            };
            var gpumd = AppJob("8812230", "gpumd_kappa_300K", JobSource.Slurm, kappa);
            gpumd.Elapsed = 2_710 + t * 2; gpumd.Limit = 12 * 3600; gpumd.Detail = "gpu07 · gpu-v100";
            gpumd.Partition = "gpu-v100"; gpumd.Nodes = 1; gpumd.Alloc = new JobAlloc(8, 1, 1);

            // 数组作业：40 个应变点的状态方程扫描
            var eos = new RemoteJob("8812300", "eos_scan", JobState.Running, JobSource.Slurm) { ArrayJobID = "8812300" };
            var running = new List<RemoteJob>();
            for (int k = 0; k < 6; k++)
            {
                running.Add(new RemoteJob($"8812300_{16 + k}", "eos_scan", JobState.Running, JobSource.Slurm)
                {
                    ArrayJobID = "8812300", ArrayTask = $"{16 + k}", Elapsed = 300 + k * 170 + t * 2, Limit = 4 * 3600, Detail = $"cn0{40 + k} · cpu",
                    Partition = "cpu", Nodes = 1, Alloc = new JobAlloc(32, 0, 1),
                });
            }
            eos.Array = new ArrayInfo { BaseID = "8812300", Running = running, Pending = 18, Done = 14, Failed = 2, Total = 40, Accurate = true };
            eos.Elapsed = running.Max(r => r.Elapsed);
            eos.Limit = 4 * 3600;
            eos.Partition = "cpu";
            eos.Detail = L("运行 6 · 排队 18 · 失败 2", "6 running · 18 pending · 2 failed");

            var phonon = new RemoteJob("8812355", "phonon_Si_444", JobState.Pending, JobSource.Slurm) { Limit = 12 * 3600, Detail = "(Priority) · cpu", Reason = "Priority" };
            var band = new RemoteJob("8812356", "band_LiCoO2", JobState.Pending, JobSource.Slurm) { Limit = 6 * 3600, Detail = "(Dependency) · cpu", Reason = "Dependency" };

            s.Jobs = [md, relax, eos, gpumd, phonon, band];
        }
        else
        {
            s.Metrics = new HostMetrics { CpuPercent = 41 + Wave(2) * 30, Load1 = 9.8, Cores = 32, MemoryUsed = 71e9 + Wave(3) * 6e9, MemoryTotal = 128e9 };
            for (int i = 0; i < 4; i++)
            {
                bool idle = i == 3;
                s.Gpus.Add(new GPUStat
                {
                    Index = i, Name = "RTX 4090", Utilization = idle ? 0 : 70 + Wave(i) * 29,
                    MemoryUsedMB = idle ? 312 : 6_000 + Wave(i) * 9_000, MemoryTotalMB = 24_564, Temperature = 64,
                });
            }

            // NEP 训练（GPUMD 套件）
            var nepApp = new AppProgress(AppKind.Nep, Home + "/Li3PS4/nep-train")
            {
                Current = 7_800 + t * 20, Total = 20_000, Rate = 26.4, UpdatedAt = DateTime.Now,
                Losses = [new("Total", 0.1815), new("L1", 0.045), new("L2", 0.064), new("Energy", 0.00074), new("Force", 0.07098), new("Virial", 0.01131)],
            };
            var nep = AppJob("41022", "nep", JobSource.Process, nepApp);
            nep.Elapsed = 5_420 + t * 2; nep.GpuMemoryMB = 2_600;

            // LAMMPS 高熵合金 NPT
            var lmpApp = new AppProgress(AppKind.Lammps, Home + "/HEA/npt-1200K")
            { Current = 380_000 + t * 2_000, Total = 1_000_000, Rate = 830, UpdatedAt = DateTime.Now, EndMarker = false };
            var lmp = AppJob("41377", "lmp", JobSource.Process, lmpApp);
            lmp.Elapsed = 2_300 + t * 2; lmp.CpuPercent = 1_580;

            // MACE 机器学习势训练
            var mace = new AppProgress(AppKind.Mace, Home + "/MACE/run-02")
            {
                Current = 37, Total = 200, UpdatedAt = DateTime.Now, DataFile = "logs/MACE_run-123.log",
                Losses = [new("Loss", 0.0213), new("RMSE E (meV)", 0.41), new("RMSE F (meV/Å)", 14.6)],
            };
            var train = AppJob("41856", "python train_mace.py", JobSource.Process, mace);
            train.Elapsed = 9_250 + t * 2; train.CpuPercent = 104; train.GpuMemoryMB = 14_800;

            s.Jobs = [nep, lmp, train];
        }
        return s;
    }

    // MARK: - 按需查询的演示数据（演示模式下打开详情、历史、分区等页面时用，不连接服务器）

    public static JobDetail Detail(RemoteJob job)
    {
        var d = new JobDetail();
        if (job.Source == JobSource.Slurm)
        {
            d.Fields = [(L("状态", "State"), job.RawState.Length > 0 ? job.RawState : job.State == JobState.Pending ? "PENDING" : "RUNNING"),
                        (L("已运行", "Elapsed"), job.Elapsed is { } e ? Format.Duration(e) : "0:00"), (L("时间上限", "Time limit"), job.Limit is { } l ? Format.Duration(l) : "UNLIMITED"),
                        (L("分区", "Partition"), job.Detail.Split(" · ").LastOrDefault() ?? "cpu"), (L("节点", "Nodes"), job.Detail.Split(" · ").FirstOrDefault() ?? "cn001"),
                        (L("工作目录", "Directory"), job.App?.Dir ?? Home + "/" + job.Name)];
            d.Stdout = new JobDetail.LogTail
            {
                Path = (job.App?.Dir ?? Home + "/" + job.Name) + $"/slurm-{job.Id}.out",
                Lines = job.App?.Kind == AppKind.Vasp
                    ? ["       N       E                     dE             d eps       ncg     rms          rms(c)",
                       "RMM:   5    -0.43125E+03   -0.21E-04   -0.18E-04  2240   0.67E-02",
                       "  17 F= -.43125E+03 E0= -.43125E+03  d E =-.24E-02  mag=     2.0003",
                       "DAV:   1    -0.43126E+03   -0.11E-02   -0.27E-02  2216   0.11E+00"]
                    : ["step 1241000  T = 300.4 K  E = -3.512 eV/atom", "step 1242000  T = 299.8 K  E = -3.512 eV/atom", "step 1243000  T = 300.1 K  E = -3.512 eV/atom"],
            };
        }
        else
        {
            d.Fields = [(L("PID / 父进程", "PID / parent"), $"{job.Id} / 40990"), (L("用户", "User"), "demo"),
                        (L("已运行", "Elapsed"), job.Elapsed is { } e ? Format.Duration(e) : "0:00"),
                        (L("CPU / 内存", "CPU / MEM"), $"{(job.CpuPercent ?? 99):0.0}% / 1.4%"), (L("常驻内存", "RSS"), "1.8G"), (L("状态", "State"), "Rl"),
                        (L("工作目录", "Directory"), job.App?.Dir ?? Home), (L("命令", "Command"), job.Detail.Length > 0 ? job.Detail : job.Name)];
            d.Stdout = new JobDetail.LogTail
            {
                Path = (job.App?.Dir ?? Home) + "/train.log",
                Lines = ["Generation = 8000, Total = 0.1815, L1 = 0.0450, L2 = 0.0640", "  RMSE-E-train = 0.74 meV/atom", "  RMSE-F-train = 70.98 meV/A",
                         "  RMSE-V-train = 11.31 meV/atom", "Generation = 8020, Total = 0.1812", "WARNING: learning rate decayed", "Generation = 8040, Total = 0.1809"],
            };
        }
        return d;
    }

    public static List<AppCurve> Curves(AppProgress app)
    {
        var out_ = new List<AppCurve>();
        foreach (var src in AppCurves.SourcesFor(app))
        {
            var rows = src.Kind switch
            {
                AppCurves.CurveKind.Loss when src.App == AppKind.Mace => Enumerable.Range(0, 8).Select(i => new[] { i * 5.0, 0.4 / (i + 1) + 0.02, 3.0 / (i + 1) + 0.4, 120.0 / (i + 1) + 14 }).ToList(),
                AppCurves.CurveKind.Loss =>Enumerable.Range(1, 80).Select(i => new[] { i * 100.0, 2.0 / Math.Sqrt(i) + 0.15, 0.04, 0.06, 0.05 / i + 0.0007, 0.6 / Math.Sqrt(i) + 0.07, 0.2 / Math.Sqrt(i) + 0.011 }).ToList(),
                AppCurves.CurveKind.VaspEnergy => Enumerable.Range(1, 17).Select(i => new[] { (double)i, -432.1 - 3.2 * (1 - Math.Exp(-i / 5.0)) }).ToList(),
                _ => Enumerable.Range(1, 17).Select(i => new[] { 0, 1.8 * Math.Exp(-i / 4.0) + 0.012 + 0.004 * Math.Sin(i) }).ToList(),
            };
            if (AppCurves.Curve(src, rows, new() { ["ediffg"] = "-0.02", ["keys"] = "loss,RMSE_E_per_atom,RMSE_F" }) is { } c) out_.Add(c);
        }
        return out_;
    }

    public static List<HistoryEntry> History() =>
    [
        new() { Id = "8811920", Name = "relax_Li3PS4_pbe", State = "COMPLETED", Elapsed = 4_210, EndedAt = DateTime.Now.AddMinutes(-35), ExitCode = "0:0", Dir = "/home/demo/projects/Li3PS4/relax" },
        new() { Id = "8811877", Name = "aimd_LGPS_600K", State = "TIMEOUT", Elapsed = 172_800, EndedAt = DateTime.Now.AddHours(-3), ExitCode = "0:15" },
        new() { Id = "8811702", Name = "eos_scan", State = "COMPLETED", Elapsed = 1_800, EndedAt = DateTime.Now.AddHours(-6), Note = L("数组 38✓ 2✗", "array 38✓ 2✗") },
        new() { Id = "8811650", Name = "phonon_Si_333", State = "FAILED", Elapsed = 210, EndedAt = DateTime.Now.AddHours(-9), ExitCode = "1:0", Dir = "/home/demo/projects/Si/phonon_333" },
        new() { Id = "8810412", Name = "nep_train_LiF", State = HistoryEntry.EndedUnknown, Elapsed = 86_400, EndedAt = DateTime.Now.AddDays(-3),
                Note = L("ServerIsland 未运行时结束", "ended while ServerIsland wasn't running"), Dir = "/home/demo/nep/LiF" },
    ];

    public static List<PartitionStat> Partitions() =>
    [
        new() { Name = "gpu-a100", TimeLimit = "2-00:00:00", Nodes = 8, IdleNodes = 1, MixNodes = 3, AllocNodes = 4, CpuIdle = 112, CpuTotal = 512, GpuIdle = 7, GpuTotal = 32, GpuModels = ["a100"] },
        new() { Name = "cpu", IsDefault = true, TimeLimit = "7-00:00:00", Nodes = 64, IdleNodes = 9, MixNodes = 20, AllocNodes = 33, DownNodes = 2, CpuIdle = 1_480, CpuTotal = 4_096 },
        new() { Name = "gpu-v100", TimeLimit = "1-00:00:00", Nodes = 6, AllocNodes = 6, CpuTotal = 240, GpuTotal = 24, GpuModels = ["v100"] },
        new() { Name = "debug", Available = false, TimeLimit = "30:00", Nodes = 2, DownNodes = 2, CpuTotal = 64 },
    ];
}
