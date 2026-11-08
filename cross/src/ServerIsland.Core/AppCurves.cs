using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// 详情页里的小曲线：NEP / GNEP 的 loss，VASP 结构优化每个离子步的能量和最大力（两张并排）。
/// 只在详情页打开时读取，并且是增量的：记住已经读到第几行和文件第一行，下次只取新增的行；
/// 第一行变了（重新训练）或文件变短时从头读。
public sealed class AppCurve
{
    /// Faint：辅助线（比如"目前最低"），画得淡一些。
    /// Markers：每个点画一个圆点（NEB 能垒图的像点），图例里不显示最后一个值。
    public sealed record Series(string Name, List<(double X, double Y)> Points, bool Faint = false, bool Markers = false);

    public string Title = "";
    public List<Series> SeriesList = [];
    /// loss 跨好几个数量级，用对数坐标。
    public bool LogY;
    public string XLabel = "";
    /// 收敛目标（虚线），比如 VASP 的 |EDIFFG|（EDIFFG < 0 时按力收敛）。
    public double? Threshold;
    /// 图右上角的说明，比如"按能量收敛 ΔE < 0.001 eV"（这时不画目标线）。
    public string? Note;
    /// 柱子（NEB 每个像点的最大力），用自己的纵轴（0 到最大值）；有柱子时 Threshold 画在柱子的纵轴上。
    public List<(double X, double Y)> Bars = [];
    public string BarsLabel = "";
}

/// 增量读取的缓存（每台服务器 + 文件一份）。
public sealed class CurveCache
{
    public int LineCount;
    public string Head = "";
    public List<double[]> Rows = [];
    /// 曲线以外的值（如 ediffg）。
    public Dictionary<string, string> Extra = [];
}

public static class AppCurves
{
    const string Marker = RemoteScript.Marker;
    /// 一次最多取这么多行（第一次打开很长的 loss.out 时只取最后这些）。
    public const int MaxRows = 50_000;

    /// Neb：Path 是 NEB 的顶层目录，读 00…N+1 各像点，一次出两张图。
    public enum CurveKind { Loss, VaspEnergy, VaspForce, Neb }

    /// VaspForce：OUTCAR 每个离子步 TOTAL-FORCE 块里最大的原子受力 |F|；INCAR 的 EDIFFG 作为收敛目标。
    /// Loss：NEP / GNEP / TorchNEP 的 loss.out，MACE 的训练日志（logs/*.log 里每 eval_interval 个 epoch 一行验证集误差），App 说明是哪一种。
    public sealed record Source(string Path, CurveKind Kind, AppKind App = AppKind.Nep)
    {
        public bool IsLoss => Kind == CurveKind.Loss;
    }

    /// 哪些任务有曲线：NEP / GNEP / TorchNEP / MACE 训练、VASP 结构优化（能量 + 最大力）。
    public static List<Source> SourcesFor(AppProgress app) => app.Kind switch
    {
        AppKind.Nep or AppKind.Gnep => [new Source(app.Dir + "/loss.out", CurveKind.Loss, app.Kind)],
        AppKind.TorchNep or AppKind.Mace or AppKind.DeepMd => app.DataFile is { } f ? [new Source(app.Dir + "/" + f, CurveKind.Loss, app.Kind)] : [],
        AppKind.Vasp when app.Vasp == VaspMode.Relax => [new Source(app.Dir + "/OSZICAR", CurveKind.VaspEnergy), new Source(app.Dir + "/OUTCAR", CurveKind.VaspForce)],
        AppKind.Vasp when app.Vasp == VaspMode.Neb => [new Source(app.Dir, CurveKind.Neb)],
        _ => [],
    };

