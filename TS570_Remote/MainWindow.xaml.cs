using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TS570_Remote
{
    public partial class MainWindow : Window
    {
        private sealed class CatActionMap
        {
            public string? OneShotCommand { get; set; }
            public string? OnCommand { get; set; }
            public string? OffCommand { get; set; }
            public string? StepUpCommand { get; set; }
            public string? StepDownCommand { get; set; }
            public string? SetValueCommand { get; set; }
            public bool RequireConfirmation { get; set; }
            public string? ConfirmationMessage { get; set; }
        }

        private sealed class CatCommandMap
        {
            public List<string> Namespaces { get; set; } = new();
            public Dictionary<string, CatActionMap> Actions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        // ── OmniRig ──────────────────────────────────────────────────────────
        private OmniRig.OmniRigXClass? rigControl;
        private DispatcherTimer pollingTimer = null!;
        private readonly HashSet<string> _missingMapWarnings = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _mapFilePath = Path.Combine(AppContext.BaseDirectory, "CatCommandMap.json");
        private CatCommandMap _catMap = CreateDefaultCommandMap();

        // ── Tuning steps ───────────────────────────────────────────────────────
        private static readonly int[] TuneSteps = { 10, 100, 500, 1_000, 2_500, 5_000, 10_000, 100_000 };
        private int _tuneStepIndex = 3;  // default: 1 kHz

        // ── Toggle state flags ────────────────────────────────────────────────
        private bool _attOn    = false;
        private bool _preAmpOn = false;
        private bool _voxOn    = false;
        private bool _procOn   = false;
        private bool _isTx     = false;
        private bool _ritOn    = false;
        private bool _xitOn    = false;
        private bool _splitOn  = false;
        private bool _scanOn   = false;
        private bool _nbOn     = false;
        private bool _fineOn   = false;
        private bool _fLockOn  = false;
        private bool _step1MHz = false;
        private bool _agcFast  = false;
        private bool _bcOn     = false;
        private bool _atOn     = false;   // in-line tuner
        private bool _memMode  = false;
        private int  _nrState  = 0;       // 0=off  1=NR1  2=NR2
        private int  _antSel   = 1;       // 1 or 2
        private int  _vfoSel   = 0;       // 0=A  1=B
        private int  _qmCh     = 1;       // active quick memo channel (1-5)
        private double _afRfOuterAngle = 0;
        private double _afRfInnerAngle = 0;
        private double _ifSqlOuterAngle = 0;
        private double _ifSqlInnerAngle = 0;
        private double _dspSlopeOuterAngle = 0;
        private double _dspSlopeInnerAngle = 0;
        private int _afValue = 50;
        private int _rfValue = 50;
        private int _ifShiftValue = 50;
        private int _sqlValue = 50;
        private int _dspHighValue = 50;
        private int _dspLowValue = 50;
        private int _phonesValue = 50;
        private int _micGainValue = 50;
        private int _txMicValue = 50;
        private int _txPwrValue = 0;
        private bool _txPwrKnown = false;
        private int _txKeyValue = 50;
        private int _txDelayValue = 50;
        private int _multiChValue = 1;
        private int _ritXitValue = 50;
        private double _ritXitAngle = 0;
        private double _multiChAngle = 0;
        private double _vfoMainAngle = 0;
        private int _localVfoHz = 7_074_000;
        private DateTime _manualFreqOverrideUntilUtc = DateTime.MinValue;
        private bool _isDraggingKnob;
        private Point _lastKnobPoint;
        private bool _isDraggingVfo;
        private Point _lastVfoPoint;
        private double _vfoDragAccumulator;

        // ── Active TX parameter ───────────────────────────────────────────────
        private enum TxParam { None, Mic, Pwr, Key, Delay }
        private TxParam _activeTxParam = TxParam.None;

        // ── Direct frequency entry ────────────────────────────────────────────
        private bool   _freqEntry  = false;
        private string _freqBuffer = "";

        // ── CAT meters (CustomReply) ─────────────────────────────────────────
        private bool _omniRigCustomReplyHooked;
        private int _txCatMeterSeq; // 0..3: SM (S/PWR), RM1 (SWR), RM;, RM2 (ALC), RM;
        private int _rmParseAs;     // 1=SWR, 2=ALC (after RM1/RM2 select)

        private bool _hasCatSm, _hasCatPwr, _hasCatSwr, _hasCatAlc;
        private double _catSmN, _catPwrN, _catSwrN, _catAlcN;
        private int _catSmRaw = -1;
        private readonly List<Border> _sharedMeterSegments = new();
        private readonly List<Border> _swrMeterSegments = new();
        private readonly List<Border> _alcMeterSegments = new();
        private const int SharedMeterSegmentCount = 33; // S-meter / PWR shared arc
        private const int AuxMeterSegmentCount = 20;    // SWR / ALC arcs
        private const double MeterPad = 7.0;
        private const double MeterGhostOpacity = 20.0 / 255.0; // Same as VFO ghost (#14...)

        // ══════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ══════════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            UpdateFrequencyCanvasOffsets(_localVfoHz);
            LoadCommandMap();
            ValidateRequiredLeftPanelMappings();
            InitializeCatConnection();
            Closed += (_, _) => DetachOmniRigCustomReply();
            InitializeSharedMeterSegments();
            InitializeMeterLegends();
            UpdateLcdBadges();        // initial state
            UpdateTuneStepLabel();
        }

        // ══════════════════════════════════════════════════════════════════════
        // OMNIRIG CONNECTION
        // ══════════════════════════════════════════════════════════════════════
        private void InitializeCatConnection()
        {
            try
            {
                rigControl = new OmniRig.OmniRigXClass();
                pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                pollingTimer.Tick += PollingTimer_Tick;
                pollingTimer.Start();
                ledCatGreen.Visibility = Visibility.Visible;
                ledCatAmber.Visibility = Visibility.Hidden;
                txtStatusLeft.Text = "OmniRig OK – sondeando...";
                TryAttachOmniRigCustomReply();
            }
            catch (Exception ex)
            {
                MessageBox.Show("OmniRig no disponible: " + ex.Message,
                    "Error de conexión", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtStatusLeft.Text = "Sin conexión OmniRig";
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // POLLING TIMER
        // ══════════════════════════════════════════════════════════════════════
        private void PollingTimer_Tick(object? sender, EventArgs e)
        {
            if (rigControl?.Rig1 == null) return;
            try
            {
                // Frequency
                int hz = rigControl.Rig1.Freq;
                _localVfoHz = hz;
                if (!_freqEntry)
                {
                    int shownHz = DateTime.UtcNow < _manualFreqOverrideUntilUtc ? _localVfoHz : hz;
                    txtFrequency.Text = FormatFrequency(shownHz);
                    UpdateFrequencyCanvasOffsets(shownHz);
                }

                // Mode
                var mode = rigControl.Rig1.Mode;
                txtMode.Text = GetModeName(mode);
                UpdateModeButtonStyles(mode);

                // TX/RX state from the radio
                bool tx = rigControl.Rig1.Tx == OmniRig.RigParamX.PM_TX;
                if (tx != _isTx) { _isTx = tx; _txCatMeterSeq = 0; _rmParseAs = 0; UpdateLcdBadges(); }

                // Keep TX power badge synchronized with rig state.
                if (TryReadTxPower(out int pwrNow))
                {
                    int normalized = NormalizeTxPower(pwrNow);
                    if (normalized != _txPwrValue)
                    {
                        _txPwrValue = normalized;
                        if (_activeTxParam == TxParam.Pwr)
                            _multiChValue = _txPwrValue;
                        UpdateLcdBadges();
                    }
                    _txPwrKnown = true;
                }

                UpdateDisplayMeters();
                RequestNextKenwoodMeterQuery();

                // Status bar
                txtStatusLeft.Text =
                    $"Rig: {rigControl.Rig1.StatusStr}  " +
                    $"{hz / 1_000_000}.{(hz / 1_000) % 1_000:D3} kHz  " +
                    $"VFO {(_vfoSel == 0 ? "A" : "B")}";

                ledCatGreen.Visibility = Visibility.Visible;
                ledCatAmber.Visibility = Visibility.Hidden;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Poll error: " + ex.Message);
                ledCatGreen.Visibility = Visibility.Hidden;
                ledCatAmber.Visibility = Visibility.Visible;
            }
        }

        private void DetachOmniRigCustomReply()
        {
            if (!_omniRigCustomReplyHooked || rigControl == null) return;
            try
            {
                rigControl.CustomReply -= OmniRigX_CustomReply;
            }
            catch (Exception ex) { Debug.WriteLine("CustomReply unhook: " + ex.Message); }
            _omniRigCustomReplyHooked = false;
        }

        private void TryAttachOmniRigCustomReply()
        {
            if (rigControl == null || _omniRigCustomReplyHooked) return;
            try
            {
                rigControl.CustomReply += OmniRigX_CustomReply;
                _omniRigCustomReplyHooked = true;
            }
            catch (Exception ex) { Debug.WriteLine("CustomReply hook: " + ex.Message); }
        }

        private void OmniRigX_CustomReply(int rigNumber, object command, object reply)
            => OnKenwoodCustomReplyData(rigNumber, command, reply);

        private void OnKenwoodCustomReplyData(int rigNumber, object command, object reply)
        {
            if (rigNumber != 1) return;
            string cmd = VariantToAnsiString(command);
            string ans = VariantToAnsiString(reply);
            _ = Dispatcher.BeginInvoke(() => ApplyKenwoodMeterReply(cmd, ans));
        }

        private static string VariantToAnsiString(object? o)
        {
            if (o is null) return "";
            if (o is string s) return s;
            if (o is byte[] b) return Encoding.ASCII.GetString(b);
            if (o is sbyte[] sb)
            {
                var tmp = new byte[sb.Length];
                Buffer.BlockCopy(sb, 0, tmp, 0, tmp.Length);
                return Encoding.ASCII.GetString(tmp);
            }
            if (o is char[] c) return new string(c);
            try
            {
                var t = o.GetType();
                if (t == typeof(object[]) || t.IsArray)
                {
                    var arr = (Array)o;
                    var sb2 = new StringBuilder(arr.Length);
                    foreach (var x in arr)
                    {
                        if (x is byte y) sb2.Append((char)y);
                        else if (x is sbyte z) sb2.Append((char)z);
                        else sb2.Append(Convert.ToChar(x));
                    }
                    return sb2.ToString();
                }
            }
            catch { }
            return o.ToString() ?? "";
        }

        private void ApplyKenwoodMeterReply(string sentCmd, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            string r = reply.Trim();
            if (r.Length == 0) return;

            // TS-570: SM returns meter value (0..15). In TX this is RF power reading.
            if (sentCmd.Contains("SM", StringComparison.Ordinal) && r.StartsWith("SM", StringComparison.Ordinal))
            {
                if (TryParseSmReply(r, out int raw) && raw >= 0)
                {
                    _catSmRaw = raw;
                    int squares = SmRawToSquares(raw);
                    double n = squares / 20.0;
                    if (_isTx) { _catPwrN = n; _hasCatPwr = true; }
                    else { _catSmN = n; _hasCatSm = true; }
                }
                return;
            }

            // Power read (PC;)
            if (sentCmd.Contains("PC;", StringComparison.Ordinal) && r.StartsWith("PC", StringComparison.Ordinal))
            {
                var m = Regex.Match(r, @"^PC(\d{3});", RegexOptions.CultureInvariant);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int pc))
                {
                    _catPwrN = Math.Clamp(pc / 100.0, 0, 1);
                    _hasCatPwr = true;
                }
                return;
            }

            if (sentCmd.Contains("RM;", StringComparison.Ordinal) && r.StartsWith("RM", StringComparison.Ordinal))
            {
                // TS-570 format: RMp1p2; where p1 meter switch (0..3), p2 meter value (0000..0008)
                var m = Regex.Match(r, @"^RM([0-3])(\d{4});", RegexOptions.CultureInvariant);
                if (m.Success && int.TryParse(m.Groups[2].Value, out int raw))
                {
                    double n = Math.Clamp(raw / 8.0, 0, 1);
                    if (_rmParseAs == 1) { _catSwrN = n; _hasCatSwr = true; }
                    else if (_rmParseAs == 2) { _catAlcN = n; _hasCatAlc = true; }
                    _rmParseAs = 0;
                }
            }
        }

        private static bool TryParseSmReply(string r, out int value)
        {
            value = 0;
            var m = Regex.Match(r, @"^SM(\d{4});", RegexOptions.CultureInvariant);
            if (m.Success) return int.TryParse(m.Groups[1].Value, out value);
            m = Regex.Match(r, @"^SM(\d{2,3});", RegexOptions.CultureInvariant);
            if (m.Success) return int.TryParse(m.Groups[1].Value, out value);
            m = Regex.Match(r, @"^SM(\d{1,3});", RegexOptions.CultureInvariant);
            if (m.Success) return int.TryParse(m.Groups[1].Value, out value);
            return false;
        }

        private void RequestNextKenwoodMeterQuery()
        {
            if (rigControl?.Rig1 == null || !_omniRigCustomReplyHooked) return;
            try
            {
                if (!_isTx)
                {
                    rigControl.Rig1.SendCustomCommand("SM;", 7, ";");
                    return;
                }
                // TS-570 TX sequence: SM (PWR), RM1 + RM (SWR), RM2 + RM (ALC).
                switch (_txCatMeterSeq)
                {
                    case 0: rigControl.Rig1.SendCustomCommand("SM;", 7, ";"); break;
                    case 1: rigControl.Rig1.SendCustomCommand("RM1;", 0, ""); break;
                    case 2: _rmParseAs = 1; rigControl.Rig1.SendCustomCommand("RM;", 8, ";"); break;
                    case 3: rigControl.Rig1.SendCustomCommand("RM2;", 0, ""); break;
                    case 4: _rmParseAs = 2; rigControl.Rig1.SendCustomCommand("RM;", 8, ";"); break;
                }
                _txCatMeterSeq = (_txCatMeterSeq + 1) % 5;
            }
            catch (Exception ex) { Debug.WriteLine("Meter query: " + ex.Message); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // HELPER: send raw CAT command through OmniRig
        // ══════════════════════════════════════════════════════════════════════
        private void SendCAT(string cmd)
        {
            _ = TryExecuteCat(cmd, out _);
        }

        private bool TryReadTxPower(out int value)
        {
            value = _txPwrValue;
            if (rigControl?.Rig1 == null) return false;

            try
            {
                // OmniRig COM wrappers differ by driver; try common power members.
                var rig = rigControl.Rig1;
                var t = rig.GetType();
                foreach (string name in new[] { "Power", "TxPower", "Pwr" })
                {
                    var prop = t.GetProperty(name);
                    if (prop == null) continue;
                    object? raw = prop.GetValue(rig);
                    if (raw == null) continue;
                    int parsed = Convert.ToInt32(raw);
                    value = Math.Clamp(parsed, 0, 100);
                    _txPwrKnown = true;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Read TX power property failed: " + ex.Message);
            }

            return false;
        }

        private bool TryReadMeterNormalized(double defaultMax, out double normalized, params string[] candidatePropertyNames)
        {
            normalized = 0.0;
            if (rigControl?.Rig1 == null) return false;

            try
            {
                var rig = rigControl.Rig1;
                var type = rig.GetType();
                foreach (string name in candidatePropertyNames)
                {
                    var prop = type.GetProperty(name);
                    if (prop == null) continue;
                    object? raw = prop.GetValue(rig);
                    if (raw == null) continue;

                    double value = Convert.ToDouble(raw);
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                    if (value < 0) value = 0;

                    // Drivers often expose 0..100, 0..255, or already normalized.
                    if (value <= 1.0)
                    {
                        normalized = value;
                    }
                    else if (value <= 100.0)
                    {
                        normalized = value / 100.0;
                    }
                    else if (value <= 255.0)
                    {
                        normalized = value / 255.0;
                    }
                    else
                    {
                        normalized = value / defaultMax;
                    }

                    normalized = Math.Clamp(normalized, 0.0, 1.0);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Read meter property failed: " + ex.Message);
            }

            return false;
        }

        private void SetMeterFill(Border? bar, double value01, double maxWidth)
        {
            if (bar == null) return;
            double v = Math.Max(0.0, Math.Min(1.0, value01));
            bar.Width = maxWidth * v;
        }

        private void SetMeterFillSteps(Border? bar, int squaresOn, int squaresTotal, double maxWidth)
        {
            if (bar == null || squaresTotal <= 0) return;
            int on = Math.Clamp(squaresOn, 0, squaresTotal);
            bar.Width = maxWidth * on / squaresTotal;
        }

        private void InitializeSharedMeterSegments()
        {
            // S-meter/PWR: tall narrow bars.
            BuildArcSegments(sharedMeterCanvas, _sharedMeterSegments, SharedMeterSegmentCount, 2.8, 9.6, 0.45, 12.0, 19);
            // SWR: rounded "square-ish" segments.
            BuildArcSegments(swrMeterCanvas, _swrMeterSegments, AuxMeterSegmentCount, 4.1, 6.2, 2.0, MeterPad, int.MaxValue);
            // ALC: tall bars, slightly wider than S-meter.
            BuildArcSegments(alcMeterCanvas, _alcMeterSegments, AuxMeterSegmentCount, 3.0, 8.8, 0.7, MeterPad, int.MaxValue);
        }

        private void InitializeMeterLegends()
        {
            BuildSegmentLegend(
                smeterLegendCanvas,
                SharedMeterSegmentCount,
                new (int Segment, string Label)[]
                {
                    (2, "1"), (6, "3"), (10, "5"), (14, "7"), (18, "9"),
                    (22, "+20"), (26, "+40"), (30, "+60"), (33, "dB")
                },
                Color.FromRgb(0x18, 0x08, 0x00),
                12.0,
                true,
                true,
                true,
                19,
                0.34);

            BuildSegmentLegend(
                pwrLegendCanvas,
                SharedMeterSegmentCount,
                new (int Segment, string Label)[]
                {
                    (4, "10"),     // between S1(2) and S3(6)
                    (10, "25"),    // below S5
                    (20, "50"),    // between S9(18) and +20(22)
                    (30, "100W")   // below +60 / 60dB mark
                },
                Color.FromRgb(0x18, 0x08, 0x00),
                12.0,
                true,
                false,
                false,
                int.MaxValue,
                1.0);

            BuildSegmentLegend(
                swrLegendCanvas,
                AuxMeterSegmentCount,
                new (int Segment, string Label)[]
                {
                    (1, "1"), (10, "1.5"), (16, "3"), (20, "∞")
                },
                Color.FromRgb(0x18, 0x08, 0x00),
                MeterPad,
                true,
                true,
                false,
                int.MaxValue,
                1.0);
        }

        private static void BuildSegmentLegend(
            Canvas canvas,
            int segmentCount,
            (int Segment, string Label)[] labels,
            Color color,
            double sidePad,
            bool followCurve,
            bool aboveArc,
            bool drawSeparators,
            int largeFromSegment,
            double smallSpan)
        {
            canvas.Children.Clear();
            double left = sidePad;
            double right = Math.Max(left + 20.0, canvas.Width - sidePad);
            var stamp = new List<(double T, double X)>(labels.Length);
            foreach (var (segment, label) in labels)
            {
                int s = Math.Clamp(segment, 1, segmentCount);
                double t = GetSegmentArcT(s, segmentCount, largeFromSegment, smallSpan);
                double x = left + (right - left) * t;
                stamp.Add((t, x));

                double width = Math.Max(8, label.Length * 4.2 + 2);
                var tb = new TextBlock
                {
                    Text = label,
                    Width = width,
                    TextAlignment = TextAlignment.Center,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 6.8,
                    Foreground = new SolidColorBrush(color)
                };
                double minX = MeterPad;
                double maxX = Math.Max(minX, canvas.Width - width);
                double lx = Math.Clamp(x - width / 2.0, minX, maxX);
                Canvas.SetLeft(tb, lx);
                if (!followCurve)
                {
                    Canvas.SetTop(tb, 0);
                }
                else
                {
                    double u = 2.0 * t - 1.0;
                    double yCurve = (1.0 - u * u); // 0..1
                    const double textCurveAmpTop = 2.4;
                    const double textCurveAmpBottom = 2.2;
                    double y = aboveArc
                        ? 2.8 - textCurveAmpTop * yCurve
                        : 1.7 + textCurveAmpBottom * yCurve;
                    Canvas.SetTop(tb, y);

                    // Real curved look: rotate each label with the parabola tangent.
                    double amp = aboveArc ? textCurveAmpTop : textCurveAmpBottom;
                    double dxdt = Math.Max(1.0, right - left);
                    double dydt = 4.0 * amp * u;
                    double slope = dydt / dxdt;
                    double angDeg = Math.Atan(slope) * 180.0 / Math.PI;
                    tb.RenderTransformOrigin = new Point(0.5, 0.5);
                    tb.RenderTransform = new RotateTransform(angDeg);
                }
                canvas.Children.Add(tb);
            }

            if (!drawSeparators || stamp.Count < 2)
            {
                return;
            }

            for (int i = 0; i < stamp.Count - 1; i++)
            {
                double tMid = (stamp[i].T + stamp[i + 1].T) * 0.5;
                double xMid = (stamp[i].X + stamp[i + 1].X) * 0.5;
                double gapSegments = Math.Abs(labels[i + 1].Segment - labels[i].Segment);

                // Fine horizontal separators; longer in wider gaps (e.g. 9--20--40--60).
                double sepWidth = gapSegments >= 4 ? 5.4 : 3.4;
                double sepHeight = 0.75;
                double sepOpacity = 0.9;

                var sep = new Border
                {
                    Width = sepWidth,
                    Height = sepHeight,
                    CornerRadius = new CornerRadius(0.4),
                    Background = new SolidColorBrush(color),
                    Opacity = sepOpacity
                };

                double sepY;
                if (!followCurve)
                {
                    sepY = aboveArc ? 2.1 : 4.0;
                }
                else
                {
                    double u = 2.0 * tMid - 1.0;
                    double yCurve = (1.0 - u * u);
                    double yBase = aboveArc
                        ? 3.5 - 2.6 * yCurve
                        : 4.2 + 2.2 * yCurve;
                    sepY = yBase - sepHeight * 0.5;
                }

                Canvas.SetLeft(sep, xMid - sepWidth / 2.0);
                Canvas.SetTop(sep, sepY);
                canvas.Children.Add(sep);
            }
        }

        private static double GetSegmentArcT(int segmentNumber, int count, int largeFromSegment, double smallSpan)
        {
            int s = Math.Clamp(segmentNumber, 1, count);
            if (largeFromSegment > 1 && largeFromSegment <= count)
            {
                int smallCount = largeFromSegment - 1;
                int largeCount = count - smallCount;
                double span = Math.Clamp(smallSpan, 0.1, 0.9);
                if (s < largeFromSegment)
                {
                    return smallCount <= 1
                        ? 0.0
                        : ((s - 1) / (double)(smallCount - 1)) * span;
                }

                int j = s - largeFromSegment;
                return span + (largeCount <= 1
                    ? 0.0
                    : (j / (double)(largeCount - 1)) * (1.0 - span));
            }

            return count == 1 ? 0.0 : (s - 1) / (double)(count - 1);
        }

        private static void BuildArcSegments(Canvas canvas, List<Border> target, int count, double segWidth, double segHeight, double corner, double sidePad, int largeFromSegment)
        {
            target.Clear();
            canvas.Children.Clear();

            double w = canvas.Width;
            double h = canvas.Height;
            double left = sidePad;
            double right = Math.Max(left + 20.0, w - sidePad);
            double yBase = h - 2.4;
            double depth = Math.Max(5.0, h * 0.62); // much more curvature

            for (int i = 0; i < count; i++)
            {
                int segNumber = i + 1; // 1-based as seen in the radio scale.
                bool isLarge = segNumber >= largeFromSegment;
                double width = isLarge ? segWidth * 2.0 : segWidth;
                double height = isLarge ? segHeight * 2.0 : segHeight;

                double t = GetSegmentArcT(segNumber, count, largeFromSegment, 0.34);

                double x = left + (right - left) * t;
                double u = 2.0 * t - 1.0;
                double yBottom = yBase - depth * (1.0 - u * u);
                double slope = 2.0 * depth * u / ((right - left) / 2.0);
                double angDeg = Math.Atan(slope) * 180.0 / Math.PI;

                var seg = new Border
                {
                    Width = width,
                    Height = height,
                    CornerRadius = new CornerRadius(corner),
                    Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0x20)),
                    Opacity = MeterGhostOpacity,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(angDeg)
                };
                Canvas.SetLeft(seg, x - seg.Width / 2.0);
                Canvas.SetTop(seg, yBottom - seg.Height);
                canvas.Children.Add(seg);
                target.Add(seg);
            }
        }

        private static void SetMeterSquares(List<Border> segments, int onCount, Color activeColor)
        {
            if (segments.Count == 0) return;
            int on = Math.Clamp(onCount, 0, segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                bool active = i < on;
                segments[i].Background = active
                    ? new SolidColorBrush(activeColor)
                    : new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0x20));
                segments[i].Opacity = active ? 1.0 : MeterGhostOpacity;
            }
        }

        // TS-570 SM values are 0..15. Shared arc uses 33 squares with fixed anchors:
        // S1=2, S3=6, S5=10, S7=14, S9=18, +20=22, +40=26, +60=30, dB end=33.
        private static int SmRawToSquares(int smRaw)
        {
            int r = Math.Clamp(smRaw, 0, 15);
            return r switch
            {
                0 => 0,
                1 => 2,
                2 => 4,
                3 => 6,
                4 => 8,
                5 => 10,
                6 => 12,
                7 => 14,
                8 => 16,
                9 => 18,
                10 => 22,
                11 => 24,
                12 => 26,
                13 => 28,
                14 => 30,
                _ => 33
            };
        }

        private void UpdateDisplayMeters()
        {
            double sm = 0, pwr = 0, swr = 0, alc = 0;
            bool hasSm, hasPwr, hasSwr, hasAlc;

            if (_hasCatSm) { hasSm = true; sm = _catSmN; }
            else
                hasSm = TryReadMeterNormalized(100.0, out sm, "SMeter", "Smeter", "SignalStrength", "Strength", "SLevel");

            if (_hasCatPwr) { hasPwr = true; pwr = _catPwrN; }
            else
                hasPwr = TryReadMeterNormalized(100.0, out pwr, "Power", "TxPower", "Pwr", "PowerOut");

            if (_hasCatSwr) { hasSwr = true; swr = _catSwrN; }
            else
                hasSwr = TryReadMeterNormalized(10.0, out swr, "SWR", "Swr");

            if (_hasCatAlc) { hasAlc = true; alc = _catAlcN; }
            else
                hasAlc = TryReadMeterNormalized(100.0, out alc, "ALC", "Alc");

            const int sharedSquares = SharedMeterSegmentCount;
            const int auxSquares = AuxMeterSegmentCount;

            if (_isTx)
            {
                txtSmeter.Text = hasAlc ? $"ALC {Math.Clamp((int)Math.Round(alc * 100), 0, 100):000}" : "ALC ---";
                SetMeterSquares(_sharedMeterSegments, (int)Math.Round((hasPwr ? pwr : 0.0) * sharedSquares), Color.FromRgb(0x36, 0x24, 0x00));
                SetMeterSquares(_swrMeterSegments, (int)Math.Round((hasSwr ? swr : 0.0) * auxSquares), Color.FromRgb(0x2D, 0x22, 0x00));
                SetMeterSquares(_alcMeterSegments, (int)Math.Round((hasAlc ? alc : 0.0) * auxSquares), Color.FromRgb(0x8A, 0x4C, 0x00));
            }
            else
            {
                int smSquares = 0;
                if (hasSm)
                    smSquares = _catSmRaw >= 0 ? SmRawToSquares(_catSmRaw) : (int)Math.Round(sm * sharedSquares);

                string sLabel = "S ---";
                if (hasSm)
                {
                    if (smSquares <= 18)
                    {
                        int sUnits = (int)Math.Round(smSquares / 2.0, MidpointRounding.AwayFromZero);
                        if (sUnits > 9) sUnits = 9;
                        sLabel = $"S {sUnits:0}";
                    }
                    else
                    {
                        if (smSquares <= 24) sLabel = "S9+20";
                        else if (smSquares <= 28) sLabel = "S9+40";
                        else if (smSquares <= 32) sLabel = "S9+60";
                        else sLabel = "dB";
                    }
                }
                txtSmeter.Text = sLabel;

                SetMeterSquares(_sharedMeterSegments, smSquares, Color.FromRgb(0x36, 0x24, 0x00));
                SetMeterSquares(_swrMeterSegments, (int)Math.Round((hasSwr ? swr : 0.0) * auxSquares), Color.FromRgb(0x2D, 0x22, 0x00));
                SetMeterSquares(_alcMeterSegments, (int)Math.Round((hasAlc ? alc : 0.0) * auxSquares), Color.FromRgb(0x8A, 0x4C, 0x00));
            }
        }

        private (int Min, int Max) GetTxPowerLimitsByMode()
        {
            // TS-570 manual ranges:
            // SSB/CW/FSK/FM: 5..100 W
            // AM:            5..25  W
            if (rigControl?.Rig1 != null)
            {
                try
                {
                    return rigControl.Rig1.Mode == OmniRig.RigParamX.PM_AM ? (5, 25) : (5, 100);
                }
                catch
                {
                    // Fall back to wide range if mode cannot be read.
                }
            }
            return (5, 100);
        }

        private int NormalizeTxPower(int watts)
        {
            var lim = GetTxPowerLimitsByMode();
            int snapped = (int)Math.Round(watts / 5.0) * 5;
            if (snapped < lim.Min) snapped = lim.Min;
            if (snapped > lim.Max) snapped = lim.Max;
            return snapped;
        }

        private static CatCommandMap CreateDefaultCommandMap()
        {
            // Control-to-manual intent defaults for phase 1 (left function panel)
            // PF: programmable function (default mapped to voice memory trigger)
            // POWER: power control (default mapped to power-off CAT command)
            // ATT/PRE-AMP/VOX/PROC: strict CAT toggles (on/off)
            // SEND: TX/RX transition
            // AT TUNE: tuner in-line + tune sequence
            return new CatCommandMap
            {
                Namespaces = new List<string>
                {
                    "Left",
                    "DSP",
                    "VFORight",
                    "Keypad",
                    "DualKnobs"
                },
                Actions = new Dictionary<string, CatActionMap>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Left.PF"] = new CatActionMap { OneShotCommand = "VR1;" },
                    ["Left.Power"] = new CatActionMap
                    {
                        OneShotCommand = "PS0;",
                        RequireConfirmation = true,
                        ConfirmationMessage = "Power off the transceiver?"
                    },
                    ["Left.ATT"] = new CatActionMap { OnCommand = "RA01;", OffCommand = "RA00;" },
                    ["Left.PreAmp"] = new CatActionMap { OnCommand = "PA1;", OffCommand = "PA0;" },
                    ["Left.VOX"] = new CatActionMap { OnCommand = "VX1;", OffCommand = "VX0;" },
                    ["Left.PROC"] = new CatActionMap { OnCommand = "PR1;", OffCommand = "PR0;" },
                    ["Left.SEND"] = new CatActionMap { OnCommand = "TX;", OffCommand = "RX;" },
                    ["Left.ATTune"] = new CatActionMap { OnCommand = "AC011;", OffCommand = "AC000;" }
                }
            };
        }

        private void LoadCommandMap()
        {
            if (!File.Exists(_mapFilePath))
            {
                txtStatusRight.Text = "  CAT map not found; using defaults";
                return;
            }

            try
            {
                string json = File.ReadAllText(_mapFilePath);
                CatCommandMap? loaded = JsonSerializer.Deserialize<CatCommandMap>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (loaded is null || loaded.Actions.Count == 0)
                {
                    txtStatusRight.Text = "  CAT map invalid; using defaults";
                    return;
                }

                _catMap = loaded;
                txtStatusRight.Text = $"  CAT map loaded ({_catMap.Actions.Count} actions)";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CAT map load error: " + ex.Message);
                txtStatusRight.Text = "  CAT map load failed; using defaults";
            }
        }

        private CatActionMap? TryGetMapAction(string key)
        {
            if (_catMap.Actions.TryGetValue(key, out CatActionMap? action))
                return action;

            if (_missingMapWarnings.Add(key))
                txtStatusRight.Text = $"  Missing CAT mapping: {key}";
            return null;
        }

        private void ValidateRequiredLeftPanelMappings()
        {
            string[] requiredKeys =
            {
                "Left.PF",
                "Left.Power",
                "Left.ATT",
                "Left.PreAmp",
                "Left.VOX",
                "Left.PROC",
                "Left.SEND",
                "Left.ATTune"
            };

            List<string> missing = new();
            foreach (string key in requiredKeys)
            {
                if (!_catMap.Actions.ContainsKey(key))
                    missing.Add(key);
            }

            if (missing.Count > 0)
                txtStatusRight.Text = $"  Missing map keys: {string.Join(", ", missing)}";
        }

        private static string PickCommand(string? primary, string fallback)
            => string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();

        private bool TryExecuteCat(string cmd, out string error)
        {
            error = "";
            if (rigControl?.Rig1 == null)
            {
                error = "Rig is not connected.";
                return false;
            }

            try
            {
                rigControl.Rig1.SendCustomCommand(cmd, 0, "");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"CAT({cmd}): {ex.Message}");
                return false;
            }
        }

        private bool ExecuteMappedCommand(string actionKey, string command, string successLabel)
        {
            if (TryExecuteCat(command, out string error))
            {
                txtStatusRight.Text = $"  {successLabel}";
                return true;
            }

            txtStatusRight.Text = $"  {actionKey} failed: {error}";
            return false;
        }

        private bool ExecuteMappedOneShot(string actionKey, string fallbackCommand, string successLabel)
        {
            CatActionMap action = TryGetMapAction(actionKey) ?? new CatActionMap();
            string command = PickCommand(action.OneShotCommand, fallbackCommand);
            if (string.IsNullOrWhiteSpace(command))
                return false;
            return ExecuteMappedCommand(actionKey, command, successLabel);
        }

        private bool ExecuteMappedToggle(
            string actionKey,
            bool desiredState,
            string fallbackOnCommand,
            string fallbackOffCommand,
            string onLabel,
            string offLabel)
        {
            CatActionMap action = TryGetMapAction(actionKey) ?? new CatActionMap();
            string command = desiredState
                ? PickCommand(action.OnCommand, fallbackOnCommand)
                : PickCommand(action.OffCommand, fallbackOffCommand);
            return ExecuteMappedCommand(actionKey, command, desiredState ? onLabel : offLabel);
        }

        private bool ExecuteMappedStep(string actionKey, bool stepUp, string fallbackStepUp, string fallbackStepDown, string successLabel)
        {
            CatActionMap action = TryGetMapAction(actionKey) ?? new CatActionMap();
            string command = stepUp
                ? PickCommand(action.StepUpCommand, fallbackStepUp)
                : PickCommand(action.StepDownCommand, fallbackStepDown);
            if (string.IsNullOrWhiteSpace(command))
                return true;
            return ExecuteMappedCommand(actionKey, command, successLabel);
        }

        private static string BuildMappedValueCommand(string template, int value)
        {
            int clamped = ClampKnobValue(value);
            int signed = clamped - 50;
            int value255 = (int)Math.Round(clamped * 255.0 / 100.0);
            return template
                .Replace("{value}", clamped.ToString())
                .Replace("{value2}", clamped.ToString("D2"))
                .Replace("{value3}", clamped.ToString("D3"))
                .Replace("{value4}", clamped.ToString("D4"))
                .Replace("{value255}", value255.ToString("D3"))
                .Replace("{signed3}", signed.ToString("+000;-000;000"))
                .Replace("{signed4}", signed.ToString("+0000;-0000;0000"));
        }

        private bool ExecuteMappedSetValue(string actionKey, int value, string fallbackTemplate, string successLabel)
        {
            CatActionMap action = TryGetMapAction(actionKey) ?? new CatActionMap();
            string template = string.IsNullOrWhiteSpace(action.SetValueCommand) ? fallbackTemplate : action.SetValueCommand!;
            if (string.IsNullOrWhiteSpace(template))
                return true;

            string command = BuildMappedValueCommand(template, value);
            return ExecuteMappedCommand(actionKey, command, successLabel);
        }

        private static bool HasToggleCommands(CatActionMap? action)
            => action is not null
               && !string.IsNullOrWhiteSpace(action.OnCommand)
               && !string.IsNullOrWhiteSpace(action.OffCommand);

        private bool ShouldConfirm(CatActionMap action, string fallbackMessage)
        {
            if (!action.RequireConfirmation) return true;
            string message = string.IsNullOrWhiteSpace(action.ConfirmationMessage)
                ? fallbackMessage
                : action.ConfirmationMessage!;
            return MessageBox.Show(
                message,
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        // ══════════════════════════════════════════════════════════════════════
        // UPDATE LCD BADGES  ← UI state core
        //
        // Each badge reflects ONE radio/application state.
        // Called from ALL button handlers and from the polling timer.
        // ══════════════════════════════════════════════════════════════════════
        private void UpdateLcdBadges()
        {
            OmniRig.RigParamX modeNow = OmniRig.RigParamX.PM_UNKNOWN;
            if (rigControl?.Rig1 != null)
            {
                try { modeNow = rigControl.Rig1.Mode; } catch { }
            }

            Show(badgeTx,      _isTx);
            Show(badgeRx,      !_isTx);
            Show(badgeAt,      _atOn);
            badgeAnt.Visibility = Visibility.Visible;     // always visible
            txtAntBadge.Text = "ANT";
            Show(badgeAnt1,    false);
            Show(badgeAnt2,    false);

            Show(badgeAtt,     _attOn);
            Show(badgePreAmp,  _preAmpOn);
            Show(badgeVox,     _voxOn);
            Show(badgeProc,    _procOn);
            Show(badgeNB,      _nbOn);
            Show(badgeSplit,   _splitOn);
            Show(badgeRit,     _ritOn);
            Show(badgeXit,     _xitOn);
            Show(badgeFast,    _agcFast);
            Show(badgeMenu,    false);
            Show(badgeMch,     _memMode);
            Show(badgeMScr,    false);
            Show(badgeVfoA,    !_memMode && _vfoSel == 0);
            Show(badgeVfoB,    !_memMode && _vfoSel == 1);
            Show(badgeVfoM,    _memMode);
            Show(badgeFine,    _fineOn);
            Show(badgeFLock,   _fLockOn);
            Show(badge1MHz,    _step1MHz);
            Show(badgeBC,      _bcOn);
            Show(badgeTxPwr,   true);
            txtPwrBadge.Text = "TX EQ.";
            Show(badgeLsb,     modeNow == OmniRig.RigParamX.PM_SSB_L);
            Show(badgeUsb,     modeNow == OmniRig.RigParamX.PM_SSB_U);
            Show(badgeCw,      modeNow == OmniRig.RigParamX.PM_CW_L || modeNow == OmniRig.RigParamX.PM_CW_U);
            Show(badgeR,       false);
            Show(badgeFsk,     modeNow == OmniRig.RigParamX.PM_DIG_L || modeNow == OmniRig.RigParamX.PM_DIG_U);
            Show(badgeFm,      modeNow == OmniRig.RigParamX.PM_FM);
            Show(badgeAm,      modeNow == OmniRig.RigParamX.PM_AM);
            Show(badgeT,       false);
            Show(badgeCtcss,   false);

            // NR shows textual value
            Show(badgeNR, _nrState > 0);
            txtNRBadge.Text = "N.R.";
            Show(badgeNR1, false);
            Show(badgeNR2, false);

            // VFO label
            txtVfoLabel.Text = _memMode
                ? " M"
                : (_vfoSel == 0 ? " <A" : " <B");
        }

        private static void Show(Border b, bool visible)
        {
            // LCD ghosting: indicators are always printed on the panel.
            // "Off" means dimmed, not hidden.
            b.Visibility = Visibility.Visible;
            b.Opacity = visible ? 1.0 : (20.0 / 255.0);
        }

        // ══════════════════════════════════════════════════════════════════════
        // STYLE HELPERS
        // ══════════════════════════════════════════════════════════════════════
        private void SetActive(Button btn, bool active)
        {
            // Keep each control's original shape/style and only tint text.
            // This avoids regressions where active state forced legacy square styles.
            btn.Foreground = active
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x92, 0x00))
                : new SolidColorBrush(Color.FromRgb(0xD2, 0xD2, 0xD8));

            // TX parameter mini buttons have no inner text, so tinting Foreground
            // alone is not visible enough; add a subtle pressed/active glow.
            if (btn == btnMic || btn == btnPwr || btn == btnKey || btn == btnDelay)
            {
                btn.Effect = active
                    ? new DropShadowEffect
                    {
                        Color = Color.FromRgb(0xFF, 0x92, 0x00),
                        BlurRadius = 10,
                        ShadowDepth = 0,
                        Opacity = 0.9
                    }
                    : null;
            }
        }

        private void UpdateTuneStepLabel()
        {
            int s = TuneSteps[_tuneStepIndex];
            txtTuneStep.Text = s >= 1_000 ? $"{s / 1_000} kHz" : $"{s} Hz";
        }

        // ══════════════════════════════════════════════════════════════════════
        // FORMAT AND MODE
        // ══════════════════════════════════════════════════════════════════════

        private static string FormatFrequency(int hz)
        {
            int mhz    = hz / 1_000_000;
            int khz    = (hz / 1_000) % 1_000;
            int subKhz = (hz / 100) % 10 * 10 + (hz / 10) % 10;
            return $"{mhz}.{khz:D3}.{subKhz:D2}";
        }

        private void UpdateFrequencyCanvasOffsets(int hz)
        {
            // Align right edge with ghost template for <10 MHz,
            // and switch to full-left alignment when >=10 MHz.
            double left = hz >= 10_000_000 ? -9 : 31;
            Canvas.SetLeft(txtFrequency, left);
            // Keep mode (USB/LSB/…) in a fixed position; the VFO only shifts the frequency digits.
        }

        private static string GetModeName(OmniRig.RigParamX mode) => mode switch
        {
            OmniRig.RigParamX.PM_SSB_L => "LSB",
            OmniRig.RigParamX.PM_SSB_U => "USB",
            OmniRig.RigParamX.PM_CW_L  => "CW",
            OmniRig.RigParamX.PM_CW_U  => "CW",
            OmniRig.RigParamX.PM_FM    => "FM",
            OmniRig.RigParamX.PM_AM    => "AM",
            OmniRig.RigParamX.PM_DIG_L => "FSK",
            OmniRig.RigParamX.PM_DIG_U => "FSK",
            _                          => "---"
        };

        private void UpdateModeButtonStyles(OmniRig.RigParamX mode)
        {
            bool lsbUsb = mode is OmniRig.RigParamX.PM_SSB_L or OmniRig.RigParamX.PM_SSB_U;
            bool cwFsk  = mode is OmniRig.RigParamX.PM_CW_L or OmniRig.RigParamX.PM_CW_U
                               or OmniRig.RigParamX.PM_DIG_L or OmniRig.RigParamX.PM_DIG_U;
            bool fmAm   = mode is OmniRig.RigParamX.PM_FM or OmniRig.RigParamX.PM_AM;
            SetActive(btnLsbUsb, lsbUsb);
            SetActive(btnCwFsk,  cwFsk);
            SetActive(btnFmAm,   fmAm);
        }

        // ══════════════════════════════════════════════════════════════════════
        // TITLE BAR
        // ══════════════════════════════════════════════════════════════════════
        private void btnSynchronize_Click(object sender, RoutedEventArgs e)
        {
            if (rigControl?.Rig1 == null) return;
            try
            {
                txtFrequency.Text = FormatFrequency(rigControl.Rig1.Freq);
                txtMode.Text      = GetModeName(rigControl.Rig1.Mode);
                UpdateModeButtonStyles(rigControl.Rig1.Mode);
            }
            catch (Exception ex) { MessageBox.Show("Sync: " + ex.Message); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // LEFT FUNCTION BUTTONS (8 buttons)
        // ══════════════════════════════════════════════════════════════════════
        private void btnPf_Click(object sender, RoutedEventArgs e)
        {
            CatActionMap action = TryGetMapAction("Left.PF") ?? new CatActionMap();
            string command = PickCommand(action.OneShotCommand, "VR1;");
            _ = ExecuteMappedCommand("Left.PF", command, "PF command sent");
        }

        private void btnPower_Click(object sender, RoutedEventArgs e)
        {
            CatActionMap action = TryGetMapAction("Left.Power") ?? new CatActionMap
            {
                RequireConfirmation = true,
                ConfirmationMessage = "Power off the transceiver?"
            };

            if (!ShouldConfirm(action, "Power off the transceiver?"))
                return;

            string command = PickCommand(action.OneShotCommand, "PS0;");
            _ = ExecuteMappedCommand("Left.Power", command, "Power command sent");
        }

        private void btnAtt_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_attOn;
            CatActionMap action = TryGetMapAction("Left.ATT") ?? new CatActionMap();
            string command = desired
                ? PickCommand(action.OnCommand, "RA01;")
                : PickCommand(action.OffCommand, "RA00;");
            if (!ExecuteMappedCommand("Left.ATT", command, $"ATT {(desired ? "ON" : "OFF")}"))
                return;
            _attOn = desired;
            SetActive(btnAtt, desired);
            UpdateLcdBadges();
        }

        private void btnPreAmp_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_preAmpOn;
            CatActionMap action = TryGetMapAction("Left.PreAmp") ?? new CatActionMap();
            string command = desired
                ? PickCommand(action.OnCommand, "PA1;")
                : PickCommand(action.OffCommand, "PA0;");
            if (!ExecuteMappedCommand("Left.PreAmp", command, $"PRE-AMP {(desired ? "ON" : "OFF")}"))
                return;
            _preAmpOn = desired;
            SetActive(btnPreAmp, desired);
            UpdateLcdBadges();
        }

        private void btnVox_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_voxOn;
            CatActionMap action = TryGetMapAction("Left.VOX") ?? new CatActionMap();
            string command = desired
                ? PickCommand(action.OnCommand, "VX1;")
                : PickCommand(action.OffCommand, "VX0;");
            if (!ExecuteMappedCommand("Left.VOX", command, $"VOX {(desired ? "ON" : "OFF")}"))
                return;
            _voxOn = desired;
            SetActive(btnVox, desired);
            UpdateLcdBadges();
        }

        private void btnProc_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_procOn;
            CatActionMap action = TryGetMapAction("Left.PROC") ?? new CatActionMap();
            string command = desired
                ? PickCommand(action.OnCommand, "PR1;")
                : PickCommand(action.OffCommand, "PR0;");
            if (!ExecuteMappedCommand("Left.PROC", command, $"PROC {(desired ? "ON" : "OFF")}"))
                return;
            _procOn = desired;
            SetActive(btnProc, desired);
            UpdateLcdBadges();
        }

        private void btnSend_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_isTx;
            CatActionMap action = TryGetMapAction("Left.SEND") ?? new CatActionMap();
            string command = desired
                ? PickCommand(action.OnCommand, "TX;")
                : PickCommand(action.OffCommand, "RX;");
            if (!ExecuteMappedCommand("Left.SEND", command, desired ? "TX enabled" : "RX enabled"))
                return;
            _isTx = desired;
            UpdateLcdBadges();
        }

        private void btnAtTune_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_atOn;
            CatActionMap action = TryGetMapAction("Left.ATTune") ?? new CatActionMap();
            // AC: P2=in-line, P3=1 starts tuning
            string command = desired
                ? PickCommand(action.OnCommand, "AC011;")
                : PickCommand(action.OffCommand, "AC000;");
            if (!ExecuteMappedCommand("Left.ATTune", command, desired ? "AT TUNE enabled" : "AT TUNE disabled"))
                return;
            _atOn = desired;
            SetActive(btnAtTune, desired);
            UpdateLcdBadges();
        }

        // ══════════════════════════════════════════════════════════════════════
        // DSP BUTTONS (top-right corner of LCD)
        // ══════════════════════════════════════════════════════════════════════
        private void btnNR_Click(object sender, RoutedEventArgs e)
        {
            int desired = (_nrState + 1) % 3;
            if (!ExecuteMappedCommand("DSP.NR", $"NR{desired};", $"NR mode {desired}"))
                return;
            _nrState = desired;
            btnNR.Content = _nrState switch { 0 => "N.R", 1 => "N.R.1", _ => "N.R.2" };
            SetActive(btnNR, _nrState != 0);
            UpdateLcdBadges();
        }

        private void btnBC_Click(object sender, RoutedEventArgs e)
        {
            _bcOn = !_bcOn;
            if (!ExecuteMappedToggle("DSP.BC", _bcOn, "BC1;", "BC0;", "B.C. ON", "B.C. OFF"))
            {
                _bcOn = !_bcOn;
                return;
            }
            SetActive(btnBC, _bcOn);
            UpdateLcdBadges();
        }

        private void btnCwTune_Click(object sender, RoutedEventArgs e)
            => ExecuteMappedOneShot("DSP.CWTune", "CA1;", "CW TUNE command sent");

        private void btnFilter_Click(object sender, RoutedEventArgs e)
        {
            // Cycle through common SSB/CW widths
            int[] bws   = { 2400, 1800, 600, 300, 250 };
            string[] lbl = { "FILTER", "FIL1800", "FIL 600", "FIL 300", "FIL 250" };
            // Index stored in Tag
            int idx = (btnFilter.Tag is int t ? t + 1 : 1) % bws.Length;
            btnFilter.Tag     = idx;
            btnFilter.Content = lbl[idx];
            if (!ExecuteMappedOneShot("DSP.Filter", $"FW{bws[idx]:D4};", $"FILTER {bws[idx]} Hz"))
                return;
            SetActive(btnFilter, idx != 0);
        }

        // ══════════════════════════════════════════════════════════════════════
        // TX PARAM (MIC / PWR / KEY / DELAY)
        // ══════════════════════════════════════════════════════════════════════
        private void SelectTxParam(TxParam p)
        {
            _activeTxParam = (_activeTxParam == p) ? TxParam.None : p;
            SetActive(btnMic,   _activeTxParam == TxParam.Mic);
            SetActive(btnPwr,   _activeTxParam == TxParam.Pwr);
            SetActive(btnKey,   _activeTxParam == TxParam.Key);
            SetActive(btnDelay, _activeTxParam == TxParam.Delay);
            txtStatusRight.Text = _activeTxParam == TxParam.None
                ? "  TX param: none"
                : $"  TX param: {_activeTxParam}";
            UpdateLcdBadges();
        }
        private void btnMic_Click(object s, RoutedEventArgs e)   => SelectTxParam(TxParam.Mic);
        private void btnPwr_Click(object s, RoutedEventArgs e)
        {
            SelectTxParam(TxParam.Pwr);
            if (_activeTxParam == TxParam.Pwr)
            {
                bool readOk = TryReadTxPower(out int pwr);
                if (readOk)
                {
                    _txPwrValue = pwr;
                    _txPwrKnown = true;
                }
                if (!_txPwrKnown)
                {
                    txtStatusRight.Text = "  TX PWR mode: waiting rig value...";
                    UpdateLcdBadges();
                    return;
                }
                _txPwrValue = NormalizeTxPower(_txPwrValue);
                _multiChValue = _txPwrValue;
                var lim = GetTxPowerLimitsByMode();
                txtStatusRight.Text = $"  TX PWR mode: {_txPwrValue:000}W ({lim.Min}-{lim.Max}W), use MULTI CH";
                UpdateLcdBadges();
            }
        }
        private void btnKey_Click(object s, RoutedEventArgs e)   => SelectTxParam(TxParam.Key);
        private void btnDelay_Click(object s, RoutedEventArgs e) => SelectTxParam(TxParam.Delay);

        // ══════════════════════════════════════════════════════════════════════
        // MODE BUTTONS
        // ══════════════════════════════════════════════════════════════════════
        private void btnLsbUsb_Click(object sender, RoutedEventArgs e)
        {
            if (rigControl?.Rig1 == null) return;
            bool desiredUsb = rigControl.Rig1.Mode != OmniRig.RigParamX.PM_SSB_U;
            CatActionMap action = TryGetMapAction("Mode.LsbUsb") ?? new CatActionMap();
            if (HasToggleCommands(action))
            {
                _ = ExecuteMappedToggle("Mode.LsbUsb", desiredUsb, action.OnCommand!, action.OffCommand!, "USB mode", "LSB mode");
                return;
            }
            rigControl.Rig1.Mode = desiredUsb ? OmniRig.RigParamX.PM_SSB_U : OmniRig.RigParamX.PM_SSB_L;
        }
        private void btnCwFsk_Click(object sender, RoutedEventArgs e)
        {
            if (rigControl?.Rig1 == null) return;
            bool isCw = rigControl.Rig1.Mode is OmniRig.RigParamX.PM_CW_L or OmniRig.RigParamX.PM_CW_U;
            bool desiredFsk = isCw;
            CatActionMap action = TryGetMapAction("Mode.CwFsk") ?? new CatActionMap();
            if (HasToggleCommands(action))
            {
                _ = ExecuteMappedToggle("Mode.CwFsk", desiredFsk, action.OnCommand!, action.OffCommand!, "FSK mode", "CW mode");
                return;
            }
            rigControl.Rig1.Mode = desiredFsk ? OmniRig.RigParamX.PM_DIG_U : OmniRig.RigParamX.PM_CW_U;
        }
        private void btnFmAm_Click(object sender, RoutedEventArgs e)
        {
            if (rigControl?.Rig1 == null) return;
            bool desiredAm = rigControl.Rig1.Mode == OmniRig.RigParamX.PM_FM;
            CatActionMap action = TryGetMapAction("Mode.FmAm") ?? new CatActionMap();
            if (HasToggleCommands(action))
            {
                _ = ExecuteMappedToggle("Mode.FmAm", desiredAm, action.OnCommand!, action.OffCommand!, "AM mode", "FM mode");
                return;
            }
            rigControl.Rig1.Mode = desiredAm ? OmniRig.RigParamX.PM_AM : OmniRig.RigParamX.PM_FM;
        }

        private void btnMenu_Click(object sender, RoutedEventArgs e)
        {
            CatActionMap action = TryGetMapAction("Mode.Menu") ?? new CatActionMap();
            if (!string.IsNullOrWhiteSpace(action.OneShotCommand))
            {
                _ = ExecuteMappedOneShot("Mode.Menu", "", "MENU command sent");
                return;
            }

            MessageBox.Show(
                "TS-570D menu via CAT: EX###XXXX;\n" +
                "Example: read item 01 -> EX001;",
                "MENU", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void btn1MHz_Click(object sender, RoutedEventArgs e)
        {
            _step1MHz = !_step1MHz;
            CatActionMap action = TryGetMapAction("Mode.1MHz") ?? new CatActionMap();
            if (HasToggleCommands(action))
                _ = ExecuteMappedToggle("Mode.1MHz", _step1MHz, action.OnCommand!, action.OffCommand!, "1MHz ON", "1MHz OFF");
            _tuneStepIndex = _step1MHz ? 7 : 3;
            UpdateTuneStepLabel();
            SetActive(btn1MHz, _step1MHz);
            UpdateLcdBadges();
        }

        // ══════════════════════════════════════════════════════════════════════
        // VFO – FREQUENCY AND STEP
        // ══════════════════════════════════════════════════════════════════════
        private void btnFreqDown_Click(object s, RoutedEventArgs e)
        {
            _ = ChangeBand(-1);
        }
        private void btnFreqUp_Click(object s, RoutedEventArgs e)
        {
            _ = ChangeBand(+1);
        }
        private void btnStepDown_Click(object s, RoutedEventArgs e)
        {
            if (_tuneStepIndex > 0) _tuneStepIndex--;
            UpdateTuneStepLabel();
        }
        private void btnStepUp_Click(object s, RoutedEventArgs e)
        {
            if (_tuneStepIndex < TuneSteps.Length - 1) _tuneStepIndex++;
            UpdateTuneStepLabel();
        }

        // ══════════════════════════════════════════════════════════════════════
        // RIGHT-SIDE VFO BUTTONS
        // ══════════════════════════════════════════════════════════════════════
        private void btnSplit_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_splitOn;
            CatActionMap action = TryGetMapAction("VFORight.Split") ?? new CatActionMap();
            if (HasToggleCommands(action))
            {
                if (!ExecuteMappedToggle("VFORight.Split", desired, action.OnCommand!, action.OffCommand!, "SPLIT ON", "SPLIT OFF"))
                    return;
            }
            else if (rigControl?.Rig1 != null)
            {
                rigControl.Rig1.Split = desired
                    ? OmniRig.RigParamX.PM_SPLITON
                    : OmniRig.RigParamX.PM_SPLITOFF;
            }
            _splitOn = desired;
            SetActive(btnSplit, desired);
            UpdateLcdBadges();
        }

        private void btnTfSet_Click(object sender, RoutedEventArgs e)
        {
            // Listen on TX frequency
            _ = ExecuteMappedOneShot("VFORight.TFSet", "FR1;", "TF-SET command sent");
        }

        private void btnAB_Click(object sender, RoutedEventArgs e)
        {
            _vfoSel = 1 - _vfoSel;
            bool isA = _vfoSel == 0;
            if (!ExecuteMappedToggle("VFORight.AB", isA, "FR0;", "FR1;", "VFO A selected", "VFO B selected"))
            {
                _vfoSel = 1 - _vfoSel;
                return;
            }
            UpdateLcdBadges();
        }

        private void btnRit_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_ritOn;
            CatActionMap action = TryGetMapAction("VFORight.Rit") ?? new CatActionMap();
            if (HasToggleCommands(action))
            {
                if (!ExecuteMappedToggle("VFORight.Rit", desired, action.OnCommand!, action.OffCommand!, "RIT ON", "RIT OFF"))
                    return;
            }
            else if (rigControl?.Rig1 != null)
            {
                rigControl.Rig1.Mode = desired
                    ? OmniRig.RigParamX.PM_RITON
                    : OmniRig.RigParamX.PM_RITOFF;
            }
            _ritOn = desired;
            SetActive(btnRit, desired);
            UpdateLcdBadges();
        }

        private void btnMV_Click(object sender, RoutedEventArgs e)
        {
            _memMode = !_memMode;
            _ = ExecuteMappedToggle("VFORight.MV", _memMode, "MC1;", "MC0;", "Memory mode ON", "Memory mode OFF");
            SetActive(btnMV, _memMode);
            UpdateLcdBadges();
        }

        private void btnAeqB_Click(object sender, RoutedEventArgs e)
        {
            if (rigControl?.Rig1 == null) return;
            rigControl.Rig1.FreqB = rigControl.Rig1.Freq;
            _ = ExecuteMappedOneShot("VFORight.AeqB", "AB;", "A = B copied");
        }

        private void btnCLS_Click(object sender, RoutedEventArgs e)
        {
            if (rigControl?.Rig1 != null) rigControl.Rig1.RitOffset = 0;
            _ = ExecuteMappedOneShot("VFORight.CLS", "RC;", "RIT/XIT cleared");
            txtRitBadge.Text = "RIT";
        }

        private void btnXit_Click(object sender, RoutedEventArgs e)
        {
            bool desired = !_xitOn;
            CatActionMap action = TryGetMapAction("VFORight.Xit") ?? new CatActionMap();
            if (HasToggleCommands(action))
            {
                if (!ExecuteMappedToggle("VFORight.Xit", desired, action.OnCommand!, action.OffCommand!, "XIT ON", "XIT OFF"))
                    return;
            }
            else if (rigControl?.Rig1 != null)
            {
                rigControl.Rig1.Mode = desired
                    ? OmniRig.RigParamX.PM_XITON
                    : OmniRig.RigParamX.PM_XITOFF;
            }
            _xitOn = desired;
            SetActive(btnXit, desired);
            UpdateLcdBadges();
        }

        private void btnScan_Click(object sender, RoutedEventArgs e)
        {
            _scanOn = !_scanOn;
            if (!ExecuteMappedToggle("VFORight.Scan", _scanOn, "SC1;", "SC0;", "SCAN ON", "SCAN OFF"))
            {
                _scanOn = !_scanOn;
                return;
            }
            SetActive(btnScan, _scanOn);
        }

        private void btnMVfo_Click(object sender, RoutedEventArgs e)
            => ExecuteMappedOneShot("VFORight.MVfo", "MC00;", "M>VFO command sent");

        private void btnMIn_Click(object sender, RoutedEventArgs e)
        {
            if (rigControl?.Rig1 == null) return;
            int freq = rigControl.Rig1.Freq;
            int mode = (int)rigControl.Rig1.Mode;
            _ = ExecuteMappedOneShot("VFORight.MIn", $"MW0000{freq:D11}{mode}000;", "Memory stored to 00");
        }

        // ══════════════════════════════════════════════════════════════════════
        // QUICK MEMO
        // ══════════════════════════════════════════════════════════════════════
        private void btnQmMR_Click(object s, RoutedEventArgs e)
        {
            _ = ExecuteMappedOneShot("QuickMemo.MR", $"PB{_qmCh};", $"QM Recall ch {_qmCh}");
            txtStatusRight.Text = $"  QM Recall ch {_qmCh}";
        }
        private void btnQmPlus_Click(object s, RoutedEventArgs e)
        {
            _qmCh = _qmCh % 5 + 1;
            txtStatusRight.Text = $"  QM channel {_qmCh}";
        }
        private void btnQmMIn_Click(object s, RoutedEventArgs e)
        {
            _ = ExecuteMappedOneShot("QuickMemo.MIn", "LM1;", "QM stored");
            txtStatusRight.Text = "  QM stored";
        }
        private void btnQmMinus_Click(object s, RoutedEventArgs e)
        {
            _qmCh = _qmCh > 1 ? _qmCh - 1 : 5;
            txtStatusRight.Text = $"  QM channel {_qmCh}";
        }

        // ══════════════════════════════════════════════════════════════════════
        // TECLADO NUMÉRICO
        // ══════════════════════════════════════════════════════════════════════
        private void btnCH1_Click(object s, RoutedEventArgs e) => SendCAT("PB1;");
        private void btnCH2_Click(object s, RoutedEventArgs e) => SendCAT("PB2;");
        private void btnCH3_Click(object s, RoutedEventArgs e) => SendCAT("PB3;");

        private void btnKey1_Click(object s, RoutedEventArgs e)
            => SendCAT("PB1;");

        private void btnKey2_Click(object s, RoutedEventArgs e)
            => SendCAT("PB2;");

        private void btnKey3_Click(object s, RoutedEventArgs e)
            => SendCAT("PB3;");

        private void btnKey4_Click(object s, RoutedEventArgs e)
        {
            _antSel = _antSel == 1 ? 2 : 1;
            SendCAT($"AN{_antSel};");
            UpdateLcdBadges();
        }

        private void btnKey5_Click(object s, RoutedEventArgs e) => SendCAT("LM1;");

        private void btnKey6_Click(object s, RoutedEventArgs e)
        {
            _fineOn = !_fineOn;
            SendCAT(_fineOn ? "FS1;" : "FS0;");
            SetActive(btnFine, _fineOn);
            UpdateLcdBadges();
        }

        private void btnKey7_Click(object s, RoutedEventArgs e)
        {
            _nbOn = !_nbOn;
            SendCAT(_nbOn ? "NB1;" : "NB0;");
            SetActive(btnNB, _nbOn);
            UpdateLcdBadges();
        }

        private void btnKey8_Click(object s, RoutedEventArgs e)
        {
            _agcFast = !_agcFast;
            SendCAT(_agcFast ? "GT002;" : "GT004;");
            SetActive(btnAGC, _agcFast);
            UpdateLcdBadges();
        }

        private void btnKey9_Click(object s, RoutedEventArgs e)
            => SendCAT("FR0;"); // REV - returns to lower band

        private void btnKey0_Click(object s, RoutedEventArgs e)
        {
            _fLockOn = !_fLockOn;
            SendCAT(_fLockOn ? "LK1;" : "LK0;");
            UpdateLcdBadges();
        }

        private void btnKeypad_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string digit)
            {
                if (!_freqEntry)
                {
                    _freqEntry = true;
                    _freqBuffer = "";
                }
                if (_freqBuffer.Length < 8)
                {
                    _freqBuffer += digit;
                    // Show progress with ghost-format dots; pattern MM.KKK.cc
                    string padded = _freqBuffer.PadRight(9, '-');
                    txtFrequency.Text = $"{padded[0..2]}.{padded[2..5]}.{padded[5..7]}";
                }
            }
        }

        private void btnKeyCLR_Click(object s, RoutedEventArgs e)
        {
            _freqEntry = false;
            _freqBuffer = "";
            if (rigControl?.Rig1 != null)
                txtFrequency.Text = FormatFrequency(rigControl.Rig1.Freq);
        }

        private void btnKeyENT_Click(object s, RoutedEventArgs e)
        {
            if (!_freqEntry || _freqBuffer.Length < 3) return;
            string padded = _freqBuffer.PadRight(8, '0');
            if (int.TryParse(padded, out int raw))
            {
                // If < 60000 interpret as kHz, otherwise as x100 Hz units
                int hz = raw < 60_000 ? raw * 1_000 : raw * 100;
                if (rigControl?.Rig1 != null) rigControl.Rig1.Freq = hz;
            }
            _freqEntry = false;
            _freqBuffer = "";
        }

        // ══════════════════════════════════════════════════════════════════════
        // KNOBS (wheel + click-drag, independent inner/outer rotation)
        // ══════════════════════════════════════════════════════════════════════
        private static double NormalizeAngle(double angle)
        {
            angle %= 360;
            return angle < 0 ? angle + 360 : angle;
        }

        private static int ClampKnobValue(int value) => Math.Max(0, Math.Min(100, value));
        private static readonly int[] HamBandCentersHz =
        {
            1_810_000, 3_500_000, 7_000_000, 10_100_000, 14_000_000,
            18_068_000, 21_000_000, 24_890_000, 28_000_000, 50_000_000
        };

        private static double AngleFromSteps(int steps) => steps * 2.2;

        private static int WheelSteps(MouseWheelEventArgs e) => e.Delta > 0 ? 2 : -2;

        private void AdjustVfoFrequency(int deltaHz, string sourceLabel)
        {
            int baseHz = _localVfoHz;
            if (rigControl?.Rig1 != null)
            {
                try { baseHz = rigControl.Rig1.Freq; }
                catch { baseHz = _localVfoHz; }
            }

            int newHz = Math.Max(30_000, baseHz + deltaHz);
            _localVfoHz = newHz;

            if (rigControl?.Rig1 != null)
            {
                try
                {
                    rigControl.Rig1.Freq = newHz;
                    // Extra explicit CAT write for rigs/interfaces that ignore property-set intermittently.
                    SendCAT($"FA{newHz:D11};");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("VFO tune write failed: " + ex.Message);
                }
            }

            _manualFreqOverrideUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            if (!_freqEntry)
                txtFrequency.Text = FormatFrequency(newHz);

            string sign = deltaHz > 0 ? "+" : "";
            txtStatusRight.Text = $"  {sourceLabel}: {sign}{deltaHz} Hz";
        }

        private bool ChangeBand(int direction)
        {
            int currentHz = _localVfoHz;
            if (rigControl?.Rig1 != null)
            {
                try { currentHz = rigControl.Rig1.Freq; }
                catch { currentHz = _localVfoHz; }
            }

            int currentIndex = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < HamBandCentersHz.Length; i++)
            {
                int d = Math.Abs(HamBandCentersHz[i] - currentHz);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    currentIndex = i;
                }
            }

            int nextIndex = Math.Clamp(currentIndex + direction, 0, HamBandCentersHz.Length - 1);
            if (nextIndex == currentIndex)
            {
                txtStatusRight.Text = $"  Band limit: {HamBandCentersHz[currentIndex] / 1_000_000.0:0.000} MHz";
                return true;
            }

            int newHz = HamBandCentersHz[nextIndex];
            _localVfoHz = newHz;
            _manualFreqOverrideUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);

            if (rigControl?.Rig1 != null)
            {
                try { rigControl.Rig1.Freq = newHz; } catch { }
            }

            SendCAT($"FA{newHz:D11};");

            if (!_freqEntry)
                txtFrequency.Text = FormatFrequency(newHz);

            txtStatusRight.Text = $"  Band: {newHz / 1_000_000.0:0.000} MHz";
            return true;
        }

        private int ComputeDragSteps(MouseEventArgs e, UIElement element)
        {
            Point current = e.GetPosition(element);
            double dy = _lastKnobPoint.Y - current.Y;
            _lastKnobPoint = current;
            return (int)Math.Round(dy / 2.0);
        }

        private void BeginKnobDrag(MouseButtonEventArgs e, UIElement element)
        {
            _isDraggingKnob = true;
            _lastKnobPoint = e.GetPosition(element);
            element.CaptureMouse();
            e.Handled = true;
        }

        private void EndKnobDrag(MouseButtonEventArgs e, UIElement element)
        {
            _isDraggingKnob = false;
            element.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void AfRfOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int step = WheelSteps(e);
            _rfValue = ClampKnobValue(_rfValue + step);
            _afRfOuterAngle = NormalizeAngle(_afRfOuterAngle + AngleFromSteps(step));
            rtAfRfOuter.Angle = _afRfOuterAngle;
            txtStatusRight.Text = $"  AF:{_afValue:000} RF:{_rfValue:000}";
            e.Handled = true;
        }

        private void AfRfInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int step = WheelSteps(e);
            _afValue = ClampKnobValue(_afValue + step);
            _afRfInnerAngle = NormalizeAngle(_afRfInnerAngle + AngleFromSteps(step));
            rtAfRfInner.Angle = _afRfInnerAngle;
            txtStatusRight.Text = $"  AF:{_afValue:000} RF:{_rfValue:000}";
            e.Handled = true;
        }

        private void IfSqlOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int step = WheelSteps(e);
            _ifShiftValue = ClampKnobValue(_ifShiftValue + step);
            _ifSqlOuterAngle = NormalizeAngle(_ifSqlOuterAngle + AngleFromSteps(step));
            rtIfSqlOuter.Angle = _ifSqlOuterAngle;
            int shiftHz = (int)Math.Round((_ifShiftValue - 50) * 1100.0 / 50.0);
            string sign = shiftHz >= 0 ? "+" : "-";
            string isCmd = $"IS{sign}{Math.Abs(shiftHz):D4};";
            _ = ExecuteMappedCommand("Knob.IfShift", isCmd, $"IF SHIFT {shiftHz:+0000;-0000;0000} Hz");
            txtStatusRight.Text = $"  IF:{shiftHz:+0000;-0000;0000}Hz SQL:{_sqlValue:000}";
            e.Handled = true;
        }

        private void IfSqlInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int step = WheelSteps(e);
            _sqlValue = ClampKnobValue(_sqlValue + step);
            _ifSqlInnerAngle = NormalizeAngle(_ifSqlInnerAngle + AngleFromSteps(step));
            rtIfSqlInner.Angle = _ifSqlInnerAngle;
            _ = ExecuteMappedSetValue("Knob.Sql", _sqlValue, "SQ{value255};", $"SQL {_sqlValue}");
            txtStatusRight.Text = $"  IF:{_ifShiftValue:000} SQL:{_sqlValue:000}";
            e.Handled = true;
        }

        private void DspSlopeOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int step = WheelSteps(e);
            _dspHighValue = ClampKnobValue(_dspHighValue + step);
            _dspSlopeOuterAngle = NormalizeAngle(_dspSlopeOuterAngle + AngleFromSteps(step));
            rtDspSlopeOuter.Angle = _dspSlopeOuterAngle;
            int shParam = 20 - (int)Math.Round(_dspHighValue * 20.0 / 100.0);
            _ = ExecuteMappedCommand("Knob.DspHigh", $"SH{shParam:D2};", $"DSP HIGH {shParam:D2}");
            txtStatusRight.Text = $"  DSP H:{_dspHighValue:000} L:{_dspLowValue:000}";
            e.Handled = true;
        }

        private void DspSlopeInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int step = WheelSteps(e);
            _dspLowValue = ClampKnobValue(_dspLowValue + step);
            _dspSlopeInnerAngle = NormalizeAngle(_dspSlopeInnerAngle + AngleFromSteps(step));
            rtDspSlopeInner.Angle = _dspSlopeInnerAngle;
            int slParam = (int)Math.Round(_dspLowValue * 20.0 / 100.0);
            _ = ExecuteMappedCommand("Knob.DspLow", $"SL{slParam:D2};", $"DSP LOW {slParam:D2}");
            txtStatusRight.Text = $"  DSP H:{_dspHighValue:000} L:{_dspLowValue:000}";
            e.Handled = true;
        }

        private void PhonesKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _phonesValue = ClampKnobValue(_phonesValue + WheelSteps(e));
            _ = ExecuteMappedSetValue("Knob.Phones", _phonesValue, "", $"PHONES {_phonesValue}");
            txtStatusRight.Text = $"  PHONES: {_phonesValue:000}";
            e.Handled = true;
        }

        private void PhonesKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void PhonesKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void PhonesKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _phonesValue = ClampKnobValue(_phonesValue + steps);
            _ = ExecuteMappedSetValue("Knob.Phones", _phonesValue, "", $"PHONES {_phonesValue}");
            txtStatusRight.Text = $"  PHONES: {_phonesValue:000}";
        }

        private void MicGainKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _micGainValue = ClampKnobValue(_micGainValue + WheelSteps(e));
            _ = ExecuteMappedSetValue("Knob.MicGain", _micGainValue, "MG{value255};", $"MIC GAIN {_micGainValue}");
            txtStatusRight.Text = $"  MIC GAIN: {_micGainValue:000}";
            e.Handled = true;
        }

        private void MicGainKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void MicGainKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void MicGainKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _micGainValue = ClampKnobValue(_micGainValue + steps);
            _ = ExecuteMappedSetValue("Knob.MicGain", _micGainValue, "MG{value255};", $"MIC GAIN {_micGainValue}");
            txtStatusRight.Text = $"  MIC GAIN: {_micGainValue:000}";
        }

        private bool AdjustActiveTxParam(int deltaStep)
        {
            if (_activeTxParam == TxParam.None) return false;

            int delta = deltaStep * 2;
            string actionKey;
            string fallbackTemplate;
            string label;
            int value;

            switch (_activeTxParam)
            {
                case TxParam.Mic:
                    _txMicValue = ClampKnobValue(_txMicValue + delta);
                    value = _txMicValue;
                    actionKey = "TxParam.Mic";
                    fallbackTemplate = "MG{value255};";
                    label = "MIC";
                    break;
                case TxParam.Pwr:
                    _txPwrValue = ClampKnobValue(_txPwrValue + delta);
                    value = _txPwrValue;
                    actionKey = "TxParam.Pwr";
                    fallbackTemplate = "PC{value3};";
                    label = "PWR";
                    break;
                case TxParam.Key:
                    _txKeyValue = ClampKnobValue(_txKeyValue + delta);
                    value = _txKeyValue;
                    actionKey = "TxParam.Key";
                    // KS uses WPM-like value range 010..060.
                    int ks = 10 + (int)Math.Round(value * 50.0 / 100.0);
                    _ = ExecuteMappedCommand(actionKey, $"KS{ks:D3};", $"KEY SPEED {ks:000}");
                    txtStatusRight.Text = $"  TX KEY: {ks:000}";
                    return true;
                case TxParam.Delay:
                    _txDelayValue = ClampKnobValue(_txDelayValue + delta);
                    value = _txDelayValue;
                    actionKey = "TxParam.Delay";
                    int sd = (int)Math.Round(value * 100.0 / 100.0);
                    _ = ExecuteMappedCommand(actionKey, $"SD{sd:D3};", $"SEMI BREAK-IN {sd:000}");
                    txtStatusRight.Text = $"  TX DELAY: {sd:000}";
                    return true;
                default:
                    return false;
            }

            _ = ExecuteMappedSetValue(actionKey, value, fallbackTemplate, $"{label} {value}");
            txtStatusRight.Text = $"  TX {label}: {value:000}";
            return true;
        }

        private void MultiChKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int detent = e.Delta > 0 ? 1 : -1;
            _multiChValue += detent;
            if (_multiChValue < 1) _multiChValue = 1;
            if (_multiChValue > 99) _multiChValue = 99;
            _multiChAngle = NormalizeAngle(_multiChAngle + detent * 8);
            rtMultiCh.Angle = _multiChAngle;
            if (_activeTxParam == TxParam.Pwr)
            {
                if (!_txPwrKnown) return;
                _txPwrValue = NormalizeTxPower(_txPwrValue + detent * 5);
                _multiChValue = _txPwrValue;
                _ = ExecuteMappedSetValue("TxParam.Pwr", _txPwrValue, "PC{value3};", $"PWR {_txPwrValue}");
                txtStatusRight.Text = $"  TX PWR: {_txPwrValue:000}W (step 5W)";
                UpdateLcdBadges();
            }
            else
            {
                _ = ExecuteMappedSetValue("Knob.MultiCh", _multiChValue, "", $"MULTI CH {_multiChValue}");
                AdjustVfoFrequency(detent * 10_000, "MULTI CH");
            }
            e.Handled = true;
        }

        private void RitXitKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _ritXitValue = ClampKnobValue(_ritXitValue + WheelSteps(e));
            _ritXitAngle = NormalizeAngle(_ritXitAngle + AngleFromSteps(WheelSteps(e)));
            rtRitXit.Angle = _ritXitAngle;
            txtStatusRight.Text = $"  RIT/XIT: {_ritXitValue:000}";
            e.Handled = true;
        }

        private void AfRfOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void AfRfOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void AfRfOuterKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _rfValue = ClampKnobValue(_rfValue + steps);
            _afRfOuterAngle = NormalizeAngle(_afRfOuterAngle + AngleFromSteps(steps));
            rtAfRfOuter.Angle = _afRfOuterAngle;
            _ = ExecuteMappedSetValue("Knob.Rf", _rfValue, "RG{value255};", $"RF {_rfValue}");
            txtStatusRight.Text = $"  AF:{_afValue:000} RF:{_rfValue:000}";
        }

        private void AfRfInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void AfRfInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void AfRfInnerKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _afValue = ClampKnobValue(_afValue + steps);
            _afRfInnerAngle = NormalizeAngle(_afRfInnerAngle + AngleFromSteps(steps));
            rtAfRfInner.Angle = _afRfInnerAngle;
            _ = ExecuteMappedSetValue("Knob.Af", _afValue, "AG{value255};", $"AF {_afValue}");
            txtStatusRight.Text = $"  AF:{_afValue:000} RF:{_rfValue:000}";
        }

        private void IfSqlOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void IfSqlOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void IfSqlOuterKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _ifShiftValue = ClampKnobValue(_ifShiftValue + steps);
            _ifSqlOuterAngle = NormalizeAngle(_ifSqlOuterAngle + AngleFromSteps(steps));
            rtIfSqlOuter.Angle = _ifSqlOuterAngle;
            int shiftHz = (int)Math.Round((_ifShiftValue - 50) * 1100.0 / 50.0);
            string sign = shiftHz >= 0 ? "+" : "-";
            _ = ExecuteMappedCommand("Knob.IfShift", $"IS{sign}{Math.Abs(shiftHz):D4};", $"IF SHIFT {shiftHz:+0000;-0000;0000} Hz");
            txtStatusRight.Text = $"  IF:{shiftHz:+0000;-0000;0000}Hz SQL:{_sqlValue:000}";
        }

        private void IfSqlInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void IfSqlInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void IfSqlInnerKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _sqlValue = ClampKnobValue(_sqlValue + steps);
            _ifSqlInnerAngle = NormalizeAngle(_ifSqlInnerAngle + AngleFromSteps(steps));
            rtIfSqlInner.Angle = _ifSqlInnerAngle;
            _ = ExecuteMappedSetValue("Knob.Sql", _sqlValue, "SQ{value255};", $"SQL {_sqlValue}");
            txtStatusRight.Text = $"  IF:{_ifShiftValue:000} SQL:{_sqlValue:000}";
        }

        private void DspSlopeOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void DspSlopeOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void DspSlopeOuterKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _dspHighValue = ClampKnobValue(_dspHighValue + steps);
            _dspSlopeOuterAngle = NormalizeAngle(_dspSlopeOuterAngle + AngleFromSteps(steps));
            rtDspSlopeOuter.Angle = _dspSlopeOuterAngle;
            int shParam = 20 - (int)Math.Round(_dspHighValue * 20.0 / 100.0);
            _ = ExecuteMappedCommand("Knob.DspHigh", $"SH{shParam:D2};", $"DSP HIGH {shParam:D2}");
            txtStatusRight.Text = $"  DSP H:{_dspHighValue:000} L:{_dspLowValue:000}";
        }

        private void DspSlopeInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void DspSlopeInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void DspSlopeInnerKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _dspLowValue = ClampKnobValue(_dspLowValue + steps);
            _dspSlopeInnerAngle = NormalizeAngle(_dspSlopeInnerAngle + AngleFromSteps(steps));
            rtDspSlopeInner.Angle = _dspSlopeInnerAngle;
            int slParam = (int)Math.Round(_dspLowValue * 20.0 / 100.0);
            _ = ExecuteMappedCommand("Knob.DspLow", $"SL{slParam:D2};", $"DSP LOW {slParam:D2}");
            txtStatusRight.Text = $"  DSP H:{_dspHighValue:000} L:{_dspLowValue:000}";
        }

        private void VfoMainKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int ticks = e.Delta > 0 ? 1 : -1;
            _vfoMainAngle = NormalizeAngle(_vfoMainAngle + ticks * 3.6);
            rtVfoMain.Angle = _vfoMainAngle;
            AdjustVfoFrequency(ticks * 10, "VFO");
            e.Handled = true;
        }

        private void VfoMainKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingVfo = true;
            _lastVfoPoint = e.GetPosition((IInputElement)sender);
            _vfoDragAccumulator = 0;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void VfoMainKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingVfo || e.LeftButton != MouseButtonState.Pressed) return;
            Point current = e.GetPosition((IInputElement)sender);
            double dy = _lastVfoPoint.Y - current.Y;
            _lastVfoPoint = current;
            _vfoDragAccumulator += dy;
            const double pixelsPerDetent = 12.0;
            while (_vfoDragAccumulator >= pixelsPerDetent)
            {
                _vfoDragAccumulator -= pixelsPerDetent;
                _vfoMainAngle = NormalizeAngle(_vfoMainAngle + 1.8);
                rtVfoMain.Angle = _vfoMainAngle;
                AdjustVfoFrequency(10, "VFO drag");
            }
            while (_vfoDragAccumulator <= -pixelsPerDetent)
            {
                _vfoDragAccumulator += pixelsPerDetent;
                _vfoMainAngle = NormalizeAngle(_vfoMainAngle - 1.8);
                rtVfoMain.Angle = _vfoMainAngle;
                AdjustVfoFrequency(-10, "VFO drag");
            }
        }

        private void VfoMainKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingVfo = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private void RitXitKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void RitXitKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void RitXitKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _ritXitValue = ClampKnobValue(_ritXitValue + steps);
            _ritXitAngle = NormalizeAngle(_ritXitAngle + AngleFromSteps(steps));
            rtRitXit.Angle = _ritXitAngle;
            txtStatusRight.Text = $"  RIT/XIT: {_ritXitValue:000}";
        }

        private void MultiChKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void MultiChKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void MultiChKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;
            _multiChValue += steps;
            if (_multiChValue < 1) _multiChValue = 1;
            if (_multiChValue > 99) _multiChValue = 99;
            _multiChAngle = NormalizeAngle(_multiChAngle + AngleFromSteps(steps));
            rtMultiCh.Angle = _multiChAngle;
            if (_activeTxParam == TxParam.Pwr)
            {
                if (!_txPwrKnown) return;
                _txPwrValue = NormalizeTxPower(_txPwrValue + steps * 5);
                _multiChValue = _txPwrValue;
                _ = ExecuteMappedSetValue("TxParam.Pwr", _txPwrValue, "PC{value3};", $"PWR {_txPwrValue}");
                txtStatusRight.Text = $"  TX PWR: {_txPwrValue:000}W (step 5W)";
                UpdateLcdBadges();
            }
            else
            {
                AdjustVfoFrequency(steps * 10_000, "MULTI CH");
            }
        }
    }
}
