using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ServerIsland.Desktop.Ui;

/// 代替 SF Symbols 的一套线条图标（24×24 坐标，圆头描边）。少数图标是实心的。
public static class Icons
{
    sealed record Part(Geometry Geometry, bool Filled);

    static readonly Dictionary<string, Part[]> Map = [];

    static Part S(string d) => new(Geometry.Parse(d), false);
    static Part F(string d) => new(Geometry.Parse(d), true);
    static Part Circle(double cx, double cy, double r, bool filled = false) => new(new EllipseGeometry(new Rect(cx - r, cy - r, 2 * r, 2 * r)), filled);

    static Icons()
    {
        Map["bolt"] = [F("M13.5 2 L4.5 13.5 H11 L10 22 L19.5 10 H13 Z")];
        Map["hourglass"] = [S("M6 3 H18 M6 21 H18 M7.5 3 V7 L12 12 L16.5 7 V3 M7.5 21 V17 L12 12 L16.5 17 V21")];
        Map["pause"] = [F("M6 4 H10 V20 H6 Z M14 4 H18 V20 H14 Z")];
        Map["play"] = [F("M7 3.5 L20.5 12 L7 20.5 Z")];
        Map["bell"] = [S("M18 8 A6 6 0 0 0 6 8 C6 15 3 17 3 17 H21 C21 17 18 15 18 8 M13.73 21 A2 2 0 0 1 10.27 21")];
        Map["bellSlash"] = [S("M18 8 A6 6 0 0 0 6 8 C6 15 3 17 3 17 H21 C21 17 18 15 18 8 M13.73 21 A2 2 0 0 1 10.27 21 M2.5 2.5 L21.5 21.5")];
        Map["eye"] = [S("M2 12 C2 12 5.5 5 12 5 C18.5 5 22 12 22 12 C22 12 18.5 19 12 19 C5.5 19 2 12 2 12 Z"), Circle(12, 12, 3)];
        Map["eyeFill"] = [S("M2 12 C2 12 5.5 5 12 5 C18.5 5 22 12 22 12 C22 12 18.5 19 12 19 C5.5 19 2 12 2 12 Z"), Circle(12, 12, 3.6, true)];
        Map["history"] = [S("M3.5 12 A8.5 8.5 0 1 0 6 6 L3.5 8.5 M3.5 3.5 V8.5 H8.5 M12 7.5 V12 L15 14")];
        Map["grid"] = Grid();
        Map["timer"] = [S("M10 2 H14 M12 14 L15 11 M20 14 A8 8 0 1 1 4 14 A8 8 0 1 1 20 14 Z")];
        Map["hand"] = [S("M9 11.5 V5 A1.5 1.5 0 0 1 12 5 V11 M12 9.5 A1.5 1.5 0 0 1 15 9.5 V11 M15 10.5 A1.5 1.5 0 0 1 18 10.5 V15 A6 6 0 0 1 12 21 H11.5 A5 5 0 0 1 7.3 18.7 L4.6 14.8 A1.5 1.5 0 0 1 7 13 L9 15.5 V11.5")];
        Map["pin"] = [S("M12 17 V22 M8.5 3 H15.5 M10 3 V9.5 L6.5 14 H17.5 L14 9.5 V3")];
        Map["pinFill"] = [F("M10 3 H14 V9.5 L17.5 14 H6.5 L10 9.5 Z"), S("M12 17 V22 M8.5 3 H15.5")];
        Map["list"] = [S("M4.5 6.5 H5 M4.5 12 H5 M4.5 17.5 H5 M9 6.5 H19.5 M9 12 H19.5 M9 17.5 H19.5")];
        Map["refresh"] = [S("M20.5 12 A8.5 8.5 0 1 1 17.6 5.6 L20.5 8.5 M20.5 3.5 V8.5 H15.5")];
        Map["gear"] = Gear();
        Map["chevronLeft"] = [S("M15 5.5 L8.5 12 L15 18.5")];
        Map["chevronRight"] = [S("M9 5.5 L15.5 12 L9 18.5")];
        Map["chevronDown"] = [S("M5.5 9 L12 15.5 L18.5 9")];
        Map["chevronUp"] = [S("M5.5 15 L12 8.5 L18.5 15")];
        Map["check"] = [S("M20 6.5 L9.5 17 L4 11.5")];
        Map["xmark"] = [S("M18 6 L6 18 M6 6 L18 18")];
        Map["minus"] = [S("M5 12 H19")];
        Map["plus"] = [S("M5 12 H19 M12 5 V19")];
        Map["terminal"] = [S("M4 17 L10 11 L4 5 M12 19 H20")];
        Map["copy"] = [S("M9 9 H20 V20 H9 Z M5 15 H4 V4 H15 V5")];
        Map["search"] = [S("M17.5 10.5 A7 7 0 1 1 3.5 10.5 A7 7 0 1 1 17.5 10.5 Z M20.5 20.5 L15.5 15.5")];
        Map["key"] = [S("M12.5 15.5 A5 5 0 1 1 2.5 15.5 A5 5 0 1 1 12.5 15.5 Z M11 12 L20 3 M17 6 L20 9 M15 8 L17 10")];
        Map["wifiAlert"] = [S("M2 9 A15 15 0 0 1 15.5 5.2 M5.5 12.7 A10 10 0 0 1 13 10.2 M9 16.4 A5 5 0 0 1 11.5 15.4 M12 20 H12.01 M19.5 8 V14 M19.5 18.5 V18.51")];
        Map["moon"] = [F("M12 3 A6 6 0 0 0 21 12 A9 9 0 1 1 12 3 Z")];
        Map["zzz"] = [F("M12 3 A6 6 0 0 0 21 12 A9 9 0 1 1 12 3 Z")];
        Map["atom"] = Atom();
        Map["stack"] = [S("M12 2.5 L2.5 7.5 L12 12.5 L21.5 7.5 Z M2.5 12 L12 17 L21.5 12 M2.5 16.5 L12 21.5 L21.5 16.5")];
        Map["chart"] = [S("M6 20 V13 M12 20 V6 M18 20 V10")];
        // 副屏设置页用到的几个。
        Map["wifi"] = [S("M2 9 A15 15 0 0 1 22 9 M5.5 12.7 A10 10 0 0 1 18.5 12.7 M9 16.4 A5 5 0 0 1 15 16.4 M12 20 H12.01")];
        Map["calendar"] = [S("M4 6 H20 V20 H4 Z M4 10 H20 M8 3.5 V7.5 M16 3.5 V7.5")];
        Map["cloud"] = [S("M7 18 H17.5 A4 4 0 0 0 17 10 A5.5 5.5 0 0 0 6.5 11 A3.5 3.5 0 0 0 7 18 Z")];
        Map["puzzle"] = [S("M5 7 H9 A2 2 0 1 1 13 7 H17 V11 A2 2 0 1 1 17 15 V19 H13 A2 2 0 1 0 9 19 H5 Z")];
        Map["sliders"] = [S("M4 7 H14 M18 7 H20 M4 17 H6 M10 17 H20 M16 5 V9 M8 15 V19")];
        Map["lock"] = [S("M5 11 H19 V21 H5 Z M8 11 V7.5 A4 4 0 0 1 16 7.5 V11")];
        Map["warning"] = [S("M12 3 L22 20 H2 Z M12 9.5 V13.5 M12 17 V17.01")];
        Map["warningFill"] = [F("M12 2.5 L22.5 20.5 H1.5 Z")];
        Map["info"] = [S("M12 10.5 V18 M12 6 V6.01")];
        Map["exclamation"] = [S("M12 4 V14 M12 19.5 V19.51")];
        Map["exclamationCircle"] = [Circle(12, 12, 10, true)];
        Map["cpu"] = [S("M6 6 H18 V18 H6 Z M9.5 9.5 H14.5 V14.5 H9.5 Z M9 2.5 V6 M15 2.5 V6 M9 18 V21.5 M15 18 V21.5 M2.5 9 H6 M2.5 15 H6 M18 9 H21.5 M18 15 H21.5")];
        Map["drive"] = [S("M22 12 H2 M5.5 5 L2 12 V18 A2 2 0 0 0 4 20 H20 A2 2 0 0 0 22 18 V12 L18.5 5 A2 2 0 0 0 16.8 4 H7.2 A2 2 0 0 0 5.5 5 Z M6 16 H6.01 M10 16 H10.01")];
        Map["star"] = [S("M12 2.5 L14.9 8.4 L21.4 9.3 L16.7 13.9 L17.8 20.4 L12 17.3 L6.2 20.4 L7.3 13.9 L2.6 9.3 L9.1 8.4 Z")];
        Map["starFill"] = [F("M12 2.5 L14.9 8.4 L21.4 9.3 L16.7 13.9 L17.8 20.4 L12 17.3 L6.2 20.4 L7.3 13.9 L2.6 9.3 L9.1 8.4 Z")];
        Map["server"] = [S("M3 4 H21 V10 H3 Z M3 14 H21 V20 H3 Z M7 7 H7.01 M7 17 H7.01")];
        Map["trash"] = [S("M3 6 H21 M8.5 6 V3.5 H15.5 V6 M5.5 6 L6.8 21 H17.2 L18.5 6")];
        Map["question"] = [Circle(12, 12, 9.5), S("M9.2 9.2 A2.9 2.9 0 0 1 14.8 10 C14.8 12 12 12.4 12 14.2 M12 17.4 V17.41")];
        Map["arrowDown"] = [S("M12 3 V15 M7 10 L12 15 L17 10 M5 20.5 H19")];
        Map["wand"] = [S("M15 4 V2 M15 16 V14 M8 9 H10 M20 9 H22 M17.8 11.8 L19 13 M17.8 6.2 L19 5 M3 21 L12.5 11.5 M12.2 6.2 L11 5")];
        Map["globe"] = [Circle(12, 12, 9.5), S("M2.5 12 H21.5 M12 2.5 C15 5.5 15 18.5 12 21.5 C9 18.5 9 5.5 12 2.5")];
        Map["box"] = [S("M21 7.5 L12 3 L3 7.5 V16.5 L12 21 L21 16.5 Z M3 7.5 L12 12 L21 7.5 M12 12 V21")];
        Map["speaker"] = [S("M11 5 L6 9 H2.5 V15 H6 L11 19 Z M15.5 8.5 A5 5 0 0 1 15.5 15.5 M18.5 5.5 A9.5 9.5 0 0 1 18.5 18.5")];
        Map["speakerLow"] = [S("M11 5 L6 9 H2.5 V15 H6 L11 19 Z")];
        Map["power"] = [S("M12 2.5 V12 M18.4 6.6 A9 9 0 1 1 5.6 6.6")];
        Map["folder"] = [S("M20 20 A2 2 0 0 0 22 18 V8.5 A2 2 0 0 0 20 6.5 H12 L10 4 H4 A2 2 0 0 0 2 6 V18 A2 2 0 0 0 4 20 Z")];
        Map["pulse"] = [S("M22 12 H18 L15 20.5 L9 3.5 L6 12 H2")];
        Map["layout"] = [S("M3 3.5 H10 V10.5 H3 Z M14 3.5 H21 V10.5 H14 Z M3 14 H21 V20.5 H3 Z")];
        Map["external"] = [S("M15 3 H21 V9 M10 14 L21 3 M18 13.5 V19 A2 2 0 0 1 16 21 H5 A2 2 0 0 1 3 19 V8 A2 2 0 0 1 5 6 H10.5")];
        Map["pet"] = [S("M5 13 V9 A7 7 0 0 1 19 9 V13 M5 13 L3.5 20 M9 13 L8 20 M15 13 L16 20 M19 13 L20.5 20"), Circle(9.5, 9.5, 1.2, true), Circle(14.5, 9.5, 1.2, true)];
        // 计费：货币（圆圈里画 ¥ / $ / € / £）和设置页的卡片
        Map["yen"] = [Circle(12, 12, 9.5), S("M8.5 6.5 L12 11.5 L15.5 6.5 M12 11.5 V17.5 M8.5 12.5 H15.5 M8.5 15.2 H15.5")];
        Map["dollar"] = [Circle(12, 12, 9.5), S("M12 5.5 V18.5 M15 8.8 C14.2 7.6 9 7.2 9 9.8 C9 12.4 15 11.6 15 14.2 C15 16.8 9.8 16.4 8.8 15.2")];
        Map["euro"] = [Circle(12, 12, 9.5), S("M15.5 8 A4.6 4.6 0 1 0 15.5 16 M7 10.8 H13 M7 13.2 H13")];
        Map["pound"] = [Circle(12, 12, 9.5), S("M15 8 A2.8 2.8 0 0 0 10 9.5 V16.5 H15.5 M8.2 12.5 H13 M8.5 16.5 H10")];
        Map["card"] = [S("M3 6 H21 V18 H3 Z M3 10 H21 M6.5 14.5 H10")];
        Map["display"] = [S("M3 4 H21 V16 H3 Z M8 20.5 H16 M12 16 V20.5")];
        // 手机推送：手机 + 两侧的信号
        Map["phone"] = [S("M9 3.5 H15 A1.5 1.5 0 0 1 16.5 5 V19 A1.5 1.5 0 0 1 15 20.5 H9 A1.5 1.5 0 0 1 7.5 19 V5 A1.5 1.5 0 0 1 9 3.5 Z M11 17.5 H13 M4.5 9 C3.5 10.8 3.5 13.2 4.5 15 M19.5 9 C20.5 10.8 20.5 13.2 19.5 15")];
        Map["circle"] = [Circle(12, 12, 6, true)];
    }