    /// OUTCAR 里每个离子步的最大力（和 VASP 判断 EDIFFG 收敛时的口径一致）：
    /// - 有 VTST 补丁打印的 `FORCES: max atom, RMS` 就直接用它的 max atom（和 `grep RMS OUTCAR` 看到的一样），输出 `row=1 值`；
    /// - 否则读 TOTAL-FORCE 块（表头、虚线、每个原子 x y z Fx Fy Fz、虚线），按 POSCAR 的 Selective dynamics 去掉固定（F）的方向
    ///   再取最大的 |F|，输出 `row=0 值`。两种都有时曲线只用 VTST 的。
    /// n= 报告最后一个读完的块（或 VTST 行）所在的行，正在写的半个块下次重读，VTST 行也不会重复计数。 文本和 Swift 版逐字一致。
    public const string ForceAwk = """
        awk -v off="$off" -v mask="$mask" '
        /FORCES: max atom, RMS/ { printf "row=1 %.6g\n", $5; last = NR; next }
        /TOTAL-FORCE/ { inb = 1; d = 0; m = 0; a = 0; next }
        inb && /^ *-+ *$/ { d++; if (d == 2) { printf "row=0 %.6g\n", sqrt(m); inb = 0; last = NR }; next }
        inb && d == 1 && NF >= 6 {
          a++; fx = $4; fy = $5; fz = $6
          if (length(mask) >= 3 * a) { if (substr(mask, 3 * a - 2, 1) == "F") fx = 0; if (substr(mask, 3 * a - 1, 1) == "F") fy = 0; if (substr(mask, 3 * a, 1) == "F") fz = 0 }
          f = fx * fx + fy * fy + fz * fz; if (f > m) m = f
        }
        END { print "n=" off + last }'
        """;

    /// 远端脚本：输出 n=总行数，必要时 reset=1，然后是 row= 开头的数据行。
    /// `head -n $((n-off))` 只取 wc 数到的完整行，正在写的半行不读，偏移量才对得上。
    public static string Script(Source src, int offset, string head)
    {
        if (src.Kind == CurveKind.VaspForce) return ForceScript(src.Path, offset, head);
        if (src.Kind == CurveKind.Neb) return NebScript(src.Path);
        var extra = "";
        string filter;
        if (src.IsLoss && src.App == AppKind.Nep)
        {
            // GPUMD 新版在每次（重新）开始训练时写一段 # 表头，其中 # output_interval N 是之后每行间隔的代数：输出成 "-1 N"。
            filter = "awk '/^[[:space:]]*#[[:space:]]*output_interval[[:space:]]/ { print -1, $3; next } NF >= 2 && $1 ~ /^[0-9]+$/ { print }' | cut -c1-300";
            extra = """awk '$1=="output_interval" && $2~/^[0-9]+$/ {print "interval="$2; exit}' "$(dirname "$f")/nep.in" 2>/dev/null""" + "\n";
        }
        else if (src.IsLoss && src.App == AppKind.Mace)
        {
            // 每行：epoch 和各个误差的数字；误差的名字变了才输出一次 keys=。多头训练同一个 epoch 有好几行，只取第一行。
            filter = """
                awk '/INFO: Epoch [0-9]+:/ { s = $0; sub(/.*INFO: Epoch /, "", s); e = s + 0; if (n && e == p) next; p = e; n = 1
                  sub(/^[0-9]+: */, "", s); gsub(/= +/, "=", s); k = ""; o = e; m = split(s, a, ",")
                  for (i = 1; i <= m; i++) if (a[i] ~ /=/) { split(a[i], b, "="); sub(/^ +/, "", b[1]); split(b[2], c, " "); k = k (k == "" ? "" : ",") b[1]; o = o " " c[1] }
                  if (k != K) { print "keys=" k; K = k }; print o }'
                """;
        }
        else filter = src.IsLoss
            ? "awk 'NF >= 2 && $1 ~ /^[0-9]+$/ { print }' | cut -c1-300"
            : """awk '$1 ~ /^[0-9]+$/ && $2 ~ /^F=/ { for (i = 3; i <= NF; i++) if ($i ~ /^E0=/) { v = substr($i, 4); if (v == "") v = $(i + 1); print $1, v; break } }'""";
        return $"""
            f={RemoteScript.ShellQuote(src.Path)}
            if [ ! -f "$f" ]; then echo '{Marker}NOFILE'; exit 0; fi
            n=$(wc -l < "$f" | tr -d ' '); h=$(head -n 1 "$f" | cut -c1-200)
            off={Math.Max(0, offset)}
            if [ "$h" != {RemoteScript.ShellQuote(head)} ] || [ "$n" -lt "$off" ]; then off=0; echo reset=1; fi
            if [ $((n - off)) -gt {MaxRows} ]; then off=$((n - {MaxRows})); echo reset=1; fi
            echo "n=$n"; echo "head=$h"
            {extra}tail -n +$((off + 1)) "$f" | head -n $((n - off)) | {filter} | sed '/^keys=/!s/^/row=/'

            """;
    }

