using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VisionSystem.Studio.Localization;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// Base of the Run screen's charts (UI-UX-SPEC.md principle 15): one scale per chart, thin marks,
/// limits drawn as lines with their value, and a tooltip on every mark (pointer over it, or a tap).
/// Series colours are the Vs.Brush.Chart tokens; OK and NG colours only where they mean OK and NG;
/// text always in the text colours. Colours follow the theme.
/// </summary>
public abstract class ChartBase : Control
{
    public static readonly StyledProperty<OverviewData?> DataProperty =
        AvaloniaProperty.Register<ChartBase, OverviewData?>(nameof(Data));

    private string? _tip;

    static ChartBase()
    {
        AffectsRender<ChartBase>(DataProperty);
    }

    public OverviewData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>The tooltip text of the mark under a point, or null.</summary>
    public abstract string? TipAt(Point at);

    protected static Strings S => Strings.Instance;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ShowTip(TipAt(e.GetPosition(this)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ShowTip(TipAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ShowTip(null);
    }

    private void ShowTip(string? tip)
    {
        if (tip == _tip) return;
        _tip = tip;
        ToolTip.SetTip(this, tip);
        ToolTip.SetIsOpen(this, tip is not null);
    }

    protected IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    protected double FontSize =>
        this.TryFindResource("Vs.Font.Small", ActualThemeVariant, out var value) && value is double size ? size : 11;

    protected FormattedText Measure(string text, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, weight: bold ? FontWeight.SemiBold : FontWeight.Normal), FontSize, brush);

    /// <summary>Draws text with its middle at y: align -1 starts at x, 0 is centred on x, 1 ends at x.</summary>
    protected void Text(DrawingContext context, string text, double x, double y, IBrush brush, int align = -1, bool bold = false)
    {
        var ft = Measure(text, brush, bold);
        var left = align switch { 0 => x - ft.Width / 2, 1 => x - ft.Width, _ => x };
        context.DrawText(ft, new Point(left, y - ft.Height / 2));
    }

    /// <summary>The text cut with "…" so it fits the width; never cut mid-way silently.</summary>
    protected string Fit(string text, double width, IBrush brush)
    {
        if (Measure(text, brush).Width <= width) return text;
        for (var n = text.Length - 1; n > 0; n--)
        {
            var cut = text[..n].TrimEnd() + "…";
            if (Measure(cut, brush).Width <= width) return cut;
        }
        return "…";
    }

    protected void Empty(DrawingContext context, string key) =>
        Text(context, S[key], Bounds.Width / 2, Bounds.Height / 2, Brush("Vs.Brush.TextMuted"), 0);

    protected static string Number(double value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A round axis top at or above the count, with a whole step that gives about four gridlines.</summary>
    protected static (double Top, double Step) Axis(double max)
    {
        var step = Math.Max(1, OverviewData.NiceStep(Math.Max(1, max) / 4));
        return (Math.Max(step, step * Math.Ceiling(max / step)), step);
    }
}

/// <summary>Good parts per minute as columns over the last hour; a minute with bad parts gets an NG
/// dot above its column that grows with the count.</summary>
public sealed class MinutesChart : ChartBase
{
    public static readonly StyledProperty<double> LineSpeedProperty =
        AvaloniaProperty.Register<MinutesChart, double>(nameof(LineSpeed));

    private const double L = 34, R = 6, T = 8, B = 16;

    static MinutesChart()
    {
        AffectsRender<MinutesChart>(LineSpeedProperty);
    }

    /// <summary>The parts per minute the line is set to run at; 0 when not known (no line drawn).</summary>
    public double LineSpeed
    {
        get => GetValue(LineSpeedProperty);
        set => SetValue(LineSpeedProperty, value);
    }

    private double Slot => (Bounds.Width - L - R) / OverviewData.MinutesShown;

    /// <summary>The axis top: the most good parts in a minute, or the line speed when that is higher.</summary>
    private (double Top, double Step) Scale(OverviewData data) => Axis(Math.Max(data.Minutes.Max(m => m.Good), LineSpeed));

    private double YAt(double top, double v) => T + (Bounds.Height - T - B) - v / top * (Bounds.Height - T - B);

    public override void Render(DrawingContext context)
    {
        var data = Data;
        if (data is null || !data.HasParts)
        {
            // Parts were made, only not in the last hour.
            Empty(context, data is { Minutes.Count: > 0 } ? "run.chart.no_parts_hour" : "run.chart.no_parts");
            return;
        }
        var w = Bounds.Width;
        var h = Bounds.Height - T - B;
        var (top, step) = Scale(data);
        double Y(double v) => YAt(top, v);
        var grid = new Pen(Brush("Vs.Brush.Chart.Grid"), 1);
        var axis = Brush("Vs.Brush.Chart.Axis");
        for (var v = 0.0; v <= top + 1e-9; v += step)
        {
            context.DrawLine(grid, new Point(L, Y(v)), new Point(w - R, Y(v)));
            Text(context, Number(v), L - 5, Y(v), axis, 1);
        }
        var good = Brush("Vs.Brush.Chart.S1");
        var ng = Brush("Vs.Brush.Status.Ng");
        var ring = new Pen(Brush("Vs.Brush.Surface"), 1.5);
        var slot = Slot;
        var bw = Math.Clamp(slot - 2, 1, 24);
        for (var i = 0; i < data.Minutes.Count; i++)
        {
            var m = data.Minutes[i];
            var x = L + i * slot + (slot - bw) / 2;
            var y = Y(m.Good);
            if (m.Good > 0) context.DrawRectangle(good, null, new Rect(x, y, bw, T + h - y), Math.Min(2, bw / 2), Math.Min(2, bw / 2));
            if (m.Bad > 0)
            {
                var r = 2.5 + Math.Min(m.Bad, 10) * 0.3;
                context.DrawEllipse(ng, ring, new Point(x + bw / 2, Math.Max(T + r, y - r - 2)), r, r);
            }
        }
        if (LineSpeed > 0)
        {
            // the line speed as a limit line with its value (principle 15); in the text colour, not a series colour
            var text = Brush("Vs.Brush.Text");
            var ly = Y(LineSpeed);
            context.DrawLine(new Pen(text, 1.5, new DashStyle([4, 3], 0)), new Point(L, ly), new Point(w - R, ly));
            var label = S.Format("run.chart.line_speed", Number(LineSpeed));
            var below = ly - 8 < T;   // no room above the line at the top of the chart
            Text(context, label, w - R, below ? ly + 8 : ly - 8, text, 1);
        }
        var n = data.Minutes.Count;
        Text(context, "−60", L, Bounds.Height - B / 2, axis);
        Text(context, "−30", L + (n / 2) * slot + slot / 2, Bounds.Height - B / 2, axis, 0);
        Text(context, S["run.chart.now"], w - R, Bounds.Height - B / 2, axis, 1);
    }

    public override string? TipAt(Point at)
    {
        var data = Data;
        if (data is null || !data.HasParts || at.X < L) return null;
        if (LineSpeed > 0 && Math.Abs(at.Y - YAt(Scale(data).Top, LineSpeed)) <= 4)
            return S.Format("run.chart.line_speed_tip", Number(LineSpeed));
        var i = (int)Math.Floor((at.X - L) / Slot);
        if (i < 0 || i >= data.Minutes.Count) return null;
        var m = data.Minutes[i];
        return S.Format("run.chart.minute_tip", m.Start.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture), Number(m.Good), Number(m.Bad));
    }
}

/// <summary>Why parts were NG: one horizontal bar per reason, largest first, its count at the tip.</summary>
public sealed class ReasonsChart : ChartBase
{
    private const double Pad = 4;

