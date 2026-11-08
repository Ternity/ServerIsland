import Foundation

/// 计算软件识别的自检：在本机临时目录里造假的 VASP / LAMMPS / 自定义输出，
/// 用本机 bash 真正执行生成的远端脚本，再解析结果。
/// （macOS 自带的是 BSD awk / grep，顺便检查脚本没有用到 GNU 专有写法。）
enum SelfTestApps {
    static func run(_ check: (Bool, String) -> Void) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("si-selftest-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }

        func make(_ dir: String, _ files: [String: String]) -> String {
            let d = root.appendingPathComponent(dir)
            try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
            for (name, text) in files {
                let url = d.appendingPathComponent(name)
                try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try? text.write(to: url, atomically: true, encoding: .utf8)
            }
            return d.path
        }

        func detect(_ dir: String, rules: [ProgressRule] = [], builtin: Bool = true) -> AppProgress? {
            let lines = bash(AppDetectors.directoryScript(id: "t", dir: dir, rules: rules, builtin: builtin)).components(separatedBy: "\n")
            return AppDetectors.parse(lines, rules: rules)["t"]
        }

        func bash(_ script: String) -> String {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/bash")
            p.arguments = ["-s"]
            let input = Pipe(), output = Pipe()
            p.standardInput = input
            p.standardOutput = output
            p.standardError = FileHandle.nullDevice
            do { try p.run() } catch { return "" }
            input.fileHandleForWriting.write(script.data(using: .utf8)!)
            try? input.fileHandleForWriting.close()
            let data = output.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            return String(decoding: data, as: UTF8.self)
        }

        // MARK: VASP

        let oszMD = """
               N       E                     dE             d eps       ncg     rms          rms(c)
        DAV:   1     0.123E+03    0.123E+03   -0.5E+03  1000   0.1E+02
        RMM:   2    -0.101E+03   -0.22E+03   -0.3E+02  1200   0.4E+01
           1 T=   300. E= -.10E+03 F= -.11E+03 E0= -.11E+03  EK= 0.1E+01 SP= 0.0E+00 SK= 0.0E+00
        DAV:   1    -0.111E+03   -0.1E-01   -0.3E-01  1000   0.2E+00
           2 T=   310. E= -.10E+03 F= -.11E+03 E0= -.11E+03  EK= 0.1E+01 SP= 0.0E+00 SK= 0.0E+00
        RMM:   3    -0.111E+03   -0.1E-03   -0.1E-03  1000   0.1E-01

        """
        let md = detect(make("vasp-md", ["INCAR": "SYSTEM = test\nIBRION = 0 ; NSW = 500   ! MD\nPOTIM = 1\n",
                                         "OSZICAR": oszMD, "OUTCAR": " running on 4 nodes\n"]))
        check(md?.vaspMode == .md && md?.current == 2 && md?.total == 500 && md?.endMarker == false, "VASP MD：离子步 / NSW")

        let oszRelax = """
        DAV:   1     0.4E+03    0.4E+03   -0.1E+04  1000   0.1E+02
           1 F= -.43E+03 E0= -.43E+03  d E =-.43E+03  mag=     2.0
        RMM:   5    -0.43E+03   -0.1E-04   -0.1E-04  1000   0.1E-01
           2 F= -.44E+03 E0= -.44E+03  d E =-.1E+01  mag=     2.0
           3 F= -.44E+03 E0= -.44E+03  d E =-.1E-02  mag=     2.0

        """
        let relax = detect(make("vasp-relax", ["INCAR": "ibrion=2; nsw=200\nEDIFFG = -0.01 # force\n", "OSZICAR": oszRelax,
                                               "OUTCAR": "...\n General timing and accounting informations for this job:\n Total CPU time used (sec): 1.0\n"]))
        check(relax?.vaspMode == .relax && relax?.current == 3 && relax?.total == nil && relax?.fraction == nil
              && relax?.endMarker == true && relax?.finished == true, "VASP 结构优化：只显示离子步（小写、分号、注释）")

        let scf = detect(make("vasp-scf", ["INCAR": "ENCUT = 500\n", "OSZICAR": "DAV:   1  0.1E+03\nDAV:  11  0.1E+03\nRMM:  12  0.1E+03\n"]))
        check(scf?.vaspMode == .scf && scf?.current == 12 && scf?.endMarker == nil, "VASP 单点：电子步")

        let phonon = detect(make("vasp-phonon", ["INCAR": "IBRION = 6\nNSW = 1\n", "OSZICAR": oszRelax]))
        check(phonon == nil, "VASP 声子（IBRION=6）不识别")
        let mdOver = detect(make("vasp-md-bad", ["INCAR": "IBRION = 0\nNSW = 1\n", "OSZICAR": oszMD]))
        check(mdOver == nil, "VASP MD 离子步超过 NSW 时不显示")

        // MARK: LAMMPS

        let lmpHead = """
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

        """
        let lmp = detect(make("lmp-run", ["log.lammps": lmpHead]))
        check(lmp?.kind == .lammps && lmp?.current == 200 && lmp?.total == 1000 && lmp?.endMarker == false, "LAMMPS：最后一段 run 的进度")

        let lmpDone = lmpHead + """
               1500   300           -3.5            -3.4            12
        Loop time of 12.3 on 4 procs for 1000 steps with 1000 atoms
        Nlocal:           1000 ave        1000 max        1000 min
        Total # of neighbors = 64000
        Total wall time: 0:00:13

        """
        let done = detect(make("lmp-done", ["log.lammps": lmpDone]))
        check(done?.current == 1000 && done?.endMarker == true && done?.finished == true, "LAMMPS：Total wall time 判定正常结束")

        let lmpMin = lmpDone.replacingOccurrences(of: "Total wall time: 0:00:13\n", with: "minimize 1e-6 1e-8 1000 10000\n   Step  Temp  E_pair\n  1500 0 -3.6\n")
        check(detect(make("lmp-min", ["log.lammps": lmpMin])) == nil, "LAMMPS：run 之后接 minimize 时不显示")
        let lmpVar = "LAMMPS\nvariable n equal 1000\nrun ${n}\n   Step Temp\n  0 300\n  100 300\n"
        check(detect(make("lmp-var", ["log.lammps": lmpVar])) == nil, "LAMMPS：run 步数是变量且没有展开时不显示")
        let lmpMulti = "LAMMPS\nthermo_style multi\nrun 1000\n------------ Step              0 ----- CPU =      0.0 (sec) ----\nTotEng   =     -3.4 KinEng   =      0.1\n"
        check(detect(make("lmp-multi", ["log.lammps": lmpMulti])) == nil, "LAMMPS：thermo_style multi 不显示")

        // MARK: 自定义规则

        var kw = ProgressRule()
        kw.name = "PyTorch"; kw.file = "*.log"; kw.pattern = "epoch"; kw.unit = "epoch"; kw.doneMarker = "Training finished"
        let kwDir = make("custom-kw", ["train.log": "Epoch 36/100 loss 0.5\nEpoch 37/100 loss 0.4\nvalidating...\n"])
        let k = detect(kwDir, rules: [kw])
        check(k?.kind == .custom && k?.title == "PyTorch" && k?.current == 37 && k?.total == 100 && k?.endMarker == false,
              "自定义规则：关键字 + 自动识别 a/b")
        check(detect(kwDir, rules: [kw], builtin: false)?.current == 37, "自定义规则：只测试这一条（不跑内置识别）")

        let okTest = AppDetectors.interpretTest(bash(AppDetectors.ruleTestScript(dir: kwDir, rule: kw)), rule: kw)
        check(okTest.ok && okTest.headline.contains("37") && okTest.headline.contains("100"), "测试规则：匹配成功")
        var kwBad = kw; kwBad.pattern = "loss_total"
        let badTest = AppDetectors.interpretTest(bash(AppDetectors.ruleTestScript(dir: kwDir, rule: kwBad)), rule: kwBad)
        check(!badTest.ok && badTest.lines.contains("validating..."), "测试规则：没匹配上时给出文件最后几行")
        var kwNoFile = kw; kwNoFile.file = "*.csv"
        check(AppDetectors.interpretTest(bash(AppDetectors.ruleTestScript(dir: kwDir, rule: kwNoFile)), rule: kwNoFile).headline.contains("*.csv"),
              "测试规则：文件不存在")
        check(AppDetectors.interpretTest(bash(AppDetectors.ruleTestScript(dir: kwDir + "/nope", rule: kw)), rule: kw).headline.contains("目录"),
              "测试规则：目录不存在")

        var re = ProgressRule()
        re.name = "MySim"; re.file = "progress.txt"; re.match = .regex; re.pattern = #"iter=(\d+).*target=(\d+)"#
        let r = detect(make("custom-re", ["progress.txt": "start\niter=1234 of target=5000\n"]), rules: [re])
        check(r?.current == 1234 && r?.total == 5000, "自定义规则：正则捕获组")

        var tf = ProgressRule()
        tf.name = "Solver"; tf.file = "run.log"; tf.pattern = "step"; tf.totalFile = "config.ini"; tf.totalKeyword = "max_steps"
        let t = detect(make("custom-tf", ["run.log": "step 100\nstep 120 residual 1e-3\n", "config.ini": "[run]\nmax_steps = 1000\n"]), rules: [tf])
        check(t?.current == 120 && t?.total == 1000 && t?.endMarker == nil, "自定义规则：从另一个文件读目标值")

        let over = detect(make("custom-over", ["train.log": "Epoch 120/100\n"]), rules: [kw])
        check(over == nil, "自定义规则：当前值超过目标值时不显示")

        // 自定义规则排在内置识别前面：同一个目录里既有 log.lammps 也匹配规则时，用规则
        var lr = ProgressRule()
        lr.name = "My LAMMPS"; lr.file = "log.lammps"; lr.pattern = "run"
        check(detect(make("lmp-rule", ["log.lammps": lmpHead]), rules: [lr])?.kind == .custom, "自定义规则优先于内置识别")

        // MARK: 详情页曲线（增量读取）

        let lossDir = make("curve-nep", ["loss.out": """
        100 0.30 0.01 0.02 0.20 0.50 0.10 0 0 0
        200 0.20 0.01 0.02 0.10 0.40 0.08 0 0 0
        300 0.15 0.01 0.02 0.08 0.30 0.06 0 0 0

        """])
        let lossFile = lossDir + "/loss.out"
        let lossSrc = AppCurves.Source.loss(path: lossFile, kind: .nep)
        var cache = AppCurves.merge(bash(AppCurves.script(lossSrc, offset: 0, head: "")), into: CurveCache())
        check(cache?.rows.count == 3 && cache?.lineCount == 3, "曲线：第一次读取全部行")
        // 追加两行完整的 + 半行（还没写完）
        if let h = FileHandle(forWritingAtPath: lossFile) {
            h.seekToEndOfFile()
            h.write("400 0.12 0.01 0.02 0.07 0.25 0.05 0 0 0\n500 0.10 0.01 0.02 0.06 0.20 0.04 0 0 0\n600 0.0".data(using: .utf8)!)
            try? h.close()
        }
        let incOut = bash(AppCurves.script(lossSrc, offset: cache?.lineCount ?? 0, head: cache?.head ?? ""))
        cache = cache.flatMap { AppCurves.merge(incOut, into: $0) }
        check(!incOut.contains("reset=1") && incOut.components(separatedBy: "row=").count - 1 == 2 && cache?.rows.count == 5
              && cache?.rows.last?.first == 500, "曲线：增量只读新增的完整行（半行不读）")
        let c = cache.flatMap { AppCurves.curve(lossSrc, rows: $0.rows) }
        check(c?.logY == true && c?.series.map(\.name) == ["Total", "能量", "力", "维里"] && c?.series.first?.points.count == 5,
              "曲线：NEP loss 各列")
        // 重新训练：文件被重写，第一行变了
        try? "100 0.90 0.01 0.02 0.20 0.50 0.10 0 0 0\n200 0.80 0.01 0.02 0.10 0.40 0.08 0 0 0\n".write(toFile: lossFile, atomically: true, encoding: .utf8)
        cache = cache.flatMap { AppCurves.merge(bash(AppCurves.script(lossSrc, offset: $0.lineCount, head: $0.head)), into: $0) }
        check(cache?.rows.count == 2 && cache?.rows.first?[1] == 0.9, "曲线：文件重写后从头读")

        let oszDir = make("curve-vasp", ["OSZICAR": oszRelax])
        let vSrc = AppCurves.Source.vaspEnergy(path: oszDir + "/OSZICAR")
        let vCache = AppCurves.merge(bash(AppCurves.script(vSrc, offset: 0, head: "")), into: CurveCache())
        let vc = vCache.flatMap { AppCurves.curve(vSrc, rows: $0.rows) }
        check(vCache?.rows.count == 3 && vCache?.rows.last == [3, -440] && vc?.logY == false, "曲线：VASP 离子步 E0")
        check(AppCurves.merge(bash(AppCurves.script(.loss(path: lossDir + "/nope", kind: .nep), offset: 0, head: "")), into: CurveCache()) == nil,
              "曲线：文件不存在")

        // NEP 续跑：loss.out 追加写入，代数从头数起；横轴按行累加（新版 GPUMD 每段开头有 # output_interval）
        let restartDir = make("curve-nep-restart", ["nep.in": "generation 1000\n", "loss.out": """
        100 0.30 0.01 0.02 0.20 0.50 0.10 0 0 0
        200 0.20 0.01 0.02 0.10 0.40 0.08 0 0 0
        100 0.18 0.01 0.02 0.09 0.35 0.07 0 0 0
        # format_version 1
        # output_interval 50
        # columns generation total L1 L2 rmse_energy_train rmse_force_train rmse_virial_train rmse_energy_test rmse_force_test rmse_virial_test
        50 0.15 0.01 0.02 0.08 0.30 0.06 0 0 0
        # format_version 1
        # output_interval 50
        # columns generation total L1 L2 rmse_energy_train rmse_force_train rmse_virial_train rmse_energy_test rmse_force_test rmse_virial_test

        """])
        let rSrc = AppCurves.Source.loss(path: restartDir + "/loss.out", kind: .nep)
        let rCache = AppCurves.merge(bash(AppCurves.script(rSrc, offset: 0, head: "")), into: CurveCache())
        let rc = rCache.flatMap { AppCurves.curve(rSrc, rows: $0.rows, extra: $0.extra) }
        check(rc?.series.first?.points.map(\.x) == [100, 200, 300, 350], "曲线：NEP 续跑按行累加代数（每段的 output_interval）")
        let rNep = detect(restartDir)
        check(rNep?.kind == .nep && rNep?.current == 50 && rNep?.total == 1000, "NEP：刚续跑、末尾是 # 表头时取最后一行数据")

        // TorchNEP：loss.out 在输出目录（默认 output/）里，带表头；总 epoch 取自 output.log
        let tnHeader = "# epoch  loss  rmse_e(eV/atom)  rmse_f(eV/A)  rmse_v(eV/atom)  rmse_stress(GPa)\n"
        let tnDir = make("torchnep", ["nep.in": "type 4 Li La Zr O\nepoch 300\n",
                                      "output/loss.out": tnHeader + "1 5.16e-01 0.068 0.717 0.404 5.51\n2 2.65e-01 0.201 0.514 0.284 3.82\n",
                                      "output/output.log": "torchnep v1.0.7\nTraining: epochs 1..200, Stage 2 from epoch 100 (SWA=off)\nEpoch    1 | loss 5.1618e-01\n"])
        let tn = detect(tnDir)
        check(tn?.kind == .torchnep && tn?.current == 2 && tn?.total == 200 && tn?.dataFile == "output/loss.out"
              && tn?.losses.map(\.label) == ["Loss", "E (eV/atom)", "F (eV/A)", "V (eV/atom)", "S (GPa)"] && tn?.endMarker == nil,
              "TorchNEP：output/loss.out + output.log")
        try? "Done. Best loss: 2.65e-01\nTotal time: 00:00:26.9\n".write(toFile: tnDir + "/output/output.log", atomically: true, encoding: .utf8)
        let tnDone = detect(tnDir)
        check(tnDone?.endMarker == true && tnDone?.total == 300 && tnDone?.finished == true, "TorchNEP：Done 结束标记；没有 Training 行时用 nep.in 的 epoch")
        let tnHere = detect(make("torchnep-here", ["nep.in": "epoch 50\n", "loss.out": tnHeader + "7 1e-02 0.01 0.2 0.05 0.5\n"]))
        check(tnHere?.kind == .torchnep && tnHere?.current == 7 && tnHere?.total == 50, "TorchNEP：输出在工作目录里时不当成 NEP")
        let tnSrc = AppCurves.Source.loss(path: tnDir + "/output/loss.out", kind: .torchnep)
        try? (tnHeader + "1 0.5 0.06 0.7 0.4 5\n2 0.3 0.05 0.5 0.3 4\n3 0.2 0.04 0.4 0.2 3\n2 0.25 0.05 0.45 0.25 3.5\n3 0.15 0.03 0.3 0.15 2\n")
            .write(toFile: tnDir + "/output/loss.out", atomically: true, encoding: .utf8)
        let tnCache = AppCurves.merge(bash(AppCurves.script(tnSrc, offset: 0, head: "")), into: CurveCache())
        let tnc = tnCache.flatMap { AppCurves.curve(tnSrc, rows: $0.rows, extra: $0.extra) }
        check(tnc?.series.map(\.name) == ["Loss", "E", "F", "V"] && tnc?.series.first?.points.map(\.x) == [1, 2, 3]
              && tnc?.series.first?.points.last?.y == 0.15 && tnc?.xLabel == "epoch", "曲线：TorchNEP（从检查点续跑时去掉重跑的那段）")

        // DeePMD（PyTorch 后端）：lcurve.out 带表头，续跑时表头会再写一遍；num_epochs 换算后的总步数只在日志里
        let dpHeader = "# step      mae_e_val   mae_e_trn     mae_f_val   mae_f_trn     mae_v_val   mae_v_trn      rmse_val    rmse_trn   lr\n# If there is no available reference data, rmse_*_{val,trn} will print nan\n"
        let dpDir = make("deepmd", ["input.json": #"{"training": {"num_epochs": 100, "disp_file": "lcurve.out"}}"#,
                                    "lcurve.out": dpHeader + "      1      2.79e-01    3.64e-01      3.00e+00    4.86e+00      2.82e-01    4.39e-01      7.93e+00    1.03e+01    6.0e-05\n"
                                        + "    500      1.20e-01    1.30e-01      9.00e-01    9.50e-01      1.00e-01    1.10e-01      4.00e+00    4.20e+00    1.0e-04\n"
                                        + dpHeader + "  20000      1.26e-02    1.83e-02      1.90e-01    1.73e-01      2.52e-02    2.02e-02      2.00e+00    1.92e+00    2.9e-04\n",
                                    "val.log": "#  step   E_MAE(meV/atom)\n   10000      113.47\n",
                                    "log": "[2026-11-06 20:04:32,900] DEEPMD INFO    Computed num_steps=3016700 from num_epoch=100.0 and total_numb_batch=30167.\n[2026-11-06 20:04:32,933] DEEPMD INFO    Start to train 3016700 steps.\n[2026-11-06 20:05:00,000] DEEPMD INFO    Batch   20000: total wall time = 64.75 s\n"])
        let dp = detect(dpDir)
        check(dp?.kind == .deepmd && dp?.current == 20000 && dp?.total == 3016700 && dp?.hasTarget == true && dp?.dataFile == "lcurve.out"
              && dp?.losses.prefix(3).map(\.label) == ["MAE E val", "MAE F val", "MAE V val"] && dp?.losses.last?.label == "lr"
              && dp?.losses.first?.value == 0.0126 && dp?.endMarker == nil,
              "DeePMD：lcurve.out（续跑时重复的表头）+ 日志里的总步数")
        try? "[2026-11-11 09:00:00,000] DEEPMD INFO    Trained model has been saved to: model.ckpt\n".write(toFile: dpDir + "/log2.log", atomically: true, encoding: .utf8)
        try? FileManager.default.removeItem(atPath: dpDir + "/log")
        let dpDone = detect(dpDir)
        check(dpDone?.kind == .deepmd && dpDone?.total == nil && dpDone?.hasTarget == false && dpDone?.current == 20000
              && dpDone?.endMarker == true && dpDone?.finished == true,
              "DeePMD：日志里没有总步数时只显示当前步；Trained model has been saved 是结束标记")
        let dpTF = detect(make("deepmd-tf", ["input.json": "{\n  \"training\": {\n    \"numb_steps\": 1000000,\n    \"disp_freq\": 100\n  }\n}\n",
                                             "lcurve.out": "#  step      rmse_trn    rmse_e_trn    rmse_f_trn         lr\n      0      2.21e+01      1.34e+00      6.98e-01    1.0e-03\n    100      1.03e+01      nan      4.11e-01    1.0e-03\n",
                                             "train.log": "DEEPMD INFO    batch    1000 training time 5.2 s\nDEEPMD INFO    average training time: 0.0520 s/batch\n"]))
        check(dpTF?.current == 100 && dpTF?.total == 1_000_000 && dpTF?.losses.map(\.label) == ["RMSE F trn", "RMSE trn", "lr"] && dpTF?.endMarker == true,
              "DeePMD（TensorFlow）：numb_steps、nan 不显示、average training time 结束标记")
        let dpSrc = AppCurves.Source.loss(path: dpDir + "/lcurve.out", kind: .deepmd)
        let dpCache = AppCurves.merge(bash(AppCurves.script(dpSrc, offset: 0, head: "")), into: CurveCache())
        let dpc = dpCache.flatMap { AppCurves.curve(dpSrc, rows: $0.rows, extra: $0.extra) }
        check(dpc?.series.map(\.name) == [L("能量", "Energy"), L("力", "Force"), L("维里", "Virial")] && dpc?.series.first?.points.map(\.x) == [1, 500, 20000]
              && dpc?.series[1].points.last?.y == 0.19 && dpc?.logY == true, "曲线：DeePMD lcurve.out（验证集的 E / F / V）")

        // MACE：logs/<名字>_run-<种子>.log；总 epoch 取自 _debug.log 的参数，正在跑的 epoch 取自 results/*_train.txt
        let maceLog = """
        2026-01-14 20:25:46.805 INFO: ===========VERIFYING SETTINGS===========
        2026-01-14 20:25:46.805 INFO: MACE version: 0.3.15
        2026-01-14 20:26:10.051 INFO: Initial: head: Default, loss=93.68933105, RMSE_E_per_atom=  288.65 meV, RMSE_F=  917.99 meV / A
        2026-01-14 20:27:19.883 INFO: Epoch 0: head: Default, loss=2.62567067, RMSE_E_per_atom=    3.04 meV, RMSE_F=  161.12 meV / A
        2026-01-14 20:28:54.625 INFO: Epoch 5: head: Default, loss=0.25760791, RMSE_E_per_atom=    6.65 meV, RMSE_F=   50.07 meV / A
        2026-01-14 20:30:17.603 INFO: Epoch 10: head: Default, loss=0.13423754, RMSE_E_per_atom=    2.73 meV, RMSE_F=   36.35 meV / A

        """
        let maceDir = make("mace", ["logs/LLZO_run-123.log": maceLog,
                                    "logs/LLZO_run-123_debug.log": "DEBUG: Configuration: Namespace(config='config.yaml', max_num_epochs=600, patience=40)\nINFO: Epoch 10: x\n",
                                    "results/LLZO_run-123_train.txt": #"{"loss": 0.07, "time": 0.088, "mode": "opt", "epoch": 13}"# + "\n"])
        let mace = detect(maceDir)
        check(mace?.kind == .mace && mace?.current == 13 && mace?.total == 600 && mace?.dataFile == "logs/LLZO_run-123.log"
              && mace?.losses.map(\.label) == ["Loss", "RMSE E (meV)", "RMSE F (meV/Å)"] && mace?.losses.last?.value == 36.35 && mace?.endMarker == nil,
              "MACE：日志 + debug 日志 + results")
        try? (maceLog + "2026-01-14 23:14:21.092 INFO: Training complete\n2026-01-14 23:14:41.335 INFO: Done\n")
            .write(toFile: maceDir + "/logs/LLZO_run-123.log", atomically: true, encoding: .utf8)
        try? FileManager.default.removeItem(atPath: maceDir + "/results")
        let maceDone = detect(maceDir)
        check(maceDone?.current == 11 && maceDone?.endMarker == true && maceDone?.finished == true, "MACE：Training complete 结束标记（提前停止也算完成）")
        check(detect(make("mace-not", ["logs/run.log": "hello\n"])) == nil, "MACE：logs 里不是 MACE 的日志不识别")
        let mSrc = AppCurves.Source.loss(path: maceDir + "/logs/LLZO_run-123.log", kind: .mace)
        let mCache = AppCurves.merge(bash(AppCurves.script(mSrc, offset: 0, head: "")), into: CurveCache())
        let mc = mCache.flatMap { AppCurves.curve(mSrc, rows: $0.rows, extra: $0.extra) }
        check(mCache?.rows.count == 3 && mc?.series.map(\.name) == ["Loss", "RMSE E", "RMSE F"] && mc?.series.last?.points.map(\.x) == [0, 5, 10],
              "曲线：MACE 验证集误差（跳过 Initial）")

        // 完整的轮询脚本（含两条规则）语法正确
        let saved = ProgressRule.active
        ProgressRule.active = [kw, tf]
        var syntaxOK = true
        for kind in [HostKind.slurm, .direct] {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/bash")
            p.arguments = ["-n"]
            let input = Pipe()
            p.standardInput = input
            p.standardError = FileHandle.nullDevice
            if (try? p.run()) == nil { syntaxOK = false; continue }
            input.fileHandleForWriting.write(AppDetectors.script(host: HostConfig(alias: "x", kind: kind)).data(using: .utf8)!)
            try? input.fileHandleForWriting.close()
            p.waitUntilExit()
            if p.terminationStatus != 0 { syntaxOK = false }
        }
        ProgressRule.active = saved
        check(syntaxOK, "轮询脚本（含自定义规则）bash 语法检查")

        check(ProgressRule.isSafeFileName("logs/*.log") && !ProgressRule.isSafeFileName("a;rm -rf ~")
              && !ProgressRule.isSafeFileName("../x") && !ProgressRule.isSafeFileName("$(id)"), "规则文件名只允许安全字符")

        // 文件没变时跳过重读：si_same 只在目录和签名都对上时成立；same=1 解析成"沿用上次"，sig= 记成签名
        let same = AppDetectors.sameFunction(["/w/it's a|b": "1.2 3.4 ", "/w/x": "9.9 "]) + """

        si_same "/w/it's a|b" "1.2 3.4 " && echo hit1; si_same "/w/it's a|b" "1.2 3.5 " || echo miss1
        si_same "/w/y" "9.9 " || echo miss2; si_same "/w/x" "" || echo miss3

        """
        check(bash(same).split(separator: "\n") == ["hit1", "miss1", "miss2", "miss3"], "跳过重读：签名比较（目录带引号和 |）")
        let skip = AppDetectors.parse(["@@SI:DIR 7|/w/a", "kind=vasp", "same=1", "@@SI:DIR 8|/w/b", "kind=lammps", "sig=5.6 ",
                                       "total=100", "first=0", "last=50", "bad=0", "@@SI:DIR 9|/w/c", "kind=lammps", "sig=",
                                       "total=100", "first=0", "last=50", "bad=0"])
        check(skip["7"]?.unchanged == true && skip["7"]?.dir == "/w/a" && skip["8"]?.signature == "5.6 " && skip["8"]?.current == 50
              && skip["9"]?.signature == nil, "跳过重读：解析 same=1 和签名")
    }
}