    /// OUTCAR 可能有几十万行：不限制行数（awk 一遍就过去了），增量读取保证之后每次只读新增部分。
    public static string ForceScript(string path, int offset, string head) => $$"""
        f={{RemoteScript.ShellQuote(path)}}
        if [ ! -f "$f" ]; then echo '{{Marker}}NOFILE'; exit 0; fi
        n=$(wc -l < "$f" | tr -d ' '); h=$(head -n 1 "$f" | cut -c1-200)
        off={{Math.Max(0, offset)}}
        if [ "$h" != {{RemoteScript.ShellQuote(head)}} ] || [ "$n" -lt "$off" ]; then off=0; echo reset=1; fi
        echo "head=$h"
        grep -m 1 '^ *EDIFFG *=' "$f" | awk '{ print "ediffg=" $3 }'
        sed -n 's/^[[:space:]]*EDIFFG[[:space:]]*=[[:space:]]*\([-+.0-9eEdD]*\).*/ediffg=\1/p' "$(dirname "$f")/INCAR" 2>/dev/null | tail -n 1
        mask=$(awk 'NR == 6 && $1 ~ /^[0-9]+$/ { for (i = 1; i <= NF; i++) n += $i; L = 7 }
        NR == 7 && !L { for (i = 1; i <= NF; i++) n += $i; L = 8 }
        L && NR == L { if ($1 ~ /^[Ss]/) sel = 1; else exit }
        sel && NR > L + 1 && NR <= L + 1 + n { m = m (NF >= 6 ? substr($4, 1, 1) substr($5, 1, 1) substr($6, 1, 1) : "TTT") }
        END { if (sel) print m }' "$(dirname "$f")/POSCAR" 2>/dev/null)
        tail -n +$((off + 1)) "$f" | head -n $((n - off)) | {{ForceAwk}}

        """;

    /// NEB：每次整个重读（OSZICAR 很小，OUTCAR 只读末尾），文本和 Swift 版逐字一致。输出 ediffg= / images=，以及
    /// `row=像点 离子步 E0`（中间像点的 OSZICAR）、`row=像点 0 能量`（OUTCAR 最后的 energy(sigma→0)）、`row=像点 -1 力`（VTST）。
    public static string NebScript(string dir) => $$"""
        d={{RemoteScript.ShellQuote(dir)}}
        if [ ! -d "$d/01" ]; then echo '{{Marker}}NOFILE'; exit 0; fi
        echo n=0; echo reset=1; echo head=
        sed -n 's/^[[:space:]]*EDIFFG[[:space:]]*=[[:space:]]*\([-+.0-9eEdD]*\).*/ediffg=\1/p' "$d/INCAR" 2>/dev/null | tail -n 1
        awk '{ sub(/[!#].*/, ""); s = toupper($0) } s ~ /^[[:space:]]*IMAGES[[:space:]]*=/ { sub(/^[^=]*=[[:space:]]*/, "", s); split(s, v, " "); print "images=" v[1]; exit }' "$d/INCAR" 2>/dev/null
        for s in "$d"/[0-9][0-9]; do
          i=${s##*/}; i=$((10#$i))
          [ -f "$s/OSZICAR" ] && awk -v i="$i" '$1 ~ /^[0-9]+$/ && $2 ~ /^F=/ { for (k = 3; k <= NF; k++) if ($k ~ /^E0=/) { v = substr($k, 4); if (v == "") v = $(k + 1); print "row=" i, $1, v; break } }' "$s/OSZICAR"
          [ -f "$s/OUTCAR" ] && tail -n 25000 "$s/OUTCAR" | awk -v i="$i" '/energy  without entropy/ { e = $NF } /FORCES: max atom, RMS/ { f = $5 } END { if (e != "") print "row=" i, 0, e; if (f != "") print "row=" i, -1, f }'
        done
        true

""";