    private (double LabelWidth, double Row) Layout(OverviewData data) =>
        (Math.Min(Bounds.Width * 0.48, 190), Math.Min(24, (Bounds.Height - 2 * Pad) / Math.Max(1, data.Reasons.Count)));

    public override void Render(DrawingContext context)
    {
        var data = Data;
        if (data is null || data.Reasons.Count == 0)
        {
            Empty(context, "run.chart.no_bad");
            return;
        }
        var (labelWidth, row) = Layout(data);
        var max = data.Reasons.Max(r => r.Count);
        var text = Brush("Vs.Brush.Text");
        var bar = Brush("Vs.Brush.Chart.S2");
        var room = Bounds.Width - labelWidth - 8 - 40;
        for (var i = 0; i < data.Reasons.Count; i++)
        {
            var r = data.Reasons[i];
            var mid = Pad + i * row + row / 2;
            Text(context, Fit(r.Reason, labelWidth - 6, text), labelWidth - 6, mid, text, 1);
            var bh = Math.Min(14, row - 6);
            var bw = Math.Max(2, r.Count / (double)max * room);
            context.DrawRectangle(bar, null, new Rect(labelWidth, mid - bh / 2, bw, bh), 3, 3);
            Text(context, Number(r.Count), labelWidth + bw + 5, mid, text);
        }
    }