    static Part[] Grid()
    {
        var parts = new List<Part>();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                parts.Add(new Part(new RectangleGeometry(new Rect(3.5 + c * 6, 3.5 + r * 6, 5, 5)), true));
        return [.. parts];
    }

    static Part[] Gear()
    {
        // 8 个齿的齿轮（实心）+ 中间的孔（用偶奇填充挖空）
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);
            const int teeth = 8;
            const double rOuter = 10.8, rInner = 8.0, step = 2 * Math.PI / teeth;
            static Point P(double r, double a) => new(12 + r * Math.Cos(a), 12 + r * Math.Sin(a));
            for (int i = 0; i < teeth; i++)
            {
                double c = i * step;
                if (i == 0) ctx.BeginFigure(P(rInner, c - 0.30 * step), true);
                else ctx.LineTo(P(rInner, c - 0.30 * step));
                ctx.LineTo(P(rOuter, c - 0.17 * step));
                ctx.LineTo(P(rOuter, c + 0.17 * step));
                ctx.LineTo(P(rInner, c + 0.30 * step));
                ctx.ArcTo(P(rInner, c + 0.70 * step), new Size(rInner, rInner), 0, false, SweepDirection.Clockwise);
            }
            ctx.EndFigure(true);
            ctx.BeginFigure(new Point(15.2, 12), true);
            ctx.ArcTo(new Point(8.8, 12), new Size(3.2, 3.2), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(15.2, 12), new Size(3.2, 3.2), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        return [new Part(g, true)];
    }