    /// 把一次输出合并进缓存。读不到文件时返回 null。
    public static CurveCache? Merge(string stdout, CurveCache cache)
    {
        var lines = stdout.Split('\n');
        if (lines.Contains(Marker + "NOFILE")) return null;
        var nLine = lines.FirstOrDefault(l => l.StartsWith("n=", StringComparison.Ordinal));
        if (nLine == null || Text.Int(nLine[2..]) is not { } n) return null;
        var c = new CurveCache { LineCount = cache.LineCount, Head = cache.Head, Rows = [.. cache.Rows], Extra = new(cache.Extra) };
        if (lines.Contains("reset=1")) c.Rows = [];
        c.LineCount = n;
        c.Head = lines.FirstOrDefault(l => l.StartsWith("head=", StringComparison.Ordinal)) is { } h ? h[5..] : "";
        if (lines.FirstOrDefault(l => l.StartsWith("ediffg=", StringComparison.Ordinal)) is { } e) c.Extra["ediffg"] = e[7..];
        if (lines.FirstOrDefault(l => l.StartsWith("images=", StringComparison.Ordinal)) is { } im) c.Extra["images"] = im[7..];
        if (lines.FirstOrDefault(l => l.StartsWith("interval=", StringComparison.Ordinal)) is { } iv) c.Extra["interval"] = iv[9..];
        if (lines.LastOrDefault(l => l.StartsWith("keys=", StringComparison.Ordinal)) is { } ks) c.Extra["keys"] = ks[5..];
        c.Extra["head"] = c.Head;
        foreach (var l in lines.Where(l => l.StartsWith("row=", StringComparison.Ordinal)))
        {
            var nums = Text.Words(l[4..]).Select(Text.Dbl).Where(v => v != null).Select(v => v!.Value).ToArray();
            if (nums.Length >= 2) c.Rows.Add(nums);
        }
        return c;
    }

    /// 一个来源可能出好几张图（NEB 出两张）。
    public static List<AppCurve> Curves(Source src, List<double[]> rows, Dictionary<string, string>? extra = null) =>
        src.Kind == CurveKind.Neb ? NebCurves(rows, extra ?? []) : Curve(src, rows, extra) is { } c ? [c] : [];