    public override string? TipAt(Point at)
    {
        var data = Data;
        if (data is null || data.Reasons.Count == 0) return null;
        var (_, row) = Layout(data);
        var i = (int)Math.Floor((at.Y - Pad) / row);
        if (i < 0 || i >= data.Reasons.Count) return null;
        var r = data.Reasons[i];
        var share = 100.0 * r.Count / data.Reasons.Sum(x => x.Count);
        return S.Format("run.chart.reason_tip", r.Reason, Number(r.Count), share.ToString("0", CultureInfo.CurrentCulture));
    }
}

/// <summary>How the cycle times of the last parts spread: columns per time band, the 95th
/// percentile marked, and the cycle budget as a red line when it is known.</summary>
public sealed class CycleChart : ChartBase
{
    public static readonly StyledProperty<double> BudgetMsProperty =
        AvaloniaProperty.Register<CycleChart, double>(nameof(BudgetMs));

    private const double L = 6, R = 6, T = 30, B = 16;

    static CycleChart()
    {
        AffectsRender<CycleChart>(BudgetMsProperty);
    }

    /// <summary>The time a part may take at line speed, in ms; 0 when not known (no line drawn).</summary>
    public double BudgetMs
    {
        get => GetValue(BudgetMsProperty);
        set => SetValue(BudgetMsProperty, value);
    }

    private double XMax(OverviewData data) => Math.Max(data.Cycle[^1].ToMs, BudgetMs > 0 ? BudgetMs * 1.05 : 0);

    private double X(OverviewData data, double ms) => L + ms / XMax(data) * (Bounds.Width - L - R);