    static Part[] Atom()
    {
        var parts = new List<Part> { Circle(12, 12, 1.8, true) };
        foreach (var angle in new[] { 0.0, 60.0, -60.0 })
        {
            var e = new EllipseGeometry(new Rect(2, 8, 20, 8))
            {
                Transform = new RotateTransform(angle, 0, 0) { CenterX = 12, CenterY = 12 },
            };
            parts.Add(new Part(e, false));
        }
        return [.. parts];
    }

    public static bool Exists(string name) => Map.ContainsKey(name);

    /// 画一个图标：size 是边长（像素），weight 越大线条越粗。
    public static void Draw(DrawingContext ctx, string name, Rect rect, IBrush brush, double weight = 2.3)
    {
        if (!Map.TryGetValue(name, out var parts)) return;
        double scale = Math.Min(rect.Width, rect.Height) / 24.0;
        var pen = new Pen(brush, weight, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(rect.X + (rect.Width - 24 * scale) / 2, rect.Y + (rect.Height - 24 * scale) / 2)))
        {
            foreach (var p in parts)
            {
                if (p.Filled) ctx.DrawGeometry(brush, null, p.Geometry);
                else ctx.DrawGeometry(null, pen, p.Geometry);
            }
        }
    }
}

/// 图标控件。
public sealed class Icon : Control
{
    public string Name2 { get; }
    public IBrush Brush { get; set; }
    public double Weight { get; set; }

    public Icon(string name, double size, IBrush brush, double weight = 2.4)
    {
        Name2 = name;
        Brush = brush;
        Weight = weight;
        Width = size;
        Height = size;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context) => Icons.Draw(context, Name2, new Rect(Bounds.Size), Brush, Weight);
}
