using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TS570_Remote.Controls;

/// <summary>Visual flavor of a front-panel knob.</summary>
public enum KnobKind
{
    /// <summary>Black knob: nine-flute skirt, satin bevel and flat cap (PHONES, MIC, RIT/XIT, MULTI/CH, outer concentric rings).</summary>
    Ribbed,
    /// <summary>Smaller concentric inner knob sitting on top of an outer ring.</summary>
    Inner,
    /// <summary>Main tuning dial: knurled rubber band, light lip, grey face and a large finger dimple.</summary>
    MainDial,
}

/// <summary>
/// Self-rendered rotary knob modelled on the TS-570D's knobs. Lighting (shadow, shading, gloss)
/// stays fixed while the skirt flutes, pointer and dimple rotate, so turning looks physical.
/// <see cref="TurnTo"/> eases the rotation; hover and drag light a rim glow.
/// </summary>
public sealed class RotaryKnob : FrameworkElement
{
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(KnobKind), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(KnobKind.Ribbed, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    /// <summary>Colour of the painted pointer line; null draws no pointer (endless encoders on the rig have none).</summary>
    public static readonly DependencyProperty PointerBrushProperty = DependencyProperty.Register(
        nameof(PointerBrush), typeof(Brush), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x1A)),
            FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    /// <summary>
    /// Depth of the skirt's nine rounded flutes as a fraction of the radius (rig: ≈0.065 on single knobs
    /// and outer rings, shallower on concentric inner knobs); 0 gives a plain round skirt.
    /// </summary>
    public static readonly DependencyProperty FluteDepthProperty = DependencyProperty.Register(
        nameof(FluteDepth), typeof(double), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(0.065, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    private static readonly DependencyProperty RimGlowProperty = DependencyProperty.Register(
        "RimGlow", typeof(double), typeof(RotaryKnob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration TurnDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly IEasingFunction TurnEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction SweepEase = new QuarticEase { EasingMode = EasingMode.EaseInOut };
    private static readonly Color GlowColor = Color.FromRgb(0xFF, 0x9A, 0x1A);

    private Drawing? _base;     // fixed: contact shadow (dial: rim and band too)
    private Drawing? _grip;     // rotating: skirt silhouette and flutes, or the dial's knurling
    private Geometry? _skirt;   // skirt outline in knob space; clips the fixed shading
    private Drawing? _shade;    // fixed: lighting over the skirt
    private Drawing? _top;      // fixed: cap or dial face
    private Drawing? _pointer;  // rotating: pointer line
    private Drawing? _gloss;    // fixed: soft highlight
    private Pen? _glowPen;
    private Size _cachedFor;

    static RotaryKnob()
    {
        CursorProperty.OverrideMetadata(typeof(RotaryKnob), new FrameworkPropertyMetadata(Cursors.Hand));
        FocusableProperty.OverrideMetadata(typeof(RotaryKnob), new FrameworkPropertyMetadata(false));
    }

    public double Angle
    {
        get => (double)GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public KnobKind Kind
    {
        get => (KnobKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush? PointerBrush
    {
        get => (Brush?)GetValue(PointerBrushProperty);
        set => SetValue(PointerBrushProperty, value);
    }

    public double FluteDepth
    {
        get => (double)GetValue(FluteDepthProperty);
        set => SetValue(FluteDepthProperty, value);
    }

    /// <summary>Rotates to <paramref name="angle"/> (degrees, clockwise from 12 o'clock) with a short ease-out.</summary>
    public void TurnTo(double angle) => TurnTo(angle, TurnDuration, TurnEase);

    /// <summary>Slow eased sweep, used by the power-on sequence.</summary>
    public void SweepTo(double from, double to, TimeSpan delay, TimeSpan duration)
    {
        // Key frames (not BeginTime) so the knob rests on `from` during the delay
        // while the base value already holds the final angle.
        Angle = to;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration), SweepEase));
        BeginAnimation(AngleProperty, anim);
    }

    /// <summary>Jumps to <paramref name="angle"/> without animation.</summary>
    public void SnapTo(double angle)
    {
        BeginAnimation(AngleProperty, null);
        Angle = angle;
    }

    private void TurnTo(double angle, Duration duration, IEasingFunction ease)
    {
        if (!IsLoaded)
        {
            SnapTo(angle);
            return;
        }

        // Base value first, so it stays correct once the animation is released.
        Angle = angle;
        var anim = new DoubleAnimation(angle, duration) { EasingFunction = ease };
        BeginAnimation(AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        AnimateGlow(1.0, 120);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured)
            AnimateGlow(0.0, 380);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!IsMouseOver)
            AnimateGlow(0.0, 380);
    }

    private void AnimateGlow(double to, int ms)
        => BeginAnimation(RimGlowProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms))), HandoffBehavior.SnapshotAndReplace);

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RotaryKnob)d)._base = null;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _base = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        Size size = RenderSize;
        double r = Math.Min(size.Width, size.Height) / 2.0;
        if (r < 4)
            return;

        if (_base is null || _cachedFor != size)
            BuildDrawings(size);

        var c = new Point(size.Width / 2.0, size.Height / 2.0);
        double angle = Angle;

        dc.DrawDrawing(_base);

        var turn = new RotateTransform(angle, c.X, c.Y);
        dc.PushTransform(turn);
        dc.DrawDrawing(_grip);
        if (_skirt is not null && _shade is not null)
        {
            // Light the turning silhouette from the room: clip to the rotated skirt, then undo the turn.
            dc.PushClip(_skirt);
            dc.PushTransform(new RotateTransform(-angle, c.X, c.Y));
            dc.DrawDrawing(_shade);
            dc.Pop();
            dc.Pop();
        }
        dc.Pop();

        dc.DrawDrawing(_top);

        if (Kind == KnobKind.MainDial)
            DrawDimple(dc, c, r, angle);

        dc.PushTransform(turn);
        dc.DrawDrawing(_pointer);
        dc.Pop();

        dc.DrawDrawing(_gloss);

        double glow = (double)GetValue(RimGlowProperty);
        if (glow > 0.01 && _glowPen is not null)
        {
            dc.PushOpacity(glow);
            dc.DrawEllipse(null, _glowPen, c, r + 1.4, r + 1.4);
            dc.Pop();
        }
    }

    private void BuildDrawings(Size size)
    {
        _cachedFor = size;
        double r = Math.Min(size.Width, size.Height) / 2.0;
        var c = new Point(size.Width / 2.0, size.Height / 2.0);
        bool dial = Kind == KnobKind.MainDial;

        var baseG = new DrawingGroup();
        var gripG = new DrawingGroup();
        var shadeG = new DrawingGroup();
        var topG = new DrawingGroup();
        var ptrG = new DrawingGroup();
        var glossG = new DrawingGroup();
        Geometry? skirt = null;

        using (var dc = baseG.Open())
        {
            DrawContactShadow(dc, c, r, dial ? 0.05 : 0.10, Kind == KnobKind.Inner ? 0.75 : 0.85);
            if (dial)
            {
                // Outer black rim, then the darker rubber band carrying the knurling.
                dc.DrawEllipse(Lin(0, 0, 0, 1, (0, "#FF2E2D31"), (1, "#FF0E0E10")), new Pen(Solid("#FF060607"), 0.8), c, r, r);
                dc.DrawEllipse(Lin(0, 0, 0, 1, (0, "#FF3A383C"), (0.5, "#FF29282C"), (1, "#FF1B1A1D")), null, c, r * 0.93, r * 0.93);
            }
        }

        using (var dc = gripG.Open())
        {
            if (dial)
            {
                int ribs = Math.Max(60, (int)Math.Round(2 * Math.PI * r * 0.93 / 3.4));
                var groove = new Pen(Solid("#E0060607"), Math.Max(1.0, r * 0.013));
                var crest = new Pen(Solid("#24FFFFFF"), 0.6);
                for (int i = 0; i < ribs; i++)
                {
                    double a = i * 360.0 / ribs;
                    dc.DrawLine(groove, Polar(c, r * 0.875, a), Polar(c, r * 0.97, a));
                    dc.DrawLine(crest, Polar(c, r * 0.88, a + 1.4), Polar(c, r * 0.965, a + 1.4));
                }
            }
            else
            {
                double depth = FluteDepth;
                skirt = depth > 0 ? FlutedOutline(c, r, depth) : new EllipseGeometry(c, r, r);
                skirt.Freeze();
                dc.DrawGeometry(Solid("#FF202125"), new Pen(Solid("#FF060607"), 0.8), skirt);
                if (depth > 0)
                {
                    // Each flute runs up the skirt as a channel towards the bevel, darker the deeper it is cut.
                    byte shade = (byte)Math.Round(Math.Min(1, depth / 0.065) * 0x40);
                    var channel = new Pen(new SolidColorBrush(Color.FromArgb(shade, 0, 0, 0)), r * 0.17) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    double top = CapRadius(Kind) + BevelWidth;
                    dc.PushClip(skirt);
                    for (int i = 0; i < FluteCount; i++)
                    {
                        double a = (i + 0.5) * 360.0 / FluteCount;
                        dc.DrawLine(channel, Polar(c, r * (1 - depth), a), Polar(c, r * top, a));
                    }
                    dc.Pop();
                }
            }
        }

        if (skirt is not null)
        {
            using var dc = shadeG.Open();
            // Top light falling off towards the bottom, plus a soft sheen along the outer edge.
            dc.DrawEllipse(Lin(0, 0, 0, 1, (0, "#3CFFFFFF"), (0.45, "#08FFFFFF"), (1, "#50000000")), null, c, r, r);
            var rim = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.84), new GradientStop(Color.FromArgb(0x26, 255, 255, 255), 1) } };
            rim.Freeze();
            dc.DrawEllipse(rim, null, c, r, r);
        }

        using (var dc = topG.Open())
        {
            if (dial)
                DrawDialFace(dc, c, r);
            else
            {
                // Satin metal bevel round the top of the skirt, then the flat black cap.
                double capR = r * CapRadius(Kind);
                dc.DrawEllipse(Lin(0.25, 0, 0.75, 1, (0, "#FFD4D6DC"), (0.3, "#FF9A9DA5"), (0.62, "#FF4A4C53"), (1, "#FF7C7F87")),
                    new Pen(Solid("#FF08080A"), 0.7), c, r * (CapRadius(Kind) + BevelWidth), r * (CapRadius(Kind) + BevelWidth));
                dc.DrawEllipse(Lin(0, 0, 0, 1, (0, "#FF34353A"), (0.5, "#FF222328"), (1, "#FF16171A")),
                    new Pen(Solid("#FF0B0B0D"), 0.8), c, capR, capR);
            }
        }

        using (var dc = ptrG.Open())
        {
            if (!dial && PointerBrush is { } ptr)
            {
                // Inner knobs: line across the cap; outer rings: short mark on the skirt edge.
                double from = Kind == KnobKind.Inner ? 0.18 : 0.70;
                double to = Kind == KnobKind.Inner ? 0.66 : 0.97;
                double th = Math.Max(1.4, r * (Kind == KnobKind.Inner ? 0.085 : 0.07));
                Point p0 = Polar(c, r * from, 0), p1 = Polar(c, r * to, 0);
                dc.DrawLine(new Pen(Solid("#C0000000"), th + 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, p0, p1);
                dc.DrawLine(new Pen(ptr, th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, p0, p1);
            }
        }

        using (var dc = glossG.Open())
        {
            double gr = dial ? r * 0.845 : r * CapRadius(Kind);
            var gloss = new RadialGradientBrush
            {
                Center = new Point(0.36, 0.24),
                GradientOrigin = new Point(0.36, 0.2),
                RadiusX = 0.62,
                RadiusY = 0.5,
            };
            gloss.GradientStops.Add(new GradientStop(Color.FromArgb(dial ? (byte)0x22 : (byte)0x1E, 255, 255, 255), 0));
            gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            gloss.Freeze();
            dc.DrawEllipse(gloss, null, c, gr, gr);
        }

        _glowPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, GlowColor.R, GlowColor.G, GlowColor.B)), dial ? 2.2 : 1.5);
        _glowPen.Freeze();

        baseG.Freeze(); gripG.Freeze(); shadeG.Freeze(); topG.Freeze(); ptrG.Freeze(); glossG.Freeze();
        _base = baseG; _grip = gripG; _top = topG; _pointer = ptrG; _gloss = glossG;
        _skirt = skirt;
        _shade = skirt is null ? null : shadeG;
    }

    // Skirt flutes measured on the rig: nine rounded scallops with soft shoulders; a lobe sits at
    // 12 o'clock so the pointer mark lands on it. Depth comes from FluteDepth.
    private const int FluteCount = 9;
    private const double FluteRadius = 0.22;
    private const double FluteFillet = 0.12;

    /// <summary>
    /// Outline of the fluted skirt: lobe arcs on the knob radius, each flute a concave arc joined to
    /// the lobes by convex fillets tangent to both, so the edge has no sharp corners.
    /// </summary>
    private static StreamGeometry FlutedOutline(Point c, double r, double depth)
    {
        double rn = r * FluteRadius, rf = r * FluteFillet;
        double notchDist = r * (1 - depth) + rn;        // flute circle centre, from the knob centre
        double filletDist = r - rf;                     // fillet circle centre, from the knob centre
        // Angle between a flute centre and its fillet centres (fillet tangent to the flute circle).
        double phi = Math.Acos((filletDist * filletDist + notchDist * notchDist - (rn + rf) * (rn + rf))
                               / (2 * filletDist * notchDist)) * 180 / Math.PI;
        double step = 360.0 / FluteCount;

        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(Polar(c, r, -step / 2 + phi), true, true);
            for (int i = 0; i < FluteCount; i++)
            {
                double mid = (i + 0.5) * step;
                Point n = Polar(c, notchDist, mid);
                Point onFlute(double filletDeg)
                {
                    Vector v = Polar(c, filletDist, filletDeg) - n;
                    v.Normalize();
                    return n + v * rn;
                }

                g.ArcTo(Polar(c, r, mid - phi), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.ArcTo(onFlute(mid - phi), new Size(rf, rf), 0, false, SweepDirection.Clockwise, true, false);
                g.ArcTo(onFlute(mid + phi), new Size(rn, rn), 0, false, SweepDirection.Counterclockwise, true, false);
                g.ArcTo(Polar(c, r, mid + phi), new Size(rf, rf), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        return geo;
    }

    private static void DrawContactShadow(DrawingContext dc, Point c, double r, double drop, double solid)
    {
        var shadow = new RadialGradientBrush();
        shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0, 0, 0), 0));
        shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0xA0, 0, 0, 0), solid));
        shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
        shadow.Freeze();
        double sr = r * 1.12;
        dc.DrawEllipse(shadow, null, new Point(c.X, c.Y + r * drop), sr, sr);
    }

    private const double BevelWidth = 0.09;

    private static double CapRadius(KnobKind kind) => kind == KnobKind.Inner ? 0.70 : 0.64;

    /// <summary>
    /// Bright lip ring round a large grey face with a faint spun-metal sheen (light bands along the
    /// upper-left / lower-right diagonal, fixed to the room light so they never turn with the dial).
    /// </summary>
    private static void DrawDialFace(DrawingContext dc, Point c, double r)
    {
        double lip = r * 0.87;
        dc.DrawEllipse(Lin(0.2, 0, 0.8, 1, (0, "#FFE2E3E7"), (0.35, "#FFA7A9AF"), (0.7, "#FF55575D"), (1, "#FF8D9096")), null, c, lip, lip);

        double face = r * 0.845;
        var faceFill = new RadialGradientBrush
        {
            Center = new Point(0.45, 0.4),
            GradientOrigin = new Point(0.4, 0.34),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x8C, 0x8D, 0x92), 0),
                new GradientStop(Color.FromRgb(0x77, 0x78, 0x7D), 0.6),
                new GradientStop(Color.FromRgb(0x5F, 0x60, 0x65), 1),
            },
        };
        faceFill.Freeze();
        dc.DrawEllipse(faceFill, new Pen(Solid("#70000000"), 0.8), c, face, face);

        // Spun finish: 5° wedges whose brightness follows cos(2θ) around the light diagonal.
        const int wedges = 72;
        for (int i = 0; i < wedges; i++)
        {
            double a0 = i * 360.0 / wedges, a1 = a0 + 360.0 / wedges;
            double k = Math.Cos(2 * ((a0 + a1) / 2 + 45) * Math.PI / 180);
            byte alpha = (byte)Math.Round(Math.Abs(k) * 0x2A);
            if (alpha < 3)
                continue;
            Color tint = k > 0 ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(c, true, true);
                ctx.LineTo(Polar(c, face, a0), false, false);
                ctx.ArcTo(Polar(c, face, a1), new Size(face, face), 0, false, SweepDirection.Clockwise, false, false);
            }
            g.Freeze();
            dc.DrawGeometry(Frozen(new SolidColorBrush(tint)), null, g);
        }
    }

    /// <summary>Concave finger dimple (≈30 % of the dial diameter); drawn per frame so its lighting stays world-aligned while it orbits.</summary>
    private static void DrawDimple(DrawingContext dc, Point c, double r, double angle)
    {
        Point p = Polar(c, r * 0.41, angle - 60);
        double dr = r * 0.29;
        dc.DrawEllipse(DimpleFill, null, p, dr, dr);
        dc.DrawEllipse(null, DimpleRim, p, dr, dr);
    }

    // Light from the upper left: the upper-left wall of the hollow falls in shadow, the lower-right wall catches light.
    private static readonly Brush DimpleFill = Frozen(new RadialGradientBrush
    {
        Center = new Point(0.6, 0.64),
        GradientOrigin = new Point(0.66, 0.74),
        RadiusX = 0.72,
        RadiusY = 0.72,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xC8, 0xCA, 0xD0), 0),
            new GradientStop(Color.FromRgb(0x8E, 0x90, 0x96), 0.45),
            new GradientStop(Color.FromRgb(0x4A, 0x4C, 0x52), 1),
        },
    });

    private static readonly Pen DimpleRim = Frozen(new Pen(Lin(0.15, 0.1, 0.85, 0.9, (0, "#C0000000"), (0.5, "#30000000"), (1, "#A0FFFFFF")), 1.4));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Point Polar(Point c, double radius, double degreesFromTop)
    {
        double rad = (degreesFromTop - 90) * Math.PI / 180.0;
        return new Point(c.X + radius * Math.Cos(rad), c.Y + radius * Math.Sin(rad));
    }

    private static SolidColorBrush Solid(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush Lin(double x0, double y0, double x1, double y1, params (double Offset, string Color)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1) };
        foreach (var (offset, color) in stops)
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        b.Freeze();
        return b;
    }
}