    public override void Render(DrawingContext context)
    {
        var data = Data;
        if (data is null || data.Cycle.Count == 0)
        {
            Empty(context, "run.chart.no_cycle");
            return;
        }
        var h = Bounds.Height - T - B;
        var max = data.Cycle.Max(b => b.Count);
        var axis = Brush("Vs.Brush.Chart.Axis");
        var text = Brush("Vs.Brush.Text");
        context.DrawLine(new Pen(Brush("Vs.Brush.Chart.Grid"), 1), new Point(L, T + h), new Point(Bounds.Width - R, T + h));
        var fill = Brush("Vs.Brush.Chart.S3");
        foreach (var b in data.Cycle)
        {
            if (b.Count == 0) continue;
            var x0 = X(data, b.FromMs) + 1;
            var x1 = X(data, b.ToMs) - 1;
            var y = T + h - b.Count / (double)max * h;
            context.DrawRectangle(fill, null, new Rect(x0, y, Math.Max(1, x1 - x0), T + h - y), 2, 2);
        }
        var p95 = X(data, data.P95Ms);
        context.DrawLine(new Pen(text, 1.5), new Point(p95, T - 14), new Point(p95, T + h));
        // Right of its line, or left of it when it would run past the edge; one line below the budget's label.
        var p95Text = S.Format("run.chart.p95", Number(data.P95Ms));
        var right = p95 + 4 + Measure(p95Text, text).Width <= Bounds.Width - R;
        Text(context, p95Text, right ? p95 + 4 : p95 - 4, T - 8, text, right ? -1 : 1);
        if (BudgetMs > 0)
        {
            var bx = X(data, BudgetMs);
            context.DrawLine(new Pen(Brush("Vs.Brush.Status.Ng"), 2), new Point(bx, 2), new Point(bx, T + h));
            Text(context, S.Format("run.chart.budget", Number(BudgetMs)), bx - 4, 8, text, 1);
        }
        var xmax = XMax(data);
        Text(context, "0", L, Bounds.Height - B / 2, axis);
        Text(context, Number(xmax / 2), X(data, xmax / 2), Bounds.Height - B / 2, axis, 0);
        Text(context, Number(xmax) + " ms", Bounds.Width - R, Bounds.Height - B / 2, axis, 1);
    }

    public override string? TipAt(Point at)
    {
        var data = Data;
        if (data is null || data.Cycle.Count == 0) return null;
        if (BudgetMs > 0 && Math.Abs(at.X - X(data, BudgetMs)) <= 4)
            return S.Format("run.chart.budget_tip", Number(BudgetMs));
        foreach (var b in data.Cycle)
            if (at.X >= X(data, b.FromMs) && at.X < X(data, b.ToMs))
                return S.Format("run.chart.cycle_tip", Number(b.FromMs), Number(b.ToMs), Number(b.Count));
        return null;
    }
}

/// <summary>Good % per minute over the last hour as a thin line with a light area under it; a
/// minute without parts leaves a gap. The scale starts at 95 % or lower when a minute was lower.</summary>
public sealed class GoodTrend : ChartBase
{
    private const double Pad = 3;

    private static double Low(IReadOnlyList<double?> values) =>
        Math.Floor(Math.Min(95, values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(95).Min()));

    public override void Render(DrawingContext context)
    {
        var data = Data;
        if (data is null || !data.HasParts) return;
        var values = data.GoodPercentByMinute;
        var low = Low(values);
        double X(int i) => Pad + i * (Bounds.Width - 2 * Pad) / (values.Count - 1);
        double Y(double v) => Pad + (100 - v) / (100 - low) * (Bounds.Height - 2 * Pad);
        var line = new Pen(Brush("Vs.Brush.Chart.S1"), 2, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        var area = Brush("Vs.Brush.Chart.S1");
        var geometry = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var g = geometry.Open())
        using (var f = fill.Open())
        {
            var start = -1;
            for (var i = 0; i <= values.Count; i++)
            {
                var v = i < values.Count ? values[i] : null;
                if (v is { } value)
                {
                    if (start < 0)
                    {
                        start = i;
                        g.BeginFigure(new Point(X(i), Y(value)), false);
                        f.BeginFigure(new Point(X(i), Bounds.Height), true);
                    }
                    else g.LineTo(new Point(X(i), Y(value)));
                    f.LineTo(new Point(X(i), Y(value)));
                }
                else if (start >= 0)
                {
                    g.EndFigure(false);
                    f.LineTo(new Point(X(i - 1), Bounds.Height));
                    f.EndFigure(true);
                    start = -1;
                }
            }
        }
        using (context.PushOpacity(0.12)) context.DrawGeometry(area, null, fill);
        context.DrawGeometry(null, line, geometry);
        var last = values.Count - 1;
        while (last >= 0 && values[last] is null) last--;
        if (last >= 0)
            context.DrawEllipse(area, new Pen(Brush("Vs.Brush.Surface"), 1.5), new Point(X(last), Y(values[last]!.Value)), 3.5, 3.5);
    }