    /// NEB 两张图（和 Swift 版一致）：能垒图（相对初态的能量曲线 + 每个中间像点的力柱 + EDIFFG 虚线）、能垒随离子步的变化（右上角写正向 / 逆向能垒）。
    public static List<AppCurve> NebCurves(List<double[]> rows, Dictionary<string, string> extra)
    {
        var steps = new Dictionary<int, Dictionary<int, double>>();
        var last = new Dictionary<int, double>();
        var force = new Dictionary<int, double>();
        foreach (var r in rows.Where(r => r.Length >= 3))
        {
            int i = (int)r[0], st = (int)r[1];
            if (st > 0) { if (!steps.TryGetValue(i, out var d)) steps[i] = d = []; d[st] = r[2]; }
            else if (st == 0) last[i] = r[2];
            else force[i] = r[2];
        }
        var all = steps.Keys.Union(last.Keys).ToList();
        if (all.Count == 0) return [];
        int n = Text.Int(extra.GetValueOrDefault("images")) ?? Math.Max(all.Max() - 1, 1);
        var middle = Enumerable.Range(1, Math.Max(n, 1)).Where(steps.ContainsKey).ToList();
        if (middle.Count == 0) return [];
        int s = middle.Min(i => steps[i].Keys.Max());
        if (s < 1) return [];
        double? e0 = last.TryGetValue(0, out var v0) ? v0 : null;
        // 没有初态 OUTCAR：相对第 1 个像点
        double? Ref(int st) => e0 ?? (steps.TryGetValue(1, out var d1) && d1.TryGetValue(st, out var x) ? x : null);
        if (Ref(s) is not { } r0) return [];
        var profile = new List<(double, double)>();
        if (e0 is { } a) profile.Add((0, a - r0));
        foreach (var i in middle) if (steps[i].TryGetValue(s, out var e)) profile.Add((i, e - r0));
        if (last.TryGetValue(n + 1, out var fe)) profile.Add((n + 1, fe - r0));
        if (profile.Count < 2) return [];
        double top = profile.Where(p => p.Item1 > 0).Select(p => p.Item2).DefaultIfEmpty(0).Max();
        var note = e0 != null ? L($"能垒 {Text.Fmt(top, "0.000")} eV", $"barrier {Text.Fmt(top, "0.000")} eV")
                              : L("相对第 1 个像点（00 没有 OUTCAR）", "relative to image 1 (no OUTCAR in 00)");
        if (e0 != null && last.ContainsKey(n + 1)) note += L($" · 逆向 {Text.Fmt(top - (fe - r0), "0.000")}", $" · reverse {Text.Fmt(top - (fe - r0), "0.000")}");
        var bars = middle.Where(force.ContainsKey).Select(i => ((double)i, force[i])).ToList();
        double? ediffg = extra.GetValueOrDefault("ediffg") is { } ed ? Text.Dbl(ed.Replace('d', 'e').Replace('D', 'e')) : null;
        var outList = new List<AppCurve>
        {
            new()
            {
                Title = L("能垒图（eV）", "Energy profile (eV)"),
                SeriesList = [new AppCurve.Series(L("能量", "energy"), profile, Markers: true)],
                LogY = false, XLabel = L("像点", "image"),
                Threshold = bars.Count > 0 && ediffg is < 0 ? -ediffg : null,
                Bars = bars, BarsLabel = L("力", "force"),
            },
        };
        var history = new List<(double, double)>();
        for (int st = 1; st <= s; st++)
        {
            var es = middle.Select(i => steps[i].TryGetValue(st, out var e) ? e : (double?)null).ToList();
            if (es.Any(e => e == null) || Ref(st) is not { } rr) continue;
            history.Add((st, es.Max()!.Value - rr));
        }
        // 能垒的数字写在这张图的右上角（能垒图的最高点通常在中间偏上，放不下）
        if (history.Count >= 2)
            outList.Add(new AppCurve
            {
                Title = L("能垒（eV）", "Barrier (eV)"), SeriesList = [new AppCurve.Series(L("能垒", "barrier"), Downsample(history))],
                LogY = false, XLabel = L("离子步", "ionic step"), Note = note,
            });
        return outList;
    }

