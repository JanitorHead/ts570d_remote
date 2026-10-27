using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TS570_Remote.Controls;

/// <summary>Silk-screened legend printed on the faceplate around a knob.</summary>
public enum KnobScaleKind
{
    /// <summary>0–10 pot scale (AF/RF, IF SHIFT/SQL): 11 ticks over ±150°, even numbers printed.</summary>
    Numbers,
    /// <summary>11 plain ticks over ±150° (PHONES / MIC level knobs).</summary>
    Level,
    /// <summary>Detent ticks every 30° all round (MULTI/CH).</summary>
    Ticks,
    /// <summary>RIT/XIT: arcs from the legend down to arrowheads, "−" and "+" under the tips.</summary>
    RitArrows,
    /// <summary>DSP SLOPE: arcs rising to arrowheads, "LOW CUT" / "HIGH CUT" set in the middle of each arc.</summary>
    SlopeArcs,
}

/// <summary>
/// Static faceplate print around a knob, concentric with it: the dark recess the knob sits in
/// plus ticks, numbers or arrowed arcs. Place it centred on the knob; <see cref="KnobRadius"/>
/// is the knob's outer radius and <see cref="ScaleRadius"/> the radius where ticks start or arcs run.
/// Angles: degrees clockwise from 12 o'clock, matching <see cref="RotaryKnob"/> (pots sweep ±150°).
/// </summary>
public sealed class KnobScale : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(KnobScaleKind), typeof(KnobScale),
        new FrameworkPropertyMetadata(KnobScaleKind.Numbers, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KnobRadiusProperty = DependencyProperty.Register(
        nameof(KnobRadius), typeof(double), typeof(KnobScale),
        new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleRadiusProperty = DependencyProperty.Register(
        nameof(ScaleRadius), typeof(double), typeof(KnobScale),
        new FrameworkPropertyMetadata(38.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Half sweep of a pot, shared with the code that maps values to knob angles.</summary>
    public const double PotSweep = 150;

    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xEF, 0xF2)));
    private static readonly Pen TickPen = Frozen(new Pen(Ink, 1.4) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat });
    private static readonly Pen ArcPen = Frozen(new Pen(Ink, 1.35));
    private static readonly Typeface LabelFace = new(
        new FontFamily("pack://application:,,,/Resources/Fonts/helvetica-bold.ttf#Helvetica"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    static KnobScale()
    {
        IsHitTestVisibleProperty.OverrideMetadata(typeof(KnobScale), new UIPropertyMetadata(false));
    }

    public KnobScaleKind Kind
    {
        get => (KnobScaleKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double KnobRadius
    {
        get => (double)GetValue(KnobRadiusProperty);
        set => SetValue(KnobRadiusProperty, value);
    }

    public double ScaleRadius
    {
        get => (double)GetValue(ScaleRadiusProperty);
        set => SetValue(ScaleRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(RenderSize.Width / 2.0, RenderSize.Height / 2.0);
        double sr = ScaleRadius;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DrawRecess(dc, c, KnobRadius);

        switch (Kind)
        {
            case KnobScaleKind.Numbers:
                for (int i = 0; i <= 10; i++)
                {
                    double a = -PotSweep + 30 * i;
                    dc.DrawLine(TickPen, Polar(c, sr, a), Polar(c, sr + 4.5, a));
                    if (i % 2 == 0)
                    {
                        string n = i.ToString(CultureInfo.InvariantCulture);
                        FormattedText ft = Format(n, 9, dpi);
                        // Numbers hug the tick end: the gap grows with the glyph's half-extent along the radius.
                        double rad = a * Math.PI / 180;
                        double gap = 1.6 + Math.Abs(Math.Sin(rad)) * ft.WidthIncludingTrailingWhitespace / 2
                                         + Math.Abs(Math.Cos(rad)) * ft.Extent / 2;
                        DrawCentered(dc, ft, Polar(c, sr + 4.5 + gap, a), 0);
                    }
                }
                break;

            case KnobScaleKind.Level:
                for (int i = 0; i <= 10; i++)
                {
                    double a = -PotSweep + 30 * i;
                    dc.DrawLine(TickPen, Polar(c, sr, a), Polar(c, sr + (i % 5 == 0 ? 5 : 3.5), a));
                }
                break;

            case KnobScaleKind.Ticks:
                for (int i = 0; i < 12; i++)
                    dc.DrawLine(TickPen, Polar(c, sr, i * 30), Polar(c, sr + 5.5, i * 30));
                break;

            case KnobScaleKind.RitArrows:
                // Gap at the top holds the RIT/XIT legend; arrows run down both sides to − / +.
                dc.DrawGeometry(null, ArcPen, Arc(c, sr, -14, -73));
                dc.DrawGeometry(null, ArcPen, Arc(c, sr, 14, 73));
                DrawArrowHead(dc, c, sr, -81, -1);
                DrawArrowHead(dc, c, sr, 81, 1);
                DrawCentered(dc, Format("−", 11, dpi), Polar(c, sr, -97), 0);
                DrawCentered(dc, Format("+", 11, dpi), Polar(c, sr, 97), 0);
                break;

            case KnobScaleKind.SlopeArcs:
                DrawSlopeSide(dc, c, sr, -1, "LOW CUT", dpi);
                DrawSlopeSide(dc, c, sr, 1, "HIGH CUT", dpi);
                break;
        }
    }

    /// <summary>The dark ring moulded into the faceplate around the knob, lit on its lower lip.</summary>
    private static void DrawRecess(DrawingContext dc, Point c, double knobRadius)
    {
        double rr = knobRadius * 1.12;
        dc.DrawEllipse(RecessFill, null, c, rr, rr);
        dc.DrawEllipse(null, RecessLip, c, rr, rr);
    }

    private static readonly Brush RecessFill = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x0B, 0x10)));
    private static readonly Pen RecessLip = Frozen(new Pen(new LinearGradientBrush(
        Color.FromArgb(0x60, 0, 0, 0), Color.FromArgb(0x30, 255, 255, 255), 90), 0.9));

    /// <summary>
    /// One DSP SLOPE side, mirrored by <paramref name="side"/> (−1 left, +1 right): a dot at the
    /// bottom, the arc broken by the legend set along it, then an arrowhead near the top.
    /// </summary>
    private static void DrawSlopeSide(DrawingContext dc, Point c, double radius, int side, string legend, double dpi)
    {
        const double bottom = 150, tip = 34, centre = 90;
        FormattedText[] glyphs = FormatEach(legend, 9, dpi, out double textLen);
        double halfSpan = textLen / radius * 180 / Math.PI / 2 + 4;

        dc.DrawEllipse(Ink, null, Polar(c, radius, side * bottom), 1.2, 1.2);
        dc.DrawGeometry(null, ArcPen, Arc(c, radius, side * (bottom - 3), side * (centre + halfSpan)));
        dc.DrawGeometry(null, ArcPen, Arc(c, radius, side * (centre - halfSpan), side * (tip + 9)));
        DrawArrowHead(dc, c, radius, side * tip, -side);

        // Left side reads upwards (angles increase), right side reads downwards (angles increase too).
        DrawOnArc(dc, glyphs, textLen, c, radius, side * centre);
    }

    /// <summary>Filled arrowhead tangent to the arc, tip at <paramref name="tipDeg"/>; dir +1 points clockwise.</summary>
    private static void DrawArrowHead(DrawingContext dc, Point c, double radius, double tipDeg, int dir)
    {
        Point tip = Polar(c, radius, tipDeg);
        Point back = Polar(c, radius, tipDeg - dir * 10);
        Vector along = tip - back;
        along.Normalize();
        var side = new Vector(-along.Y, along.X) * 3.1;
        Point b0 = tip - along * 7;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(tip, true, true);
            g.LineTo(b0 + side, false, false);
            g.LineTo(b0 - side, false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(Ink, null, geo);
    }

    /// <summary>Sets glyphs along a circle centred on the arc line, centred on <paramref name="centerDeg"/>, tops facing outward.</summary>
    private static void DrawOnArc(DrawingContext dc, FormattedText[] glyphs, double total, Point c, double radius, double centerDeg)
    {
        double deg = centerDeg - total / radius * 180 / Math.PI / 2;
        foreach (FormattedText g in glyphs)
        {
            double w = g.WidthIncludingTrailingWhitespace;
            double mid = deg + w / 2 / radius * 180 / Math.PI;
            DrawCentered(dc, g, Polar(c, radius, mid), mid);
            deg += w / radius * 180 / Math.PI;
        }
    }

    private static FormattedText[] FormatEach(string text, double size, double dpi, out double total)
    {
        var glyphs = new FormattedText[text.Length];
        total = 0;
        for (int i = 0; i < text.Length; i++)
        {
            glyphs[i] = Format(text[i].ToString(), size, dpi);
            total += glyphs[i].WidthIncludingTrailingWhitespace;
        }
        return glyphs;
    }

    private static void DrawCentered(DrawingContext dc, FormattedText ft, Point at, double rotateDeg)
    {
        if (rotateDeg != 0)
            dc.PushTransform(new RotateTransform(rotateDeg, at.X, at.Y));
        // Centre on the glyph ink, not the line box, so text sits optically on the arc / tick.
        double inkCentre = ft.Height + ft.OverhangAfter - ft.Extent / 2;
        dc.DrawText(ft, new Point(at.X - ft.WidthIncludingTrailingWhitespace / 2, at.Y - inkCentre));
        if (rotateDeg != 0)
            dc.Pop();
    }

    private static FormattedText Format(string text, double size, double dpi)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, size, Ink, dpi);

    private static StreamGeometry Arc(Point c, double radius, double fromDeg, double toDeg)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(Polar(c, radius, fromDeg), false, false);
            g.ArcTo(Polar(c, radius, toDeg), new Size(radius, radius), 0, Math.Abs(toDeg - fromDeg) > 180,
                toDeg > fromDeg ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    private static Point Polar(Point c, double radius, double degreesFromTop)
    {
        double rad = (degreesFromTop - 90) * Math.PI / 180.0;
        return new Point(c.X + radius * Math.Cos(rad), c.Y + radius * Math.Sin(rad));
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