    public override string? TipAt(Point at)
    {
        var data = Data;
        if (data is null || !data.HasParts) return null;
        var values = data.GoodPercentByMinute;
        var i = (int)Math.Round((at.X - Pad) / (Bounds.Width - 2 * Pad) * (values.Count - 1));
        if (i < 0 || i >= values.Count || values[i] is not { } v) return null;
        return S.Format("run.chart.good_tip", data.Minutes[i].Start.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
            v.ToString("0.0", CultureInfo.CurrentCulture));
    }
}

/// <summary>How a tool's score spread over the image set against its pass limit (UI-UX-SPEC.md 4.19):
/// a histogram in bands, the failing side shaded, the limit as a red line. Answers "how close to
/// failing are the good parts?" before the job runs on the line.</summary>
public sealed class MarginChart : ChartBase
{
    public static readonly StyledProperty<ToolMargin?> ScoresProperty =
        AvaloniaProperty.Register<MarginChart, ToolMargin?>(nameof(Scores));

    private const double L = 4, R = 4, T = 16, B = 14;

    static MarginChart()
    {
        AffectsRender<MarginChart>(ScoresProperty);
    }

    public ToolMargin? Scores
    {
        get => GetValue(ScoresProperty);
        set => SetValue(ScoresProperty, value);
    }

    private double X(ToolMargin m, double v) => L + (v - m.From) / (m.To - m.From) * (Bounds.Width - L - R);


    public override void Render(DrawingContext context)
    {
        if (Scores is not { Values.Count: > 0 } m || m.To <= m.From) return;
        var counts = m.Counts();
        var max = Math.Max(1, counts.Max());
        var h = Bounds.Height - T - B;
        var text = Brush("Vs.Brush.Text");
        var axis = Brush("Vs.Brush.Chart.Axis");
        using (context.PushOpacity(0.08))
            context.DrawRectangle(Brush("Vs.Brush.Status.Ng"), null, new Rect(L, T, Math.Max(0, X(m, m.Edge) - L), h));
        context.DrawLine(new Pen(Brush("Vs.Brush.Chart.Grid"), 1), new Point(L, T + h), new Point(Bounds.Width - R, T + h));
        var fill = Brush("Vs.Brush.Chart.S1");
        var band = (m.To - m.From) / m.Bands;
        for (var i = 0; i < m.Bands; i++)
        {
            if (counts[i] == 0) continue;
            var x0 = X(m, m.From + i * band) + 1;
            var x1 = X(m, m.From + (i + 1) * band) - 1;
            var y = T + h - counts[i] / (double)max * h;
            context.DrawRectangle(fill, null, new Rect(x0, y, Math.Max(1, x1 - x0), T + h - y), 2, 2);
        }
        var lx = X(m, m.Edge);
        context.DrawLine(new Pen(Brush("Vs.Brush.Status.Ng"), 2), new Point(lx, T - 4), new Point(lx, T + h));
        var label = S.Format("ws.margin.limit", m.Text(m.Limit));
        var right = lx + 4 + Measure(label, text).Width <= Bounds.Width - R;
        Text(context, label, right ? lx + 4 : lx - 4, T - 7, text, right ? -1 : 1);
        Text(context, m.Text(m.Low), L, Bounds.Height - B / 2, axis);
        Text(context, m.Text(m.High), Bounds.Width - R, Bounds.Height - B / 2, axis, 1);
    }

