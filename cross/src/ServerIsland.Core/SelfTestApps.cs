namespace ServerIsland.Core;

/// 计算软件识别的自检：在本机临时目录里造假的 VASP / LAMMPS / 自定义输出，
/// 用本机 bash 真正执行生成的远端脚本，再解析结果。（与 Swift 版 SelfTestApps.swift 一一对应）
public static class SelfTestApps
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "si-selftest-" + Guid.NewGuid().ToString("N"));
        try { RunIn(root, check); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void RunIn(string root, Action<bool, string> check)
    {
        string Make(string dir, Dictionary<string, string> files)
        {
            var d = Path.Combine(root, dir);
            Directory.CreateDirectory(d);
            foreach (var (name, text) in files)
            {
                var f = Path.Combine(d, name);
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, text);
            }
            return d.Replace('\\', '/');
        }

        string Bash(string script) => SelfTest.Bash(script, "-s");

        AppProgress? Detect(string dir, List<ProgressRule>? rules = null, bool builtin = true)
        {
            rules ??= [];
            var lines = Bash(AppDetectors.DirectoryScript("t", dir, rules, builtin)).Split('\n');
            return AppDetectors.Parse(lines, rules).GetValueOrDefault("t");
        }

        // MARK: VASP

        var oszMD = """
               N       E                     dE             d eps       ncg     rms          rms(c)
            DAV:   1     0.123E+03    0.123E+03   -0.5E+03  1000   0.1E+02
            RMM:   2    -0.101E+03   -0.22E+03   -0.3E+02  1200   0.4E+01
               1 T=   300. E= -.10E+03 F= -.11E+03 E0= -.11E+03  EK= 0.1E+01 SP= 0.0E+00 SK= 0.0E+00
            DAV:   1    -0.111E+03   -0.1E-01   -0.3E-01  1000   0.2E+00
               2 T=   310. E= -.10E+03 F= -.11E+03 E0= -.11E+03  EK= 0.1E+01 SP= 0.0E+00 SK= 0.0E+00
            RMM:   3    -0.111E+03   -0.1E-03   -0.1E-03  1000   0.1E-01

            """;
        var md = Detect(Make("vasp-md", new() { ["INCAR"] = "SYSTEM = test\nIBRION = 0 ; NSW = 500   ! MD\nPOTIM = 1\n", ["OSZICAR"] = oszMD, ["OUTCAR"] = " running on 4 nodes\n" }));
        check(md?.Vasp == VaspMode.Md && md.Current == 2 && md.Total == 500 && md.EndMarker == false, "VASP MD：离子步 / NSW");

        var oszRelax = """
            DAV:   1     0.4E+03    0.4E+03   -0.1E+04  1000   0.1E+02
               1 F= -.43E+03 E0= -.43E+03  d E =-.43E+03  mag=     2.0
            RMM:   5    -0.43E+03   -0.1E-04   -0.1E-04  1000   0.1E-01
               2 F= -.44E+03 E0= -.44E+03  d E =-.1E+01  mag=     2.0
               3 F= -.44E+03 E0= -.44E+03  d E =-.1E-02  mag=     2.0

            """;
        var relax = Detect(Make("vasp-relax", new() { ["INCAR"] = "ibrion=2; nsw=200\nEDIFFG = -0.01 # force\n", ["OSZICAR"] = oszRelax,
                                                      ["OUTCAR"] = "...\n General timing and accounting informations for this job:\n Total CPU time used (sec): 1.0\n" }));
        check(relax?.Vasp == VaspMode.Relax && relax.Current == 3 && relax.Total == null && relax.Fraction == null
              && relax.EndMarker == true && relax.Finished, "VASP 结构优化：只显示离子步（小写、分号、注释）");

        var scf = Detect(Make("vasp-scf", new() { ["INCAR"] = "ENCUT = 500\n", ["OSZICAR"] = "DAV:   1  0.1E+03\nDAV:  11  0.1E+03\nRMM:  12  0.1E+03\n" }));
        check(scf?.Vasp == VaspMode.Scf && scf.Current == 12 && scf.EndMarker == null, "VASP 单点：电子步");

        check(Detect(Make("vasp-phonon", new() { ["INCAR"] = "IBRION = 6\nNSW = 1\n", ["OSZICAR"] = oszRelax })) == null, "VASP 声子（IBRION=6）不识别");
        check(Detect(Make("vasp-md-bad", new() { ["INCAR"] = "IBRION = 0\nNSW = 1\n", ["OSZICAR"] = oszMD })) == null, "VASP MD 离子步超过 NSW 时不显示");

        // MARK: LAMMPS

        var lmpHead = """
            LAMMPS (2 Aug 2023)
            units metal
            thermo 100
            run 500
               Step          Temp          E_pair
                     0   300           -3.5
                   500   290           -3.5
            Loop time of 1.0 on 4 procs for 500 steps with 1000 atoms

            fix 2 all npt temp 300 300 0.1 iso 0 0 1
            run 1000
            Neighbor list info ...
              update: every = 1 steps, delay = 0 steps, check = yes
            Per MPI rank memory allocation (min/avg/max) = 3.1 | 3.1 | 3.1 Mbytes
               Step          Temp          E_pair         TotEng         Press
                   500   300           -3.5            -3.4            100
                   600   301.2         -3.5            -3.4            -20.5
            WARNING: Something happened (src/foo.cpp:12)
                   700   299.1         -3.5            -3.4            1.5e+02

            """;
        var lmp = Detect(Make("lmp-run", new() { ["log.lammps"] = lmpHead }));
        check(lmp?.Kind == AppKind.Lammps && lmp.Current == 200 && lmp.Total == 1000 && lmp.EndMarker == false, "LAMMPS：最后一段 run 的进度");

        var lmpDone = lmpHead + """
                   1500   300           -3.5            -3.4            12
            Loop time of 12.3 on 4 procs for 1000 steps with 1000 atoms
            Nlocal:           1000 ave        1000 max        1000 min
            Total # of neighbors = 64000
            Total wall time: 0:00:13

            """;
        var done = Detect(Make("lmp-done", new() { ["log.lammps"] = lmpDone }));
        check(done?.Current == 1000 && done.EndMarker == true && done.Finished, "LAMMPS：Total wall time 判定正常结束");

        var lmpMin = lmpDone.Replace("Total wall time: 0:00:13\n", "minimize 1e-6 1e-8 1000 10000\n   Step  Temp  E_pair\n  1500 0 -3.6\n");
        check(Detect(Make("lmp-min", new() { ["log.lammps"] = lmpMin })) == null, "LAMMPS：run 之后接 minimize 时不显示");
        var lmpVar = "LAMMPS\nvariable n equal 1000\nrun ${n}\n   Step Temp\n  0 300\n  100 300\n";
        check(Detect(Make("lmp-var", new() { ["log.lammps"] = lmpVar })) == null, "LAMMPS：run 步数是变量且没有展开时不显示");
        var lmpMulti = "LAMMPS\nthermo_style multi\nrun 1000\n------------ Step              0 ----- CPU =      0.0 (sec) ----\nTotEng   =     -3.4 KinEng   =      0.1\n";
        check(Detect(Make("lmp-multi", new() { ["log.lammps"] = lmpMulti })) == null, "LAMMPS：thermo_style multi 不显示");

        // MARK: 自定义规则

        var kw = new ProgressRule { Name = "PyTorch", File = "*.log", Pattern = "epoch", Unit = "epoch", DoneMarker = "Training finished" };
        var kwDir = Make("custom-kw", new() { ["train.log"] = "Epoch 36/100 loss 0.5\nEpoch 37/100 loss 0.4\nvalidating...\n" });
        var k = Detect(kwDir, [kw]);
        check(k?.Kind == AppKind.Custom && k.Title == "PyTorch" && k.Current == 37 && k.Total == 100 && k.EndMarker == false, "自定义规则：关键字 + 自动识别 a/b");
        check(Detect(kwDir, [kw], builtin: false)?.Current == 37, "自定义规则：只测试这一条（不跑内置识别）");

        var okTest = AppDetectors.InterpretTest(Bash(AppDetectors.RuleTestScript(kwDir, kw)), kw);
        check(okTest.Ok && okTest.Headline.Contains("37") && okTest.Headline.Contains("100"), "测试规则：匹配成功");
        var kwBad = kw.Clone(); kwBad.Pattern = "loss_total";
        var badTest = AppDetectors.InterpretTest(Bash(AppDetectors.RuleTestScript(kwDir, kwBad)), kwBad);
        check(!badTest.Ok && badTest.Lines.Contains("validating..."), "测试规则：没匹配上时给出文件最后几行");
        var kwNoFile = kw.Clone(); kwNoFile.File = "*.csv";
        check(AppDetectors.InterpretTest(Bash(AppDetectors.RuleTestScript(kwDir, kwNoFile)), kwNoFile).Headline.Contains("*.csv"), "测试规则：文件不存在");
        check(AppDetectors.InterpretTest(Bash(AppDetectors.RuleTestScript(kwDir + "/nope", kw)), kw).Headline.Contains("目录"), "测试规则：目录不存在");

        var re = new ProgressRule { Name = "MySim", File = "progress.txt", Match = RuleMatch.Regex, Pattern = @"iter=(\d+).*target=(\d+)" };
        var r = Detect(Make("custom-re", new() { ["progress.txt"] = "start\niter=1234 of target=5000\n" }), [re]);
        check(r?.Current == 1234 && r.Total == 5000, "自定义规则：正则捕获组");

        var tf = new ProgressRule { Name = "Solver", File = "run.log", Pattern = "step", TotalFile = "config.ini", TotalKeyword = "max_steps" };
        var t = Detect(Make("custom-tf", new() { ["run.log"] = "step 100\nstep 120 residual 1e-3\n", ["config.ini"] = "[run]\nmax_steps = 1000\n" }), [tf]);
        check(t?.Current == 120 && t.Total == 1000 && t.EndMarker == null, "自定义规则：从另一个文件读目标值");

        check(Detect(Make("custom-over", new() { ["train.log"] = "Epoch 120/100\n" }), [kw]) == null, "自定义规则：当前值超过目标值时不显示");

        // 自定义规则排在内置识别前面：同一个目录里既有 log.lammps 也匹配规则时，用规则
        var lr = new ProgressRule { Name = "My LAMMPS", File = "log.lammps", Pattern = "run" };
        check(Detect(Make("lmp-rule", new() { ["log.lammps"] = lmpHead }), [lr])?.Kind == AppKind.Custom, "自定义规则优先于内置识别");

        // MARK: 详情页曲线（增量读取）

        var lossDir = Make("curve-nep", new()
        {
            ["loss.out"] = "100 0.30 0.01 0.02 0.20 0.50 0.10 0 0 0\n200 0.20 0.01 0.02 0.10 0.40 0.08 0 0 0\n300 0.15 0.01 0.02 0.08 0.30 0.06 0 0 0\n",
        });
        var lossFile = lossDir + "/loss.out";
        var lossSrc = new AppCurves.Source(lossFile, AppCurves.CurveKind.Loss);
        var cache = AppCurves.Merge(Bash(AppCurves.Script(lossSrc, 0, "")), new CurveCache());
        check(cache?.Rows.Count == 3 && cache.LineCount == 3, "曲线：第一次读取全部行");
        // 追加两行完整的 + 半行（还没写完）
        File.AppendAllText(lossFile, "400 0.12 0.01 0.02 0.07 0.25 0.05 0 0 0\n500 0.10 0.01 0.02 0.06 0.20 0.04 0 0 0\n600 0.0");
        var incOut = Bash(AppCurves.Script(lossSrc, cache?.LineCount ?? 0, cache?.Head ?? ""));
        cache = cache == null ? null : AppCurves.Merge(incOut, cache);
        check(!incOut.Contains("reset=1") && incOut.Split("row=").Length - 1 == 2 && cache?.Rows.Count == 5 && cache.Rows[^1][0] == 500,
              "曲线：增量只读新增的完整行（半行不读）");
        var c = cache == null ? null : AppCurves.Curve(lossSrc, cache.Rows);
        check(c?.LogY == true && c.SeriesList.Select(s => s.Name).SequenceEqual(["Total", "能量", "力", "维里"]) && c.SeriesList[0].Points.Count == 5, "曲线：NEP loss 各列");
        // 重新训练：文件被重写，第一行变了
        File.WriteAllText(lossFile, "100 0.90 0.01 0.02 0.20 0.50 0.10 0 0 0\n200 0.80 0.01 0.02 0.10 0.40 0.08 0 0 0\n");
        cache = cache == null ? null : AppCurves.Merge(Bash(AppCurves.Script(lossSrc, cache.LineCount, cache.Head)), cache);
        check(cache?.Rows.Count == 2 && cache.Rows[0][1] == 0.9, "曲线：文件重写后从头读");

        var oszDir = Make("curve-vasp", new() { ["OSZICAR"] = oszRelax });
        var vSrc = new AppCurves.Source(oszDir + "/OSZICAR", AppCurves.CurveKind.VaspEnergy);
        var vCache = AppCurves.Merge(Bash(AppCurves.Script(vSrc, 0, "")), new CurveCache());
        var vc = vCache == null ? null : AppCurves.Curve(vSrc, vCache.Rows);
        check(vCache?.Rows.Count == 3 && vCache.Rows[^1].SequenceEqual([3.0, -440.0]) && vc?.LogY == false, "曲线：VASP 离子步 E0");
        check(AppCurves.Merge(Bash(AppCurves.Script(new AppCurves.Source(lossDir + "/nope", AppCurves.CurveKind.Loss), 0, "")), new CurveCache()) == null, "曲线：文件不存在");

        // NEP 续跑：loss.out 追加写入，代数从头数起；横轴按行累加（新版 GPUMD 每段开头有 # output_interval）
        const string nepCols = "# columns generation total L1 L2 rmse_energy_train rmse_force_train rmse_virial_train rmse_energy_test rmse_force_test rmse_virial_test\n";
        var restartDir = Make("curve-nep-restart", new()
        {
            ["nep.in"] = "generation 1000\n",
            ["loss.out"] = "100 0.30 0.01 0.02 0.20 0.50 0.10 0 0 0\n200 0.20 0.01 0.02 0.10 0.40 0.08 0 0 0\n100 0.18 0.01 0.02 0.09 0.35 0.07 0 0 0\n"
                + "# format_version 1\n# output_interval 50\n" + nepCols + "50 0.15 0.01 0.02 0.08 0.30 0.06 0 0 0\n# format_version 1\n# output_interval 50\n" + nepCols,
        });
        var rSrc = new AppCurves.Source(restartDir + "/loss.out", AppCurves.CurveKind.Loss);
        var rCache = AppCurves.Merge(Bash(AppCurves.Script(rSrc, 0, "")), new CurveCache());
        var rc = rCache == null ? null : AppCurves.Curve(rSrc, rCache.Rows, rCache.Extra);
        check(rc != null && rc.SeriesList[0].Points.Select(p => p.Item1).SequenceEqual([100.0, 200, 300, 350]), "曲线：NEP 续跑按行累加代数（每段的 output_interval）");
        var rNep = Detect(restartDir);
        check(rNep?.Kind == AppKind.Nep && rNep.Current == 50 && rNep.Total == 1000, "NEP：刚续跑、末尾是 # 表头时取最后一行数据");

        // TorchNEP：loss.out 在输出目录（默认 output/）里，带表头；总 epoch 取自 output.log
        const string tnHeader = "# epoch  loss  rmse_e(eV/atom)  rmse_f(eV/A)  rmse_v(eV/atom)  rmse_stress(GPa)\n";
        var tnDir = Make("torchnep", new()
        {
            ["nep.in"] = "type 4 Li La Zr O\nepoch 300\n",
            ["output/loss.out"] = tnHeader + "1 5.16e-01 0.068 0.717 0.404 5.51\n2 2.65e-01 0.201 0.514 0.284 3.82\n",
            ["output/output.log"] = "torchnep v1.0.7\nTraining: epochs 1..200, Stage 2 from epoch 100 (SWA=off)\nEpoch    1 | loss 5.1618e-01\n",
        });
        var tn = Detect(tnDir);
        check(tn?.Kind == AppKind.TorchNep && tn.Current == 2 && tn.Total == 200 && tn.DataFile == "output/loss.out"
              && tn.Losses.Select(l => l.Label).SequenceEqual(["Loss", "E (eV/atom)", "F (eV/A)", "V (eV/atom)", "S (GPa)"]) && tn.EndMarker == null,
              "TorchNEP：output/loss.out + output.log");
        File.WriteAllText(tnDir + "/output/output.log", "Done. Best loss: 2.65e-01\nTotal time: 00:00:26.9\n");
        var tnDone = Detect(tnDir);
        check(tnDone?.EndMarker == true && tnDone.Total == 300 && tnDone.Finished, "TorchNEP：Done 结束标记；没有 Training 行时用 nep.in 的 epoch");
        var tnHere = Detect(Make("torchnep-here", new() { ["nep.in"] = "epoch 50\n", ["loss.out"] = tnHeader + "7 1e-02 0.01 0.2 0.05 0.5\n" }));
        check(tnHere?.Kind == AppKind.TorchNep && tnHere.Current == 7 && tnHere.Total == 50, "TorchNEP：输出在工作目录里时不当成 NEP");
        var tnSrc = new AppCurves.Source(tnDir + "/output/loss.out", AppCurves.CurveKind.Loss, AppKind.TorchNep);
        File.WriteAllText(tnDir + "/output/loss.out", tnHeader + "1 0.5 0.06 0.7 0.4 5\n2 0.3 0.05 0.5 0.3 4\n3 0.2 0.04 0.4 0.2 3\n2 0.25 0.05 0.45 0.25 3.5\n3 0.15 0.03 0.3 0.15 2\n");
        var tnCache = AppCurves.Merge(Bash(AppCurves.Script(tnSrc, 0, "")), new CurveCache());
        var tnc = tnCache == null ? null : AppCurves.Curve(tnSrc, tnCache.Rows, tnCache.Extra);
        check(tnc != null && tnc.SeriesList.Select(x => x.Name).SequenceEqual(["Loss", "E", "F", "V"]) && tnc.SeriesList[0].Points.Select(p => p.Item1).SequenceEqual([1.0, 2, 3])
              && tnc.SeriesList[0].Points[^1].Item2 == 0.15 && tnc.XLabel == "epoch", "曲线：TorchNEP（从检查点续跑时去掉重跑的那段）");

        // DeePMD（PyTorch 后端）：lcurve.out 带表头，续跑时表头会再写一遍；num_epochs 换算后的总步数只在日志里
        const string dpHeader = "# step      mae_e_val   mae_e_trn     mae_f_val   mae_f_trn     mae_v_val   mae_v_trn      rmse_val    rmse_trn   lr\n# If there is no available reference data, rmse_*_{val,trn} will print nan\n";
        var dpDir = Make("deepmd", new()
        {
            ["input.json"] = """{"training": {"num_epochs": 100, "disp_file": "lcurve.out"}}""",
            ["lcurve.out"] = dpHeader + "      1      2.79e-01    3.64e-01      3.00e+00    4.86e+00      2.82e-01    4.39e-01      7.93e+00    1.03e+01    6.0e-05\n"
                + "    500      1.20e-01    1.30e-01      9.00e-01    9.50e-01      1.00e-01    1.10e-01      4.00e+00    4.20e+00    1.0e-04\n"
                + dpHeader + "  20000      1.26e-02    1.83e-02      1.90e-01    1.73e-01      2.52e-02    2.02e-02      2.00e+00    1.92e+00    2.9e-04\n",
            ["val.log"] = "#  step   E_MAE(meV/atom)\n   10000      113.47\n",
            ["log"] = "[2026-11-06 20:04:32,900] DEEPMD INFO    Computed num_steps=3016700 from num_epoch=100.0 and total_numb_batch=30167.\n[2026-11-06 20:04:32,933] DEEPMD INFO    Start to train 3016700 steps.\n[2026-11-06 20:05:00,000] DEEPMD INFO    Batch   20000: total wall time = 64.75 s\n",
        });
        var dp = Detect(dpDir);
        check(dp?.Kind == AppKind.DeepMd && dp.Current == 20000 && dp.Total == 3016700 && dp.HasTarget && dp.DataFile == "lcurve.out"
              && dp.Losses.Take(3).Select(l => l.Label).SequenceEqual(["MAE E val", "MAE F val", "MAE V val"]) && dp.Losses[^1].Label == "lr"
              && dp.Losses[0].V == 0.0126 && dp.EndMarker == null,
              "DeePMD：lcurve.out（续跑时重复的表头）+ 日志里的总步数");
        File.WriteAllText(dpDir + "/log2.log", "[2026-11-11 09:00:00,000] DEEPMD INFO    Trained model has been saved to: model.ckpt\n");
        File.Delete(dpDir + "/log");
        var dpDone = Detect(dpDir);
        check(dpDone?.Kind == AppKind.DeepMd && dpDone.Total == null && !dpDone.HasTarget && dpDone.Current == 20000 && dpDone.EndMarker == true && dpDone.Finished,
              "DeePMD：日志里没有总步数时只显示当前步；Trained model has been saved 是结束标记");
        var dpTF = Detect(Make("deepmd-tf", new()
        {
            ["input.json"] = "{\n  \"training\": {\n    \"numb_steps\": 1000000,\n    \"disp_freq\": 100\n  }\n}\n",
            ["lcurve.out"] = "#  step      rmse_trn    rmse_e_trn    rmse_f_trn         lr\n      0      2.21e+01      1.34e+00      6.98e-01    1.0e-03\n    100      1.03e+01      nan      4.11e-01    1.0e-03\n",
            ["train.log"] = "DEEPMD INFO    batch    1000 training time 5.2 s\nDEEPMD INFO    average training time: 0.0520 s/batch\n",
        }));
        check(dpTF?.Current == 100 && dpTF.Total == 1_000_000 && dpTF.Losses.Select(l => l.Label).SequenceEqual(["RMSE F trn", "RMSE trn", "lr"]) && dpTF.EndMarker == true,
              "DeePMD（TensorFlow）：numb_steps、nan 不显示、average training time 结束标记");
        var dpSrc = new AppCurves.Source(dpDir + "/lcurve.out", AppCurves.CurveKind.Loss, AppKind.DeepMd);
        var dpCache = AppCurves.Merge(Bash(AppCurves.Script(dpSrc, 0, "")), new CurveCache());
        var dpc = dpCache == null ? null : AppCurves.Curve(dpSrc, dpCache.Rows, dpCache.Extra);
        check(dpc != null && dpc.SeriesList.Select(x => x.Name).SequenceEqual([Lang.L("能量", "Energy"), Lang.L("力", "Force"), Lang.L("维里", "Virial")])
              && dpc.SeriesList[0].Points.Select(p => p.Item1).SequenceEqual([1.0, 500, 20000]) && dpc.SeriesList[1].Points[^1].Item2 == 0.19 && dpc.LogY,
              "曲线：DeePMD lcurve.out（验证集的 E / F / V）");

        // MACE：logs/<名字>_run-<种子>.log；总 epoch 取自 _debug.log 的参数，正在跑的 epoch 取自 results/*_train.txt
        var maceLog = """
            2026-01-14 20:25:46.805 INFO: ===========VERIFYING SETTINGS===========
            2026-01-14 20:25:46.805 INFO: MACE version: 0.3.15
            2026-01-14 20:26:10.051 INFO: Initial: head: Default, loss=93.68933105, RMSE_E_per_atom=  288.65 meV, RMSE_F=  917.99 meV / A
            2026-01-14 20:27:19.883 INFO: Epoch 0: head: Default, loss=2.62567067, RMSE_E_per_atom=    3.04 meV, RMSE_F=  161.12 meV / A
            2026-01-14 20:28:54.625 INFO: Epoch 5: head: Default, loss=0.25760791, RMSE_E_per_atom=    6.65 meV, RMSE_F=   50.07 meV / A
            2026-01-14 20:30:17.603 INFO: Epoch 10: head: Default, loss=0.13423754, RMSE_E_per_atom=    2.73 meV, RMSE_F=   36.35 meV / A

            """;
        var maceDir = Make("mace", new()
        {
            ["logs/LLZO_run-123.log"] = maceLog,
            ["logs/LLZO_run-123_debug.log"] = "DEBUG: Configuration: Namespace(config='config.yaml', max_num_epochs=600, patience=40)\nINFO: Epoch 10: x\n",
            ["results/LLZO_run-123_train.txt"] = """{"loss": 0.07, "time": 0.088, "mode": "opt", "epoch": 13}""" + "\n",
        });
        var mace = Detect(maceDir);
        check(mace?.Kind == AppKind.Mace && mace.Current == 13 && mace.Total == 600 && mace.DataFile == "logs/LLZO_run-123.log"
              && mace.Losses.Select(l => l.Label).SequenceEqual(["Loss", "RMSE E (meV)", "RMSE F (meV/Å)"]) && mace.Losses[^1].V == 36.35 && mace.EndMarker == null,
              "MACE：日志 + debug 日志 + results");
        File.WriteAllText(maceDir + "/logs/LLZO_run-123.log", maceLog + "2026-01-14 23:14:21.092 INFO: Training complete\n2026-01-14 23:14:41.335 INFO: Done\n");
        Directory.Delete(maceDir + "/results", true);
        var maceDone = Detect(maceDir);
        check(maceDone?.Current == 11 && maceDone.EndMarker == true && maceDone.Finished, "MACE：Training complete 结束标记（提前停止也算完成）");
        check(Detect(Make("mace-not", new() { ["logs/run.log"] = "hello\n" })) == null, "MACE：logs 里不是 MACE 的日志不识别");
        var mSrc = new AppCurves.Source(maceDir + "/logs/LLZO_run-123.log", AppCurves.CurveKind.Loss, AppKind.Mace);
        var mCache = AppCurves.Merge(Bash(AppCurves.Script(mSrc, 0, "")), new CurveCache());
        var mc = mCache == null ? null : AppCurves.Curve(mSrc, mCache.Rows, mCache.Extra);
        check(mCache?.Rows.Count == 3 && mc != null && mc.SeriesList.Select(x => x.Name).SequenceEqual(["Loss", "RMSE E", "RMSE F"])
              && mc.SeriesList[^1].Points.Select(p => p.Item1).SequenceEqual([0.0, 5, 10]), "曲线：MACE 验证集误差（跳过 Initial）");

        // 完整的轮询脚本（含两条规则）语法正确
        var saved = ProgressRule.Active;
        ProgressRule.Active = [kw, tf];
        bool syntaxOK = true;
        foreach (var kind in new[] { HostKind.Slurm, HostKind.Direct })
            if (!SelfTest.Bash(AppDetectors.Script(new HostConfig("x", kind: kind)), "-n").Trim().Equals("")) syntaxOK = false;
        ProgressRule.Active = saved;
        check(syntaxOK, "轮询脚本（含自定义规则）bash 语法检查");

        check(ProgressRule.IsSafeFileName("logs/*.log") && !ProgressRule.IsSafeFileName("a;rm -rf ~")
              && !ProgressRule.IsSafeFileName("../x") && !ProgressRule.IsSafeFileName("$(id)"), "规则文件名只允许安全字符");

        // 文件没变时跳过重读：si_same 只在目录和签名都对上时成立；same=1 解析成"沿用上次"，sig= 记成签名
        var same = AppDetectors.SameFunction(new Dictionary<string, string> { ["/w/it's a|b"] = "1.2 3.4 ", ["/w/x"] = "9.9 " }) + """

            si_same "/w/it's a|b" "1.2 3.4 " && echo hit1; si_same "/w/it's a|b" "1.2 3.5 " || echo miss1
            si_same "/w/y" "9.9 " || echo miss2; si_same "/w/x" "" || echo miss3

            """;
        check(Bash(same).Trim().Split('\n').SequenceEqual(["hit1", "miss1", "miss2", "miss3"]), "跳过重读：签名比较（目录带引号和 |）");
        var skip = AppDetectors.Parse(["@@SI:DIR 7|/w/a", "kind=vasp", "same=1", "@@SI:DIR 8|/w/b", "kind=lammps", "sig=5.6 ",
                                       "total=100", "first=0", "last=50", "bad=0", "@@SI:DIR 9|/w/c", "kind=lammps", "sig=",
                                       "total=100", "first=0", "last=50", "bad=0"]);
        check(skip.GetValueOrDefault("7") is { Unchanged: true, Dir: "/w/a" } && skip.GetValueOrDefault("8") is { Signature: "5.6 ", Current: 50 }
              && skip.GetValueOrDefault("9") is { Signature: null }, "跳过重读：解析 same=1 和签名");
    }
}