    public static AppCurve? Curve(Source src, List<double[]> rows, Dictionary<string, string>? extra = null)
    {
        if (src.Kind == CurveKind.Neb) return NebCurves(rows, extra ?? []).FirstOrDefault();
        if (rows.Count < 2) return null;
        if (src.Kind == CurveKind.VaspForce)
        {
            // 有 VTST 的 max atom（第一列是 1）就只用它，否则用自己算的（第一列是 0）。按顺序就是离子步 1、2、3…
            var vtst = rows.Where(r => r[0] == 1).ToList();
            var used = vtst.Count > 0 ? vtst : rows.Where(r => r[0] == 0).ToList();
            if (used.Count < 2) return null;
            var pts = used.Select((r, i) => ((double)(i + 1), Math.Max(r[1], 1e-6))).ToList();
            // 共轭梯度（IBRION=2）会走试探步，最大力会上下跳：再画一条"目前最低"的淡线。
            double low = double.PositiveInfinity;
            var lows = pts.Select(p => { low = Math.Min(low, p.Item2); return (p.Item1, low); }).ToList();
            // EDIFFG（OUTCAR 里 VASP 实际用的值，包括默认值）：< 0 按力收敛，|EDIFFG| 是目标（eV/Å）；≥ 0 按能量收敛。
            double? ediffg = extra?.GetValueOrDefault("ediffg") is { } ed ? Text.Dbl(ed.Replace('d', 'e').Replace('D', 'e')) : null;
            return new AppCurve
            {
                Title = L("最大力（eV/Å）", "Max force (eV/Å)"),
                SeriesList = [new AppCurve.Series("|F|max", Downsample(pts)), new AppCurve.Series(L("最低", "lowest"), Downsample(lows), true)],
                LogY = true,
                XLabel = L("离子步", "ionic step"),
                Threshold = ediffg is < 0 ? -ediffg : null,
                Note = ediffg is >= 0 ? L($"按能量收敛 ΔE < {AppProgress.Sci(ediffg.Value)} eV", $"energy criterion ΔE < {AppProgress.Sci(ediffg.Value)} eV") : null,
            };
        }
        if (src.IsLoss && src.App == AppKind.DeepMd) return DeepmdCurve(rows, extra?.GetValueOrDefault("head") ?? "");
        if (src.IsLoss)
        {
            extra ??= [];
            var data = rows;
            IReadOnlyList<string> names;
            switch (src.App)
            {
                case AppKind.Nep:
                {
                    // loss.out 是追加写入的：重新开始 / 续跑时代数从头数起，所以横轴不用第一列，而是按行累加
                    // （每行间隔 output_interval 代，默认 100；新版 GPUMD 每段开头的 # output_interval 会改变之后的间隔）。
                    double step = Text.Dbl(extra.GetValueOrDefault("interval")) ?? 100, x = 0;
                    data = [];
                    foreach (var r in rows)
                    {
                        if (r[0] < 0) { step = r[1]; continue; }
                        x += step;
                        data.Add([x, .. r[1..]]);
                    }
                    names = AppDetectors.NepLossLabels(data.Count > 0 ? data[^1].Length : 0);
                    break;
                }
                case AppKind.TorchNep:
                    names = AppDetectors.TorchnepLabels(extra.GetValueOrDefault("head") ?? "");
                    data = Monotonic(rows);
                    break;
                case AppKind.Mace:
                    names = ["epoch", .. (extra.GetValueOrDefault("keys") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(k => k == "loss" ? "Loss" : k.Replace("_per_atom", "").Replace("_", " "))];
                    data = Monotonic(rows);
                    break;
                default:
                    names = AppDetectors.NepLossLabels(0);
                    break;
            }
            // 画哪几列、图例里叫什么
            (string Key, string Name)[] wanted = [("Total", "Total"), ("Loss", "Loss"), ("Energy", L("能量", "Energy")), ("Force", L("力", "Force")),
                                                  ("Virial", L("维里", "Virial")), ("P-train", "Train"), ("P-test", "Test")];
            var cols = new List<(string, int)>();
            for (int i = 1; i < names.Count; i++)
            {
                var b = names[i].Split(" (")[0];
                if (wanted.FirstOrDefault(w => w.Key == b) is { Key: not null } w) cols.Add((w.Name, i));
                else if (src.App == AppKind.TorchNep && b is "E" or "F" or "V") cols.Add((b, i));
                else if (src.App == AppKind.Mace) cols.Add((b, i));
            }
            var series = new List<AppCurve.Series>();
            foreach (var (name, i) in cols)
            {
                var pts = data.Where(r => i < r.Length && r[i] > 0).Select(r => (r[0], r[i])).ToList();
                if (pts.Count >= 2) series.Add(new AppCurve.Series(name, Downsample(pts)));
            }
            if (series.Count == 0) return null;
            return new AppCurve { Title = "Loss", SeriesList = series, LogY = true, XLabel = src.App == AppKind.Nep ? L("代", "gen") : "epoch" };
        }
        var e0 = rows.Select(r => (r[0], r[1])).ToList();
        return new AppCurve
        {
            Title = L("能量 E0（eV）", "Energy E0 (eV)"),
            SeriesList = [new AppCurve.Series("E0", Downsample(e0))],
            LogY = false,
            XLabel = L("离子步", "ionic step"),
        };
    }

    /// DeePMD lcurve.out：能量、力、维里各画一条，有验证集用验证集（多任务取第一个任务），没有就用训练集；
    /// 三项都没有时画总误差。续跑（--restart）时 lcurve.out 追加写入，步数接着数；从较早的检查点续跑时去掉重跑的那段。
    public static AppCurve? DeepmdCurve(List<double[]> rows, string header)
    {
        var names = AppDetectors.DeepmdLabels(header);
        var data = Monotonic(rows.Where(r => r.Length == names.Count).ToList());
        List<(double, double)> Points(int i) => data.Where(r => double.IsFinite(r[i]) && r[i] > 0).Select(r => (r[0], r[i])).ToList();
        int? Column(string? q, string split)
        {
            for (int i = 1; i < names.Count; i++)
            {
                var w = names[i].Split(' ');
                var hasQ = q != null ? w.Contains(q) : !w.Any(x => x is "E" or "F" or "V");
                if (w.Contains(split) && hasQ && Points(i).Count >= 2) return i;
            }
            return null;
        }
        var series = new List<AppCurve.Series>();
        foreach (var (q, name) in new[] { ("E", L("能量", "Energy")), ("F", L("力", "Force")), ("V", L("维里", "Virial")) })
        {
            if (Column(q, "val") is { } i) series.Add(new AppCurve.Series(name, Downsample(Points(i))));
            else if (Column(q, "trn") is { } j) series.Add(new AppCurve.Series(name + L("（训练集）", " (train)"), Downsample(Points(j))));
        }
        if (series.Count == 0 && (Column(null, "val") ?? Column(null, "trn")) is { } k)
            series.Add(new AppCurve.Series(names[k], Downsample(Points(k))));
        if (series.Count == 0) return null;
        return new AppCurve { Title = "Loss", SeriesList = series, LogY = true, XLabel = L("步", "step") };
    }

    /// 横轴倒退（从较早的检查点续跑，日志 / loss.out 是追加写入的）时，丢掉之前跑过、又被重跑的那一段。
    public static List<double[]> Monotonic(List<double[]> rows)
    {
        var outRows = new List<double[]>();
        foreach (var r in rows)
        {
            while (outRows.Count > 0 && outRows[^1][0] >= r[0]) outRows.RemoveAt(outRows.Count - 1);
            outRows.Add(r);
        }
        return outRows;
    }

    /// 点太多时等间隔抽样（保留首尾），画图用不到上万个点。
    public static List<(double, double)> Downsample(List<(double, double)> pts, int limit = 400)
    {
        if (pts.Count <= limit) return pts;
        double step = (double)(pts.Count - 1) / (limit - 1);
        return Enumerable.Range(0, limit).Select(k => pts[Math.Min((int)Math.Round(k * step, MidpointRounding.AwayFromZero), pts.Count - 1)]).ToList();
    }
}