    public override string? TipAt(Point at)
    {
        if (Scores is not { Values.Count: > 0 } m || m.To <= m.From) return null;
        var band = (m.To - m.From) / m.Bands;
        var i = (int)Math.Floor((at.X - L) / (Bounds.Width - L - R) * m.Bands);
        if (i < 0 || i >= m.Bands) return null;
        return m.Grade
            ? S.Format("ws.margin.grade_tip", m.Text(m.From + (i + 0.5) * band), Number(m.Counts()[i]))
            : S.Format("ws.margin.tip", Number(m.From + i * band), Number(m.From + (i + 1) * band), Number(m.Counts()[i]));
    }
}

/// <summary>Each tool's time on the picture tried, as one stacked bar with a 2 px gap between tools and
/// a legend below, so the slow tool shows at once.</summary>
public sealed class ToolTimeChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<VisionSystem.Studio.Runtime.ToolTried>?> ToolsProperty =
        AvaloniaProperty.Register<ToolTimeChart, IReadOnlyList<VisionSystem.Studio.Runtime.ToolTried>?>(nameof(Tools));

    private const double L = 6, R = 60, Top = 6, BarH = 22;
    private static readonly string[] Series = ["Vs.Brush.Chart.S1", "Vs.Brush.Chart.S2", "Vs.Brush.Chart.S3"];

    static ToolTimeChart()
    {
        AffectsRender<ToolTimeChart>(ToolsProperty);
    }

    public IReadOnlyList<VisionSystem.Studio.Runtime.ToolTried>? Tools
    {
        get => GetValue(ToolsProperty);
        set => SetValue(ToolsProperty, value);
    }

    private double Total => Tools?.Sum(t => Math.Max(0, t.TimeMs)) ?? 0;

    public override void Render(DrawingContext context)
    {
        var tools = Tools;
        if (tools is not { Count: > 0 })
        {
            Empty(context, "ws.no_result");
            return;
        }
        var width = Bounds.Width - L - R;
        var x = L;
        var text = Brush("Vs.Brush.Text");
        for (var i = 0; i < tools.Count && Total > 0; i++)
        {
            var w = Math.Max(0, tools[i].TimeMs) / Total * width;
            var first = i == 0;
            var last = i == tools.Count - 1;
            context.DrawRectangle(Brush(Series[i % Series.Length]), null,
                new Rect(x + (first ? 0 : 1), Top, Math.Max(1, w - (first || last ? 1 : 2)), BarH), first || last ? 3 : 0, first || last ? 3 : 0);
            x += w;
        }
        Text(context, Total.ToString("0.0", CultureInfo.CurrentCulture) + " ms", L + width + 6, Top + BarH / 2, text);
        // As many legend rows as fit; the rest are counted in the last line.
        var rows = Math.Max(1, (int)((Bounds.Height - Top - BarH - 8) / 18));
        var shown = Math.Min(tools.Count, rows * 3);
        if (shown < tools.Count) shown = Math.Max(0, shown - 1);
        for (var i = 0; i < shown; i++)
        {
            var lx = L + (i % 3) * (width / 3);
            var ly = Top + BarH + 16 + (i / 3) * 18;
            context.DrawRectangle(Brush(Series[i % Series.Length]), null, new Rect(lx, ly - 5, 10, 10), 2, 2);
            Text(context, Fit($"{tools[i].Name}  {tools[i].TimeMs.ToString("0.0", CultureInfo.CurrentCulture)} ms", width / 3 - 20, text), lx + 16, ly, text);
        }
        if (shown < tools.Count)
            Text(context, S.Format("run.last_part.more", tools.Count - shown), L + (shown % 3) * (width / 3), Top + BarH + 16 + (shown / 3) * 18, text);
    }

    public override string? TipAt(Point at)
    {
        var tools = Tools;
        if (tools is not { Count: > 0 } || Total <= 0 || at.Y > Top + BarH + 4) return null;
        var x = L;
        foreach (var t in tools)
        {
            var w = Math.Max(0, t.TimeMs) / Total * (Bounds.Width - L - R);
            if (at.X >= x && at.X < x + w) return $"{t.Name}: {t.TimeMs.ToString("0.0", CultureInfo.CurrentCulture)} ms";
            x += w;
        }
        return null;
    }
}
