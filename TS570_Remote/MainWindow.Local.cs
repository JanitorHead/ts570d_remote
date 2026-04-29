using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private static readonly int[] TuneSteps = { 10, 100, 500, 1_000, 2_500, 5_000, 10_000, 100_000 };
        private static readonly Brush LcdActiveBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E4180800"));
        private static readonly Brush LcdGhostBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14180800"));

        private readonly RadioState _state = new();
        private readonly Ts570LocalCore _core;
        private readonly AppSettings _settings = AppSettings.Load();
        private Color _displayBaseColor = Color.FromRgb(255, 146, 0);
        private Acc2AudioBridge? _acc2Bridge;
        private int _tuneStepIndex = 0;

        private OmniRigCatClient? _cat;
        private readonly DispatcherTimer _catSyncTimer;
        private bool _catReady;
        private bool _catRigOnline;
        private int _catPollDivider;
        private int _catControlSyncDivider;
        private bool _catBootstrapDone;
        private int _catBootstrapTicks;
        private int _catBootstrapReadIndex;
        private int _txMeterPollPhase;
        private double _txPwrRatio;
        private double _txMidRatio;
        private double _txAlcRatio;
        private string _lastMeterDebugText = "SM: n/a";
        private readonly bool _meterDebugEnabled = false;

        private System.Windows.Shapes.Rectangle[]? _sharedMeterSegments;
        private System.Windows.Shapes.Rectangle[]? _swrMeterSegments;
        private System.Windows.Shapes.Rectangle[]? _alcMeterSegments;
        private System.Windows.Shapes.Rectangle[]? _smeterTickMarkers;

        // Last values sent to the radio (for throttling).
        private int _lastVfoAHz = int.MinValue;
        private int _lastVfoBHz = int.MinValue;
        private RadioMode _lastMode = (RadioMode)(-1);
        // Set to true so the first CAT tick pushes initial RX/flags to match UI state.
        private bool _lastIsTx = true;
        private bool _lastSplitOn = true;
        private bool _lastActiveVfo = true;
        private bool _lastAttOn = true;
        private bool _lastPreAmpOn = true;
        private bool _lastVoxOn = true;
        private bool _lastProcOn = true;
        private int _lastNrState = -1;
        private bool _lastBcOn = true;
        private bool _lastNbOn = true;
        private bool _lastFineOn = true;
        private int _lastAntSel = -1;
        private int _lastAfValue = -1;
        private int _lastRfValue = -1;
        private int _lastSqlValue = -1;
        private int _lastIfShiftValue = -1;
        private int _lastDspHighValue = -1;
        private int _lastDspLowValue = -1;
        private bool _lastRitOn = true;
        private bool _lastXitOn = true;
        private int _lastRitOffsetCentiKhz = int.MinValue;

        private int _lastTxMicValue = -1;
        private int _lastTxKeyValue = -1;
        private int _lastTxDelayValue = -1;
        private int _lastTxPwrValue = -1;
        private bool _lastAtTuneOn = true;

        // OmniRig enum constants (from OmniRig type library).
        private const int StOnline = 4;
        private const int PmSplitOn = 0x00008000;
        private const int PmSplitOff = 0x00010000;
        private const int PmRitOn = 0x00020000;
        private const int PmRitOff = 0x00040000;
        private const int PmXitOn = 0x00080000;
        private const int PmXitOff = 0x00100000;
        private const int PmRx = 0x00200000;
        private const int PmTx = 0x00400000;
        private const int PmCwU = 0x00800000;
        private const int PmCwL = 0x01000000;
        private const int PmSsbU = 0x02000000;
        private const int PmSsbL = 0x04000000;
        private const int PmDigU = 0x08000000;
        private const int PmDigL = 0x10000000;
        private const int PmAm = 0x20000000;
        private const int PmFm = 0x40000000;
        private const int PmVfoAA = 0x00000080;
        private const int PmVfoAB = 0x00000100;
        private const int PmVfoBA = 0x00000200;
        private const int PmVfoBB = 0x00000400;
        private const int PmVfoA = 0x00000800;
        private const int PmVfoB = 0x00001000;
        private const int MeterPwr = 0;
        private const int MeterSwr = 1;
        private const int MeterAlc = 2;
        private const int MeterComp = 3;

        private double _afRfOuterAngle;
        private double _afRfInnerAngle;
        private double _ifSqlOuterAngle;
        private double _ifSqlInnerAngle;
        private double _dspSlopeOuterAngle;
        private double _dspSlopeInnerAngle;
        private double _ritXitAngle;
        private double _multiChAngle;
        private double _vfoMainAngle;

        private bool _isDraggingKnob;
        private Point _lastKnobPoint;
        private double _knobDragAccumulator;
        private double _lastKnobAngle;

        private bool _isDraggingVfo;
        private Point _lastVfoPoint;
        private double _vfoDragAccumulator;
        private double _lastVfoAngle;

        private enum TxParam { None, Mic, Pwr, Key, Delay }
        private TxParam _activeTxParam = TxParam.None;

        private bool _freqEntry;
        private string _freqBuffer = "";

        private bool _isFilterAdjustMode;
        private readonly DispatcherTimer _scanTimer;
        private readonly Random _scanRandom = new();
        private int _scanHoldTicksRemaining;
        private DateTime _atTunePressedAtUtc;
        private bool _atTuneLongPressConsumed;
        private bool _tfSetMouseHoldActive;

        public MainWindow()
        {
            InitializeComponent();
            _core = new Ts570LocalCore(_state);
            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _scanTimer.Tick += ScanTimer_Tick;
            _catSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _catSyncTimer.Tick += CatSyncTimer_Tick;

            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            _displayBaseColor = ParseDisplayColor(_settings.DisplayColorHex);
            ApplyDisplayColorTheme(_displayBaseColor);

            ledCatGreen.Visibility = Visibility.Hidden;
            ledCatAmber.Visibility = Visibility.Visible;
            txtStatusLeft.Text = "Local: simulated panel · CAT/OmniRig pending · audio via USB/ACC2";

            TryInitCat();

            if (!string.IsNullOrEmpty(_settings.AudioTxPlaybackDeviceId)
                && WindowsPlaybackEndpointVolume.TryGetMasterVolumeScalar(_settings.AudioTxPlaybackDeviceId, out float micScalar))
            {
                _state.MicGainValue = (int)Math.Clamp(Math.Round(micScalar * 100.0), 0, 100);
            }

            // Requested startup defaults only when CAT is not available.
            // With CAT online, we first read rig state and then reflect it.
            if (!_catReady)
            {
                _state.AfValue = 0;
                _state.RfValue = 100;
            }

            ApplyUiFromState();
            txtStatusRight.Text = "  TS-570 local core ready";

            meterPhonesTrack.SizeChanged += (_, _) => UpdateGainMeters();
            meterMicTrack.SizeChanged += (_, _) => UpdateGainMeters();
            ContentRendered += (_, _) =>
            {
                UpdateGainMeters();
                EnsureMeterSegmentsInitialized();
                ClearRadioMeterBars();
            };

            Closing += (_, _) => _acc2Bridge?.Dispose();
        }

        private void TryInitCat()
        {
            try
            {
                Debug.WriteLine($"[CAT] Process Is64BitProcess={Environment.Is64BitProcess}");
                _cat = new OmniRigCatClient(rigNumber: 1);
                _catReady = true;
                _catRigOnline = false;
                _catBootstrapDone = false;
                _catBootstrapTicks = 0;
                _catBootstrapReadIndex = 0;
                _catSyncTimer.Start();
                ledCatGreen.Visibility = Visibility.Visible;
                ledCatAmber.Visibility = Visibility.Hidden;
                txtStatusLeft.Text = "Local: simulated panel · CAT/OmniRig connected · audio via USB/ACC2";
            }
            catch (Exception ex)
            {
                _catReady = false;
                _catRigOnline = false;
                _cat?.Dispose();
                _cat = null;
                ledCatGreen.Visibility = Visibility.Hidden;
                ledCatAmber.Visibility = Visibility.Visible;
                txtStatusLeft.Text = "Local: simulated panel · CAT/OmniRig error (see debug log) · audio via USB/ACC2";
                Debug.WriteLine($"OmniRig init failed: {ex.Message}");
            }
        }

        private void CatSyncTimer_Tick(object? sender, EventArgs e)
        {
            if (!_catReady || _cat is null)
                return;

            // Bootstrap: read rig values first, avoid writing defaults to radio.
            if (!_catBootstrapDone)
            {
                _catBootstrapTicks++;
                if (_cat.TryReadSnapshot(out var bootSnapshot))
                    ApplyRigSnapshot(bootSnapshot);
                BootstrapReadFromRig();

                // ~1.5s warm-up at 50 ms ticks.
                if (_catBootstrapTicks >= 30)
                {
                    _catBootstrapDone = true;
                    PrimeLastSentStateFromCurrent();
                    ApplyUiFromState();
                }
                return;
            }

            // Meter-first strategy:
            // - update meter bars on every fast tick (50 ms)
            // - run heavier control sync in background cadence
            UpdateRadioMetersFromCat();

            // Poll transceiver state less frequently to reduce CAT queue pressure.
            _catPollDivider = (_catPollDivider + 1) % 10;
            if (_catPollDivider == 0 && _cat.TryReadSnapshot(out var snapshot))
                ApplyRigSnapshot(snapshot);

            // Control sync cadence: 1 out of 4 ticks (~200 ms).
            _catControlSyncDivider = (_catControlSyncDivider + 1) % 4;
            if (_catControlSyncDivider != 0)
            {
                // Keep right display override stable while a TX param editor is active.
                if (_activeTxParam != TxParam.None)
                    UpdateRitDisplay();
                return;
            }

            // Frequency and mode: these are always safe to keep aligned.
            if (_state.VfoAHz != _lastVfoAHz)
            {
                _cat.SendCustomCommand($"FA{Format11Digits(_state.VfoAHz)};");
                _lastVfoAHz = _state.VfoAHz;
            }
            if (_state.VfoBHz != _lastVfoBHz)
            {
                _cat.SendCustomCommand($"FB{Format11Digits(_state.VfoBHz)};");
                _lastVfoBHz = _state.VfoBHz;
            }
            if (_state.Mode != _lastMode)
            {
                _cat.SendCustomCommand(GetModeCommand(_state.Mode));
                _lastMode = _state.Mode;
            }

            // Receiver / transmitter VFO selection (split handling).
            bool rxVfo = _state.ActiveVfo == 0; // A=0
            int rx = rxVfo ? 0 : 1;
            int tx = _state.SplitOn ? 1 - rx : rx;

            if (_state.SplitOn != _lastSplitOn || _state.ActiveVfo != (_lastActiveVfo ? 1 : 0))
            {
                _cat.SendCustomCommand($"FR{rx};FT{tx};");
                _lastSplitOn = _state.SplitOn;
                _lastActiveVfo = _state.ActiveVfo == 1;
            }

            // TX/RX.
            if (_state.IsTx != _lastIsTx)
            {
                _cat.SendCustomCommand(_state.IsTx ? "TX;" : "RX;");
                _lastIsTx = _state.IsTx;
            }

            // Basic receiver toggles.
            if (_state.AttOn != _lastAttOn)
            {
                _cat.SendCustomCommand(_state.AttOn ? "RA01;" : "RA00;");
                _lastAttOn = _state.AttOn;
            }
            if (_state.PreAmpOn != _lastPreAmpOn)
            {
                _cat.SendCustomCommand(_state.PreAmpOn ? "PA1;" : "PA0;");
                _lastPreAmpOn = _state.PreAmpOn;
            }
            if (_state.VoxOn != _lastVoxOn)
            {
                _cat.SendCustomCommand(_state.VoxOn ? "VX1;" : "VX0;");
                _lastVoxOn = _state.VoxOn;
            }
            if (_state.ProcOn != _lastProcOn)
            {
                _cat.SendCustomCommand(_state.ProcOn ? "PR1;" : "PR0;");
                _lastProcOn = _state.ProcOn;
            }
            if (_state.AtTuneOn != _lastAtTuneOn)
            {
                // CatCommandMap uses AC011/AC000.
                _cat.SendCustomCommand(_state.AtTuneOn ? "AC011;" : "AC000;");
                _lastAtTuneOn = _state.AtTuneOn;
            }

            // DSP-ish toggles we can map with existing codes.
            if (_state.NrState != _lastNrState)
            {
                _cat.SendCustomCommand($"NR{_state.NrState};");
                _lastNrState = _state.NrState;
            }
            if (_state.BcOn != _lastBcOn)
            {
                _cat.SendCustomCommand(_state.BcOn ? "BC1;" : "BC0;");
                _lastBcOn = _state.BcOn;
            }
            if (_state.NbOn != _lastNbOn)
            {
                _cat.SendCustomCommand(_state.NbOn ? "NB1;" : "NB0;");
                _lastNbOn = _state.NbOn;
            }
            if (_state.FineOn != _lastFineOn)
            {
                _cat.SendCustomCommand(_state.FineOn ? "FS1;" : "FS0;");
                _lastFineOn = _state.FineOn;
            }
            if (_state.AntSel != _lastAntSel)
            {
                _cat.SendCustomCommand(_state.AntSel == 2 ? "AN2;" : "AN1;");
                _lastAntSel = _state.AntSel;
            }

            if (_state.RitOn != _lastRitOn)
            {
                _cat.SendCustomCommand(_state.RitOn ? "RT1;" : "RT0;");
                _lastRitOn = _state.RitOn;
            }
            if (_state.XitOn != _lastXitOn)
            {
                _cat.SendCustomCommand(_state.XitOn ? "XT1;" : "XT0;");
                _lastXitOn = _state.XitOn;
            }

            if (_state.RitOffsetCentiKhz != _lastRitOffsetCentiKhz)
            {
                // RC resets offset to 0; apply RU/RD delta in 10 Hz steps.
                int oldOffset = _lastRitOffsetCentiKhz == int.MinValue ? 0 : _lastRitOffsetCentiKhz;
                int delta = _state.RitOffsetCentiKhz - oldOffset;

                if (oldOffset == 0)
                {
                    // no-op: direct relative movement from zero below
                }
                else if (Math.Abs(delta) > 120)
                {
                    // Large jumps are cheaper with clear+relative.
                    _cat.SendCustomCommand("RC;");
                    delta = _state.RitOffsetCentiKhz;
                }

                int maxStepPerTick = 24;
                int steps = Math.Clamp(Math.Abs(delta), 0, maxStepPerTick);
                string cmd = delta >= 0 ? "RU;" : "RD;";
                for (int i = 0; i < steps; i++)
                    _cat.SendCustomCommand(cmd);

                _lastRitOffsetCentiKhz = oldOffset + (delta >= 0 ? steps : -steps);
            }

            if (_state.AfValue != _lastAfValue)
            {
                _cat.SendCustomCommand($"AG{Scale0To255(_state.AfValue):000};");
                _lastAfValue = _state.AfValue;
            }
            if (_state.RfValue != _lastRfValue)
            {
                _cat.SendCustomCommand($"RG{Scale0To255(_state.RfValue):000};");
                _lastRfValue = _state.RfValue;
            }
            if (_state.SqlValue != _lastSqlValue)
            {
                _cat.SendCustomCommand($"SQ{Scale0To255(_state.SqlValue):000};");
                _lastSqlValue = _state.SqlValue;
            }
            if (_state.IfShiftValue != _lastIfShiftValue)
            {
                _cat.SendCustomCommand($"IS{ScaleIfShiftSigned4(_state.IfShiftValue)};");
                _lastIfShiftValue = _state.IfShiftValue;
            }
            if (_state.DspHighValue != _lastDspHighValue)
            {
                _cat.SendCustomCommand($"SH{Scale0To20(_state.DspHighValue):00};");
                _lastDspHighValue = _state.DspHighValue;
            }
            if (_state.DspLowValue != _lastDspLowValue)
            {
                _cat.SendCustomCommand($"SL{Scale0To20(_state.DspLowValue):00};");
                _lastDspLowValue = _state.DspLowValue;
            }

            // TX parameter path from MIC/PWR/KEY/DELAY selector + MULTI/CH.
            if (_state.TxMicValue != _lastTxMicValue)
            {
                _cat.SendCustomCommand($"MG{Scale0To255(_state.TxMicValue):000};");
                _lastTxMicValue = _state.TxMicValue;
            }
            if (_state.TxKeyValue != _lastTxKeyValue)
            {
                // KS: keying speed, roughly 004..060 WPM.
                _cat.SendCustomCommand($"KS{Scale0ToRange(_state.TxKeyValue, 4, 60):000};");
                _lastTxKeyValue = _state.TxKeyValue;
            }
            if (_state.TxDelayValue != _lastTxDelayValue)
            {
                // SD: 0..1000 ms, 50 ms steps.
                int ms = Scale0ToRange(_state.TxDelayValue, 0, 1000);
                ms = (int)Math.Round(ms / 50.0) * 50;
                _cat.SendCustomCommand($"SD{Math.Clamp(ms, 0, 1000):0000};");
                _lastTxDelayValue = _state.TxDelayValue;
            }

            // PWR knob.
            if (_state.TxPwrValue != _lastTxPwrValue)
            {
                // PC command expects 3 digits in examples (PC100;).
                int pwr = Math.Clamp(_state.TxPwrValue, 0, 999);
                _cat.SendCustomCommand($"PC{pwr:000};");
                _lastTxPwrValue = _state.TxPwrValue;
            }
        }

        private static string Format11Digits(int value)
        {
            // CAT expects 11 digits for FA/FB (e.g., 14,250,000 => 00014250000).
            return Math.Max(0, value).ToString("00000000000");
        }

        private static int Scale0To255(int value0to100)
            => Scale0ToRange(value0to100, 0, 255);

        private static int Scale0To20(int value0to100)
            => Scale0ToRange(value0to100, 0, 20);

        private static int Scale0ToRange(int value0to100, int min, int max)
        {
            int v = Math.Clamp(value0to100, 0, 100);
            return (int)Math.Round(min + ((max - min) * (v / 100.0)));
        }

        private static string ScaleIfShiftSigned4(int value0to100)
        {
            // Map center(50)=>0, range to about +/-1000 (IS-0300 style signed 4 digits).
            int v = Math.Clamp(value0to100, 0, 100);
            int signed = (int)Math.Round((v - 50) * 20.0);
            signed = Math.Clamp(signed, -9999, 9999);
            return $"{(signed >= 0 ? "+" : "-")}{Math.Abs(signed):0000}";
        }

        private static string GetModeCommand(RadioMode mode)
        {
            // Values taken from the generic Kenwood.ini you provided:
            // pmSSB_L => MD1, pmSSB_U => MD2, pmCW_U => MD3, pmFM => MD4, pmAM => MD5
            // pmCW_L => MD7, pmDIG_U => MD9, pmDIG_L => MD6.
            return mode switch
            {
                RadioMode.Lsb => "MD1;",
                RadioMode.Usb => "MD2;",
                RadioMode.Cw => "MD3;",
                RadioMode.Fsk => "MD9;",
                RadioMode.Fm => "MD4;",
                RadioMode.Am => "MD5;",
                _ => "MD2;"
            };
        }

        private static RadioMode GetReverseMode(RadioMode mode)
        {
            return mode switch
            {
                RadioMode.Usb => RadioMode.Lsb,
                RadioMode.Lsb => RadioMode.Usb,
                RadioMode.Cw => RadioMode.Cw,
                RadioMode.Fsk => RadioMode.Fsk,
                _ => mode
            };
        }

        private void ApplyRigSnapshot(OmniRigCatClient.RigSnapshot s)
        {
            _catRigOnline = s.Status == StOnline;
            if (_catRigOnline)
            {
                ledCatGreen.Visibility = Visibility.Visible;
                ledCatAmber.Visibility = Visibility.Hidden;
            }
            else
            {
                ledCatGreen.Visibility = Visibility.Hidden;
                ledCatAmber.Visibility = Visibility.Visible;
            }

            bool changed = false;

            if (s.FreqA > 0 && _state.VfoAHz != s.FreqA)
            {
                _state.VfoAHz = s.FreqA;
                changed = true;
            }
            if (s.FreqB > 0 && _state.VfoBHz != s.FreqB)
            {
                _state.VfoBHz = s.FreqB;
                changed = true;
            }

            RadioMode mode = s.Mode switch
            {
                PmSsbL => RadioMode.Lsb,
                PmSsbU => RadioMode.Usb,
                PmCwU or PmCwL => RadioMode.Cw,
                PmDigU or PmDigL => RadioMode.Fsk,
                PmFm => RadioMode.Fm,
                PmAm => RadioMode.Am,
                _ => _state.Mode
            };
            if (_state.Mode != mode)
            {
                _state.Mode = mode;
                changed = true;
            }

            int activeVfo = s.Vfo switch
            {
                PmVfoAA or PmVfoAB or PmVfoA => 0,
                PmVfoBA or PmVfoBB or PmVfoB => 1,
                _ => _state.ActiveVfo
            };
            if (_state.ActiveVfo != activeVfo)
            {
                _state.ActiveVfo = activeVfo;
                changed = true;
            }

            bool splitOn = s.Split switch
            {
                PmSplitOn => true,
                PmSplitOff => false,
                _ => (s.Vfo == PmVfoAB || s.Vfo == PmVfoBA)
            };
            if (_state.SplitOn != splitOn)
            {
                _state.SplitOn = splitOn;
                changed = true;
            }

            bool ritOn = s.Rit == PmRitOn;
            bool xitOn = s.Xit == PmXitOn;
            bool isTx = s.Tx == PmTx;

            if (_state.RitOn != ritOn) { _state.RitOn = ritOn; changed = true; }
            if (_state.XitOn != xitOn) { _state.XitOn = xitOn; changed = true; }
            if (_state.IsTx != isTx) { _state.IsTx = isTx; changed = true; }

            int centi = Math.Clamp((int)Math.Round(s.RitOffset / 10.0), -999, 999);
            if (_state.RitOffsetCentiKhz != centi)
            {
                _state.RitOffsetCentiKhz = centi;
                changed = true;
            }

            if (!changed)
                return;

            txtMode.Text = GetModeName(_state.Mode);
            UpdateModeButtonStyles(_state.Mode);
            UpdateFrequencyReadout();
            UpdateLcdBadges();
            UpdateRitDisplay();
            UpdateAuxDisplay();

            // Prevent immediate write-back churn on mirrored fields.
            _lastVfoAHz = _state.VfoAHz;
            _lastVfoBHz = _state.VfoBHz;
            _lastMode = _state.Mode;
            _lastSplitOn = _state.SplitOn;
            _lastActiveVfo = _state.ActiveVfo == 1;
            _lastRitOn = _state.RitOn;
            _lastXitOn = _state.XitOn;
            _lastIsTx = _state.IsTx;
            _lastRitOffsetCentiKhz = _state.RitOffsetCentiKhz;
        }

        private void MenuMonitorAcc2_Checked(object sender, RoutedEventArgs e)
        {
            TryStartAcc2Monitor();
        }

        private void MenuMonitorAcc2_Unchecked(object sender, RoutedEventArgs e)
        {
            StopAcc2Monitor();
        }

        private void TryStartAcc2Monitor()
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            string? cap = _settings.AudioCaptureDeviceId;
            string? play = _settings.AudioPlaybackDeviceId;
            if (string.IsNullOrEmpty(cap) || string.IsNullOrEmpty(play))
            {
                MessageBox.Show(
                    this,
                    "Set capture (USB / ACC2) and monitor playback under Settings → Audio (USB / ACC2) first.",
                    "Monitor ACC2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                menuMonitorAcc2.IsChecked = false;
                return;
            }

            try
            {
                _acc2Bridge ??= new Acc2AudioBridge();
                SyncPhonesMonitorGain();
                _acc2Bridge.Start(cap, play);
                SyncMicWindowsEndpointVolume();
                txtStatusRight.Text = "  Monitor ACC2: ON";
            }
            catch (Exception ex)
            {
                menuMonitorAcc2.IsChecked = false;
                MessageBox.Show(this,
                    "Could not open WASAPI capture/playback.\r\n\r\n" + ex.Message,
                    "Monitor ACC2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void StopAcc2Monitor()
        {
            _acc2Bridge?.Stop();
            txtStatusRight.Text = "  Monitor ACC2: OFF";
        }

        private void SyncPhonesMonitorGain()
        {
            _acc2Bridge?.SetPlaybackMonitorGain(_state.PhonesValue / 100f);
        }

        private void SyncMicWindowsEndpointVolume()
        {
            WindowsPlaybackEndpointVolume.SetMasterVolumeScalar(_settings.AudioTxPlaybackDeviceId, _state.MicGainValue / 100f);
        }

        private void MenuFileExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MenuSettingsAudio_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AudioSettingsWindow(_settings) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            txtStatusRight.Text = "  Audio USB/ACC2 saved";
            if (menuMonitorAcc2.IsChecked == true)
                TryStartAcc2Monitor();
        }

        private void MenuSettingsDisplayColor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DisplayColorWindow(_displayBaseColor) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            _displayBaseColor = dlg.SelectedColor;
            ApplyDisplayColorTheme(_displayBaseColor);
            _settings.DisplayColorHex = $"#{_displayBaseColor.R:X2}{_displayBaseColor.G:X2}{_displayBaseColor.B:X2}";
            _settings.Save();
            txtStatusRight.Text = $"  Display color saved {_settings.DisplayColorHex}";
        }

        private void MenuSettingsOpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TS570_Remote");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        private void MenuHelpAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                this,
                "TS570 Remote — local front panel; CAT/OmniRig drives radio commands.\n"
                + "Audio NF: USB interface(s) on ACC2 (not via OmniRig).\n\n"
                + $"Settings folder: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TS570_Remote")}",
                "About TS570 Remote",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private static Color ParseDisplayColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return Color.FromRgb(255, 146, 0);

            string value = hex.Trim();
            if (value.StartsWith("#"))
                value = value[1..];
            if (value.Length != 6)
                return Color.FromRgb(255, 146, 0);

            bool okR = byte.TryParse(value[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r);
            bool okG = byte.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g);
            bool okB = byte.TryParse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b);
            return okR && okG && okB ? Color.FromRgb(r, g, b) : Color.FromRgb(255, 146, 0);
        }

        private void ApplyDisplayColorTheme(Color baseColor)
        {
            gsLcdFillHigh.Color = Blend(baseColor, Colors.White, 0.22);
            gsLcdFillLow.Color = Blend(baseColor, Colors.White, 0.22);
            gsLcdFillTop.Color = Blend(baseColor, Colors.Black, 0.26);
            gsLcdFillBottom.Color = Blend(baseColor, Colors.Black, 0.26);
            gsLcdBezelTop.Color = Blend(baseColor, Colors.Black, 0.86);
            gsLcdBezelBottom.Color = Blend(baseColor, Colors.Black, 0.95);
            fxLcdGlow.Color = Blend(baseColor, Colors.Black, 0.16);
            Resources["LcdThemeOnBrush"] = new SolidColorBrush(Blend(baseColor, Colors.White, 0.10));
        }

        private static Color Blend(Color from, Color to, double toWeight)
        {
            toWeight = Math.Clamp(toWeight, 0.0, 1.0);
            double fromWeight = 1.0 - toWeight;
            byte r = (byte)Math.Round((from.R * fromWeight) + (to.R * toWeight));
            byte g = (byte)Math.Round((from.G * fromWeight) + (to.G * toWeight));
            byte b = (byte)Math.Round((from.B * fromWeight) + (to.B * toWeight));
            return Color.FromRgb(r, g, b);
        }

        private void UpdateGainMeters()
        {
            SetMeterFill(meterPhonesTrack, meterPhonesFill, _state.PhonesValue, 46);
            SetMeterFill(meterMicTrack, meterMicFill, _state.MicGainValue, 52);
        }

        private static void SetMeterFill(Border track, System.Windows.Shapes.Rectangle fill, int value0to100, double fallbackHeight)
        {
            double trackH = track.ActualHeight > 4 ? track.ActualHeight : fallbackHeight;
            fill.Height = trackH * Math.Clamp(value0to100, 0, 100) / 100.0;
        }

        private void ApplyUiFromState()
        {
            UpdateFrequencyReadout();
            txtMode.Text = GetModeName(_state.Mode);
            UpdateModeButtonStyles(_state.Mode);
            UpdateTuneStepLabel();
            UpdateLcdBadges();
            UpdateFilterDisplay();
            UpdateRitDisplay();
            UpdateAuxDisplay();

            SetActive(btnAtt, _state.AttOn);
            SetActive(btnPreAmp, _state.PreAmpOn);
            SetActive(btnVox, _state.VoxOn);
            SetActive(btnProc, _state.ProcOn);
            SetActive(btnAtTune, _state.AtTuneOn);
            SetActive(btnNR, _state.NrState != 0);
            SetActive(btnBC, _state.BcOn);
            SetActive(btnSplit, _state.SplitOn);
            SetActive(btnRit, _state.RitOn);
            SetActive(btnXit, _state.XitOn);
            SetActive(btnMV, _state.MemMode);
            SetActive(btnScan, _state.ScanOn);
            SetActive(btn1MHz, _state.Step1MHz);
            SetActive(btnFine, _state.FineOn);
            SetActive(btnNB, _state.NbOn);
            SetActive(btnAGC, _state.AgcFast);

            btnNR.Content = _state.NrState switch { 0 => "N.R", 1 => "N.R.1", _ => "N.R.2" };
            SetActive(btnFilter, _isFilterAdjustMode);
            UpdateGainMeters();
            UpdatePhonesMicKnobsVisual();
            _afRfOuterAngle = KnobAngleFromValue(_state.RfValue);
            _afRfInnerAngle = KnobAngleFromValue(_state.AfValue);
            rtAfRfOuter.Angle = _afRfOuterAngle;
            rtAfRfInner.Angle = _afRfInnerAngle;
            UpdateMeterLegendVisibility();
            SyncPhonesMonitorGain();
            SyncMicWindowsEndpointVolume();
        }

        private void UpdatePhonesMicKnobsVisual()
        {
            rtPhones.Angle = (_state.PhonesValue - 50) * 2.4;
            rtMicGain.Angle = (_state.MicGainValue - 50) * 2.4;
        }

        private static double KnobAngleFromValue(int value0to100)
            => (Math.Clamp(value0to100, 0, 100) - 50) * 2.4;

        private void UpdateMeterLegendVisibility()
        {
            // Keep fixed geometry: never collapse/move legend rows.
            // We only switch intensity (ghost vs lit), like an old LCD layer.
            const double ghost = 20.0 / 255.0;
            const double lit = 1.0;

            SetOpacityRecursive(smeterLegendCanvas, lit);
            txtSmeterLegendS.Opacity = lit;
            SetOpacityRecursive(pwrLegendCanvas, lit);
            txtAlcLegend.Opacity = lit;
            alcLegendLine.Opacity = lit;

            SetOpacityRecursive(swrLegendGrid, _state.ProcOn ? ghost : lit);
            SetOpacityRecursive(compLegendGrid, _state.ProcOn ? lit : ghost);
        }

        private void EnsureMeterSegmentsInitialized()
        {
            _sharedMeterSegments ??= sharedMeterCanvas.Children
                .OfType<System.Windows.Shapes.Rectangle>()
                .Where(r => r.Height >= 6)
                .OrderBy(Canvas.GetLeft)
                .ToArray();

            _swrMeterSegments ??= swrMeterCanvas.Children
                .OfType<System.Windows.Shapes.Rectangle>()
                .Where(r => r.Height >= 6)
                .OrderBy(Canvas.GetLeft)
                .ToArray();

            _alcMeterSegments ??= alcMeterCanvas.Children
                .OfType<System.Windows.Shapes.Rectangle>()
                .Where(r => r.Height >= 6)
                .OrderBy(Canvas.GetLeft)
                .ToArray();

            _smeterTickMarkers ??=
            [
                smeterMarkS1,
                smeterMarkS3,
                smeterMarkS5,
                smeterMarkS7,
                smeterMarkS9
            ];
        }

        private void ClearRadioMeterBars()
        {
            EnsureMeterSegmentsInitialized();
            SetSegmentFill(_sharedMeterSegments, 0.0);
            SetSegmentFill(_swrMeterSegments, 0.0);
            SetSegmentFill(_alcMeterSegments, 0.0);
        }

        private void UpdateRadioMetersFromCat()
        {
            if (!_catReady || _cat is null)
            {
                ClearRadioMeterBars();
                ShowMeterDebug("SM: CAT not ready");
                return;
            }

            EnsureMeterSegmentsInitialized();

            if (_state.IsTx)
            {
                // Prioritize SWR/COMP responsiveness; refresh PWR/ALC less frequently.
                int midMeter = _state.ProcOn ? MeterComp : MeterSwr;
                if (TryReadMeterValue(midMeter, out int mid))
                    _txMidRatio = _state.ProcOn ? (mid / 30.0) : MapSwrRawToRatio(mid);

                // Stagger lower-priority reads to reduce command queue pressure.
                switch (_txMeterPollPhase % 6)
                {
                    case 0:
                    case 3:
                        if (TryReadMeterValue(MeterPwr, out int pwr))
                            _txPwrRatio = pwr / 30.0;
                        break;
                    case 1:
                    case 4:
                        if (TryReadMeterValue(MeterAlc, out int alc))
                            _txAlcRatio = alc / 30.0;
                        break;
                }
                _txMeterPollPhase = (_txMeterPollPhase + 1) % 6;

                SetSharedMeterFill(_txPwrRatio);
                SetSegmentFill(_swrMeterSegments, _txMidRatio);
                SetSegmentFill(_alcMeterSegments, _txAlcRatio);
            }
            else
            {
                // RX: S-meter on top bar only.
                if (TryReadSmeterRaw(out int s))
                {
                    SetSharedMeterFill(MapSmeterRawToRatio(s));
                    ShowMeterDebug($"SM RX: {s:00}/30 ({_lastMeterDebugText})");
                }
                else
                {
                    ShowMeterDebug($"SM RX: read fail ({_lastMeterDebugText})");
                }
                SetSegmentFill(_swrMeterSegments, 0.0);
                SetSegmentFill(_alcMeterSegments, 0.0);
            }
        }

        private void BootstrapReadFromRig()
        {
            if (_cat is null)
                return;

            // One command per tick to keep CAT queue responsive.
            string[] cmds =
            {
                "PC;", "AG;", "RG;", "SQ;", "RA;", "PA;", "VX;", "PR;",
                "NR;", "BC;", "NB;", "FS;", "AN;", "MG;", "KS;", "SD;"
            };
            string cmd = cmds[_catBootstrapReadIndex % cmds.Length];
            _catBootstrapReadIndex++;

            if (!_cat.TryReadCustomCommand(cmd, 16, ";", out string reply) || string.IsNullOrWhiteSpace(reply))
                return;

            if (TryReadCmdInt(reply, "PC", out int pc)) _state.TxPwrValue = Math.Clamp(pc, 0, 100);
            if (TryReadCmdInt(reply, "AG", out int ag)) _state.AfValue = Scale0ToRange(ag * 100 / 255, 0, 100);
            if (TryReadCmdInt(reply, "RG", out int rg)) _state.RfValue = Scale0ToRange(rg * 100 / 255, 0, 100);
            if (TryReadCmdInt(reply, "SQ", out int sq)) _state.SqlValue = Scale0ToRange(sq * 100 / 255, 0, 100);
            if (TryReadCmdInt(reply, "RA", out int ra)) _state.AttOn = ra != 0;
            if (TryReadCmdInt(reply, "PA", out int pa)) _state.PreAmpOn = pa != 0;
            if (TryReadCmdInt(reply, "VX", out int vx)) _state.VoxOn = vx != 0;
            if (TryReadCmdInt(reply, "PR", out int pr)) _state.ProcOn = pr != 0;
            if (TryReadCmdInt(reply, "NR", out int nr)) _state.NrState = Math.Clamp(nr, 0, 2);
            if (TryReadCmdInt(reply, "BC", out int bc)) _state.BcOn = bc != 0;
            if (TryReadCmdInt(reply, "NB", out int nb)) _state.NbOn = nb != 0;
            if (TryReadCmdInt(reply, "FS", out int fs)) _state.FineOn = fs != 0;
            if (TryReadCmdInt(reply, "AN", out int an)) _state.AntSel = an == 2 ? 2 : 1;
            if (TryReadCmdInt(reply, "MG", out int mg)) _state.TxMicValue = Scale0ToRange(mg * 100 / 255, 0, 100);
            if (TryReadCmdInt(reply, "KS", out int ks)) _state.TxKeyValue = Scale0ToRange((ks - 4) * 100 / 56, 0, 100);
            if (TryReadCmdInt(reply, "SD", out int sd)) _state.TxDelayValue = Scale0ToRange(sd / 10, 0, 100);
        }

        private static bool TryReadCmdInt(string reply, string prefix, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(reply) || string.IsNullOrWhiteSpace(prefix))
                return false;

            if (!reply.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            int i = prefix.Length;
            while (i < reply.Length && char.IsDigit(reply[i]))
                i++;
            if (i <= prefix.Length)
                return false;

            return int.TryParse(reply.Substring(prefix.Length, i - prefix.Length), out value);
        }

        private void PrimeLastSentStateFromCurrent()
        {
            _lastVfoAHz = _state.VfoAHz;
            _lastVfoBHz = _state.VfoBHz;
            _lastMode = _state.Mode;
            _lastIsTx = _state.IsTx;
            _lastSplitOn = _state.SplitOn;
            _lastActiveVfo = _state.ActiveVfo == 1;
            _lastAttOn = _state.AttOn;
            _lastPreAmpOn = _state.PreAmpOn;
            _lastVoxOn = _state.VoxOn;
            _lastProcOn = _state.ProcOn;
            _lastNrState = _state.NrState;
            _lastBcOn = _state.BcOn;
            _lastNbOn = _state.NbOn;
            _lastFineOn = _state.FineOn;
            _lastAntSel = _state.AntSel;
            _lastAfValue = _state.AfValue;
            _lastRfValue = _state.RfValue;
            _lastSqlValue = _state.SqlValue;
            _lastIfShiftValue = _state.IfShiftValue;
            _lastDspHighValue = _state.DspHighValue;
            _lastDspLowValue = _state.DspLowValue;
            _lastRitOn = _state.RitOn;
            _lastXitOn = _state.XitOn;
            _lastRitOffsetCentiKhz = _state.RitOffsetCentiKhz;
            _lastTxMicValue = _state.TxMicValue;
            _lastTxKeyValue = _state.TxKeyValue;
            _lastTxDelayValue = _state.TxDelayValue;
            _lastTxPwrValue = _state.TxPwrValue;
            _lastAtTuneOn = _state.AtTuneOn;
        }

        private bool TryReadMeterValue(int meterMode, out int value)
        {
            value = 0;
            if (_cat is null)
                return false;

            _cat.SendCustomCommand($"RM{meterMode};");
            _cat.RequestSmeterRead();
            return TryReadSmeterRaw(out value);
        }

        private bool TryReadSmeterRaw(out int value)
        {
            value = 0;
            if (_cat is null)
                return false;

            // First choice: direct OmniRig signal property when available.
            if (_cat.TryReadSignalLevel(out int level))
            {
                // Normalize typical 0..255 style to TS-570 style 0..30.
                value = Math.Clamp((int)Math.Round(level * (30.0 / 255.0)), 0, 30);
                _lastMeterDebugText = $"prop={level}";
                return true;
            }

            _cat.RequestSmeterRead();
            if (_cat.TryGetLastSmeterRaw(out int raw, out string rawText))
            {
                value = raw;
                _lastMeterDebugText = $"event='{rawText}'";
                return true;
            }

            _lastMeterDebugText = $"CustomReply no SM yet ({_cat.GetCustomReplyDebug()})";
            return false;
        }

        private void ShowMeterDebug(string text)
        {
            if (!_meterDebugEnabled)
            {
                txtSmeter.Visibility = Visibility.Collapsed;
                return;
            }
            txtSmeter.Visibility = Visibility.Visible;
            txtSmeter.Foreground = LcdActiveBrush;
            txtSmeter.Text = text;
        }

        private static double MapSmeterRawToRatio(int raw0to30)
        {
            int raw = Math.Clamp(raw0to30, 0, 30);

            // Field calibration (Rafa station):
            // keep low end (around S3) as-is, boost mid/high range because
            // real S9 was visually landing around S5/S6 in the app.
            int calibrated = raw <= 3
                ? raw
                : raw <= 12
                    ? raw + 4
                    : raw + 6;
            calibrated = Math.Clamp(calibrated, 0, 30);

            // TS-570 style visual calibration:
            // - Raw 0..9 maps to S0..S9 region (about first 14/30 of bar length)
            // - Raw 10..30 maps to +dB region (remaining part up to +60 dB mark)
            const double s9Ratio = 14.0 / 30.0;
            if (calibrated <= 9)
                return (calibrated / 9.0) * s9Ratio;

            return s9Ratio + ((calibrated - 9) / 21.0) * (1.0 - s9Ratio);
        }

        private static double MapSwrRawToRatio(int raw0to30)
        {
            int raw = Math.Clamp(raw0to30, 0, 30);

            // SWR legend is non-linear: 1 -> 1.5 -> 3 -> infinity.
            // Field calibration: previous mapping read too low.
            // New mapping pushes low/mid values up so SWR~1.5 lands near 4 segments.
            const double zone1 = 4.0 / 13.0;   // around "1.5" mark
            const double zone2 = 8.0 / 13.0;   // around "3" mark

            if (raw <= 3)
                return (raw / 3.0) * zone1;
            if (raw <= 10)
                return zone1 + ((raw - 3) / 7.0) * (zone2 - zone1);
            return zone2 + ((raw - 10) / 20.0) * (1.0 - zone2);
        }

        private static void SetSegmentFill(System.Windows.Shapes.Rectangle[]? segments, double ratio0to1)
        {
            if (segments is null || segments.Length == 0)
                return;

            int litCount = (int)Math.Round(Math.Clamp(ratio0to1, 0.0, 1.0) * segments.Length);
            for (int i = 0; i < segments.Length; i++)
                segments[i].Fill = i < litCount ? LcdActiveBrush : LcdGhostBrush;
        }

        private void SetSharedMeterFill(double ratio0to1)
        {
            SetSegmentFill(_sharedMeterSegments, ratio0to1);
            UpdateSmeterTickMarkers(ratio0to1);
        }

        private void UpdateSmeterTickMarkers(double ratio0to1)
        {
            if (_sharedMeterSegments is null || _sharedMeterSegments.Length == 0 || _smeterTickMarkers is null)
                return;

            int litCount = (int)Math.Round(Math.Clamp(ratio0to1, 0.0, 1.0) * _sharedMeterSegments.Length);
            int[] segmentThresholds = { 2, 6, 10, 14, 18 };
            int n = Math.Min(_smeterTickMarkers.Length, segmentThresholds.Length);
            for (int i = 0; i < n; i++)
                _smeterTickMarkers[i].Fill = litCount >= segmentThresholds[i] ? LcdActiveBrush : LcdGhostBrush;
        }

        private void ForceLegendLit()
        {
            // Kept for backward compatibility with previous calls.
            UpdateMeterLegendVisibility();
        }

        private static void SetOpacityRecursive(DependencyObject root, double opacity)
        {
            if (root is UIElement ui)
                ui.Opacity = opacity;

            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                SetOpacityRecursive(VisualTreeHelper.GetChild(root, i), opacity);
        }

        private void UpdateLcdBadges()
        {
            UpdateMeterLegendVisibility();
            Show(badgeTx, _state.IsTx);
            Show(badgeRx, !_state.IsTx);
            Show(badgeAt, _state.AtTuneOn);
            txtAntBadge.Text = "ANT";
            Show(badgeAnt1, _state.AntSel == 1);
            Show(badgeAnt2, _state.AntSel == 2);

            Show(badgeAtt, _state.AttOn);
            Show(badgePreAmp, _state.PreAmpOn);
            Show(badgeVox, _state.VoxOn);
            Show(badgeProc, _state.ProcOn);
            Show(badgeNB, _state.NbOn);
            Show(badgeSplit, _state.SplitOn);
            Show(badgeRit, _state.RitOn);
            Show(badgeXit, _state.XitOn);
            Show(badgeFast, _state.AgcFast);
            Show(badgeMch, _state.MemMode);
            Show(badgeCtrl, _catRigOnline);
            Show(badgeVfoA, !_state.MemMode && _state.ActiveVfo == 0);
            Show(badgeVfoB, !_state.MemMode && _state.ActiveVfo == 1);
            Show(badgeVfoM, _state.MemMode);
            Show(badgeFine, _state.FineOn);
            Show(badgeFLock, _state.FLockOn);
            Show(badge1MHz, _state.Step1MHz);
            Show(badgeBC, _state.BcOn);
            Show(badgeTxPwr, false);
            txtPwrBadge.Text = "TX EQ.";

            Show(badgeLsb, _state.Mode == RadioMode.Lsb);
            Show(badgeUsb, _state.Mode == RadioMode.Usb);
            Show(badgeCw, _state.Mode == RadioMode.Cw);
            Show(badgeFsk, _state.Mode == RadioMode.Fsk);
            Show(badgeFm, _state.Mode == RadioMode.Fm);
            Show(badgeAm, _state.Mode == RadioMode.Am);

            Show(badgeNR, _state.NrState > 0);
            txtNRBadge.Text = "N.R.";
            Show(badgeNR1, _state.NrState == 1);
            Show(badgeNR2, _state.NrState == 2);
            txtVfoLabel.Text = _state.MemMode ? " M" : (_state.ActiveVfo == 0 ? " <A" : " <B");
        }

        private void UpdateRitDisplay()
        {
            if (TryShowTxParamDisplay())
                return;

            if (!_state.RitOn)
            {
                UpdateFilterDisplay();
                return;
            }

            txtRightAlpha.Text = FormatRitOffsetDisplay();
            txtRightAlpha.Foreground = LcdActiveBrush;
        }

        private string FormatRitOffsetDisplay()
        {
            int centiKhz = _state.RitOffsetCentiKhz;
            double absKhz = Math.Abs(centiKhz) / 100.0;
            string body = absKhz.ToString("0.00");
            return centiKhz < 0 ? $"-{body}" : body;
        }

        private int GetRitOffsetHz()
        {
            return _core.GetRitOffsetHz();
        }

        private int GetDisplayedFrequencyHz()
        {
            int baseHz = GetTuneVfoFrequency();
            if (_state.RitOn)
                return Math.Max(30_000, baseHz + GetRitOffsetHz());
            return baseHz;
        }

        private int GetTuneVfoIndex()
        {
            if (_state.TfSetOn && _state.SplitOn)
                return 1 - _state.ActiveVfo;
            return _state.ActiveVfo;
        }

        private int GetTuneVfoFrequency()
        {
            int idx = GetTuneVfoIndex();
            return idx == 0 ? _state.VfoAHz : _state.VfoBHz;
        }

        private void UpdateAuxDisplay()
        {
            if (_state.MemMode)
            {
                txtMenuDigits.Text = $"{_state.SelectedMemoryChannel:00}.";
                txtMenuDigits.Foreground = LcdActiveBrush;
                return;
            }

            if (_state.TfSetOn)
            {
                txtMenuDigits.Text = "TF";
                txtMenuDigits.Foreground = LcdActiveBrush;
                return;
            }

            txtMenuDigits.Text = "8. 8.";
            txtMenuDigits.Foreground = LcdGhostBrush;
        }

        private void UpdateFrequencyReadout()
        {
            if (_freqEntry)
                return;

            int hz = GetDisplayedFrequencyHz();
            txtFrequency.Text = FormatFrequency(hz);
            UpdateFrequencyCanvasOffsets(hz);
        }

        private void UpdateFilterDisplay()
        {
            if (TryShowTxParamDisplay())
                return;

            if (_state.RitOn)
                return;

            if (!_isFilterAdjustMode)
            {
                txtRightAlpha.Text = "~.~.~.~.~.~.~.";
                txtRightAlpha.Foreground = LcdGhostBrush;
                return;
            }

            txtRightAlpha.Text = GetCurrentFilterText();
            txtRightAlpha.Foreground = LcdActiveBrush;
        }

        private bool TryShowTxParamDisplay()
        {
            if (_activeTxParam == TxParam.None)
            {
                txtRightPwrDigits.Visibility = Visibility.Collapsed;
                return false;
            }

            if (_activeTxParam == TxParam.Pwr)
            {
                int pwr = _core.NormalizeTxPower(_state.TxPwrValue);
                txtRightAlpha.Text = "PWR-";
                txtRightAlpha.Foreground = LcdActiveBrush;
                string digits = pwr.ToString();
                txtRightPwrDigits.Text = digits;
                txtRightPwrDigits.Foreground = LcdActiveBrush;
                // Per-length anchoring so digits land exactly on LCD ghost cells.
                // 100 -> cells 5-6-7, 95 -> 6-7, 5 -> 7.
                double left = digits.Length switch
                {
                    3 => 425,
                    2 => 452,
                    _ => 479
                };
                Canvas.SetLeft(txtRightPwrDigits, left);
                txtRightPwrDigits.Visibility = Visibility.Visible;
                return true;
            }

            txtRightPwrDigits.Visibility = Visibility.Collapsed;
            return false;
        }

        private string GetCurrentFilterText()
            => _core.GetCurrentFilterText();

        private static void Show(Border b, bool visible)
        {
            b.Visibility = Visibility.Visible;
            b.Opacity = visible ? 1.0 : (20.0 / 255.0);
        }

        private void SetActive(Button btn, bool active)
        {
            btn.Foreground = active
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x92, 0x00))
                : new SolidColorBrush(Color.FromRgb(0xD2, 0xD2, 0xD8));

            if (btn == btnMic || btn == btnPwr || btn == btnKey || btn == btnDelay)
            {
                btn.Effect = active
                    ? new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x92, 0x00), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 }
                    : null;
            }
        }

        private void UpdateTuneStepLabel()
        {
            int s = TuneSteps[_tuneStepIndex];
            txtTuneStep.Text = s >= 1_000 ? $"{s / 1_000} kHz" : $"{s} Hz";
        }

        private static string FormatFrequency(int hz)
        {
            int mhz = hz / 1_000_000;
            int khz = (hz / 1_000) % 1_000;
            int subKhz = (hz / 100) % 10 * 10 + (hz / 10) % 10;
            return $"{mhz}.{khz:D3}.{subKhz:D2}";
        }

        private void UpdateFrequencyCanvasOffsets(int hz)
            => Canvas.SetLeft(txtFrequency, hz >= 10_000_000 ? -9 : 31);

        private static string GetModeName(RadioMode mode) => mode switch
        {
            RadioMode.Lsb => "LSB",
            RadioMode.Usb => "USB",
            RadioMode.Cw => "CW",
            RadioMode.Fsk => "FSK",
            RadioMode.Fm => "FM",
            RadioMode.Am => "AM",
            _ => "---"
        };

        private void UpdateModeButtonStyles(RadioMode mode)
        {
            SetActive(btnLsbUsb, mode is RadioMode.Lsb or RadioMode.Usb);
            SetActive(btnCwFsk, mode is RadioMode.Cw or RadioMode.Fsk);
            SetActive(btnFmAm, mode is RadioMode.Fm or RadioMode.Am);
        }

        private bool GuardFrequencyLock()
        {
            if (!_state.FLockOn) return false;
            txtStatusRight.Text = "  F.LOCK active";
            return true;
        }

        private void btnSynchronize_Click(object sender, RoutedEventArgs e)
        {
            ApplyUiFromState();
            txtStatusRight.Text = "  UI synchronized with local state";
        }

        private void btnPf_Click(object sender, RoutedEventArgs e)
        {
            // PF mapped to Voice Recall sample command in CatCommandMap.
            _cat?.SendCustomCommand("VR1;");
            txtStatusRight.Text = "  PF: VR1 sent";
        }

        private void btnPower_Click(object sender, RoutedEventArgs e)
        {
            _cat?.SendCustomCommand("PS0;");
            txtStatusRight.Text = "  Power command sent";
        }

        private void btnAtt_Click(object sender, RoutedEventArgs e) { _core.ToggleAtt(); SetActive(btnAtt, _state.AttOn); UpdateLcdBadges(); }
        private void btnPreAmp_Click(object sender, RoutedEventArgs e) { _core.TogglePreAmp(); SetActive(btnPreAmp, _state.PreAmpOn); UpdateLcdBadges(); }
        private void btnVox_Click(object sender, RoutedEventArgs e) { _core.ToggleVox(); SetActive(btnVox, _state.VoxOn); UpdateLcdBadges(); }
        private void btnProc_Click(object sender, RoutedEventArgs e) { _core.ToggleProc(); SetActive(btnProc, _state.ProcOn); UpdateLcdBadges(); }
        private void btnSend_Click(object sender, RoutedEventArgs e) { _core.ToggleSend(); UpdateLcdBadges(); }
        private void btnAtTune_Click(object sender, RoutedEventArgs e)
        {
            if (_atTuneLongPressConsumed)
            {
                _atTuneLongPressConsumed = false;
                return;
            }

            _core.ToggleAtTune();
            SetActive(btnAtTune, _state.AtTuneOn);
            UpdateLcdBadges();
            txtStatusRight.Text = _state.AtTuneOn ? "  AT TUNE ON" : "  AT TUNE OFF";
        }

        private void btnAtTune_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _atTunePressedAtUtc = DateTime.UtcNow;
            _atTuneLongPressConsumed = false;
        }

        private void btnAtTune_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            const int longPressMs = 650;
            double heldMs = (DateTime.UtcNow - _atTunePressedAtUtc).TotalMilliseconds;
            if (heldMs < longPressMs)
                return;

            _atTuneLongPressConsumed = true;
            TryAtTuneCurrentFrequency();
            e.Handled = true;
        }

        private void TryAtTuneCurrentFrequency()
        {
            StopScanIfRunning("AT tune");

            if (!_state.AtTuneOn)
            {
                _state.AtTuneOn = true;
                SetActive(btnAtTune, true);
                UpdateLcdBadges();
            }

            int hz = GetTuneVfoFrequency();
            bool inTuneRange = hz >= 1_800_000 && hz <= 30_000_000;
            bool matchAccepted = ((hz / 1000) % 13) != 0;
            bool success = inTuneRange && matchAccepted;
            txtStatusRight.Text = success
                ? $"  AT TUNE OK {hz / 1_000_000.0:0.000} MHz"
                : $"  AT TUNE FAIL {hz / 1_000_000.0:0.000} MHz";
        }
        private void btnBC_Click(object sender, RoutedEventArgs e) { _core.ToggleBc(); SetActive(btnBC, _state.BcOn); UpdateLcdBadges(); }
        private void btnCwTune_Click(object sender, RoutedEventArgs e)
        {
            _cat?.SendCustomCommand("CA1;");
            txtStatusRight.Text = "  CW TUNE triggered";
        }

        private void btnNR_Click(object sender, RoutedEventArgs e)
        {
            _core.CycleNr();
            btnNR.Content = _state.NrState switch { 0 => "N.R", 1 => "N.R.1", _ => "N.R.2" };
            SetActive(btnNR, _state.NrState != 0);
            UpdateLcdBadges();
        }

        private void btnFilter_Click(object sender, RoutedEventArgs e)
        {
            _isFilterAdjustMode = !_isFilterAdjustMode;
            SetActive(btnFilter, _isFilterAdjustMode);
            UpdateFilterDisplay();
            txtStatusRight.Text = _isFilterAdjustMode ? "  FILTER adjust mode" : "  FILTER applied";
        }

        private void SelectTxParam(TxParam p)
        {
            _activeTxParam = _activeTxParam == p ? TxParam.None : p;
            RefreshTxParamButtons();

            _state.MultiChValue = _activeTxParam switch
            {
                TxParam.Mic => _state.TxMicValue,
                TxParam.Pwr => _state.TxPwrValue,
                TxParam.Key => _state.TxKeyValue,
                TxParam.Delay => _state.TxDelayValue,
                _ => _state.MultiChValue
            };
            UpdateRitDisplay();
        }

        private void RefreshTxParamButtons()
        {
            SetActive(btnMic, _activeTxParam == TxParam.Mic);
            SetActive(btnPwr, _activeTxParam == TxParam.Pwr);
            SetActive(btnKey, _activeTxParam == TxParam.Key);
            SetActive(btnDelay, _activeTxParam == TxParam.Delay);
        }

        private void btnMic_Click(object s, RoutedEventArgs e) => SelectTxParam(TxParam.Mic);
        private void btnPwr_Click(object s, RoutedEventArgs e)
        {
            if (_activeTxParam == TxParam.Pwr)
            {
                _activeTxParam = TxParam.None;
                RefreshTxParamButtons();
                UpdateRitDisplay();
                return;
            }

            _activeTxParam = TxParam.Pwr;
            RefreshTxParamButtons();
            _state.TxPwrValue = _core.NormalizeTxPower(_state.TxPwrValue);
            _state.MultiChValue = _state.TxPwrValue;
            UpdateRitDisplay();
        }
        private void btnKey_Click(object s, RoutedEventArgs e) => SelectTxParam(TxParam.Key);
        private void btnDelay_Click(object s, RoutedEventArgs e) => SelectTxParam(TxParam.Delay);

        private void btnLsbUsb_Click(object sender, RoutedEventArgs e) { StopScanIfRunning("mode changed"); _core.CycleLsbUsb(); txtMode.Text = GetModeName(_state.Mode); UpdateModeButtonStyles(_state.Mode); UpdateLcdBadges(); UpdateFilterDisplay(); }
        private void btnCwFsk_Click(object sender, RoutedEventArgs e) { StopScanIfRunning("mode changed"); _core.CycleCwFsk(); txtMode.Text = GetModeName(_state.Mode); UpdateModeButtonStyles(_state.Mode); UpdateLcdBadges(); UpdateFilterDisplay(); }
        private void btnFmAm_Click(object sender, RoutedEventArgs e) { StopScanIfRunning("mode changed"); _core.CycleFmAm(); txtMode.Text = GetModeName(_state.Mode); UpdateModeButtonStyles(_state.Mode); UpdateLcdBadges(); UpdateFilterDisplay(); }
        private void btnMenu_Click(object sender, RoutedEventArgs e)
        {
            // TS-570 menu has no single generic CAT "menu key" command in our map; surface clearly.
            txtStatusRight.Text = "  MENU: no CAT command mapped";
        }

        private void btn1MHz_Click(object sender, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            _core.Toggle1MHz();
            UpdateTuneStepLabel();
            SetActive(btn1MHz, _state.Step1MHz);
            UpdateLcdBadges();
        }

        private void btnFreqDown_Click(object s, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            StopScanIfRunning("manual tune");
            if (_state.Step1MHz)
            {
                _core.SetScanDirection(-1);
                _core.NudgeFrequency(-1_000_000);
                UpdateFrequencyReadout();
                txtStatusRight.Text = "  -1 MHz";
                return;
            }

            _core.SetScanDirection(-1);
            int hz = _core.ChangeBand(-1);
            _state.CurrentVfoHz = hz;
            UpdateFrequencyReadout();
            txtStatusRight.Text = $"  Band: {hz / 1_000_000.0:0.000} MHz";
        }

        private void btnFreqUp_Click(object s, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            StopScanIfRunning("manual tune");
            if (_state.Step1MHz)
            {
                _core.SetScanDirection(1);
                _core.NudgeFrequency(1_000_000);
                UpdateFrequencyReadout();
                txtStatusRight.Text = "  +1 MHz";
                return;
            }

            _core.SetScanDirection(1);
            int hz = _core.ChangeBand(1);
            _state.CurrentVfoHz = hz;
            UpdateFrequencyReadout();
            txtStatusRight.Text = $"  Band: {hz / 1_000_000.0:0.000} MHz";
        }
        private void btnStepDown_Click(object s, RoutedEventArgs e) { if (GuardFrequencyLock()) return; if (_tuneStepIndex > 0) _tuneStepIndex--; UpdateTuneStepLabel(); }
        private void btnStepUp_Click(object s, RoutedEventArgs e) { if (GuardFrequencyLock()) return; if (_tuneStepIndex < TuneSteps.Length - 1) _tuneStepIndex++; UpdateTuneStepLabel(); }

        private void btnSplit_Click(object sender, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            StopScanIfRunning("split changed");
            _core.ToggleSplit();
            if (!_state.SplitOn)
                _state.TfSetOn = false;
            SetActive(btnSplit, _state.SplitOn);
            UpdateLcdBadges();
            UpdateAuxDisplay();
            UpdateFrequencyReadout();
        }

        private void btnTfSet_Click(object sender, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            if (_tfSetMouseHoldActive)
                return;
            StopScanIfRunning("TF-SET");
            _state.TfSetOn = !_state.TfSetOn;
            if (_state.TfSetOn && !_state.SplitOn)
                _state.SplitOn = true;
            SetActive(btnSplit, _state.SplitOn);
            UpdateLcdBadges();
            UpdateAuxDisplay();
            UpdateFrequencyReadout();
            txtStatusRight.Text = _state.TfSetOn ? "  TF-SET ON" : "  TF-SET OFF";
        }

        private void btnTfSet_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (GuardFrequencyLock())
            {
                e.Handled = true;
                return;
            }

            _tfSetMouseHoldActive = true;
            StopScanIfRunning("TF-SET hold");
            _state.TfSetOn = true;
            if (!_state.SplitOn)
                _state.SplitOn = true;
            SetActive(btnSplit, _state.SplitOn);
            UpdateLcdBadges();
            UpdateAuxDisplay();
            UpdateFrequencyReadout();
            txtStatusRight.Text = "  TF-SET hold";
            e.Handled = true;
        }

        private void btnTfSet_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_tfSetMouseHoldActive)
                return;

            _tfSetMouseHoldActive = false;
            _state.TfSetOn = false;
            UpdateLcdBadges();
            UpdateAuxDisplay();
            UpdateFrequencyReadout();
            txtStatusRight.Text = "  TF-SET OFF";
            e.Handled = true;
        }

        private void btnAB_Click(object sender, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            StopScanIfRunning("VFO changed");
            _core.ToggleAB();
            if (_state.TfSetOn)
                _state.TfSetOn = false;
            UpdateFrequencyReadout();
            UpdateLcdBadges();
            UpdateAuxDisplay();
        }

        private void btnRit_Click(object sender, RoutedEventArgs e)
        {
            _core.ToggleRit();
            SetActive(btnRit, _state.RitOn);
            UpdateLcdBadges();
            UpdateRitDisplay();
            UpdateFrequencyReadout();
            txtStatusRight.Text = _state.RitOn ? "  RIT ON" : "  RIT OFF";
        }

        private void btnMV_Click(object sender, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            StopScanIfRunning("M/V changed");
            _core.ToggleMemMode();
            if (_state.MemMode)
                _core.RecallMemoryChannel(_state.SelectedMemoryChannel);
            SetActive(btnMV, _state.MemMode);
            UpdateLcdBadges();
            UpdateModeButtonStyles(_state.Mode);
            UpdateFrequencyReadout();
            UpdateAuxDisplay();
        }
        private void btnAeqB_Click(object sender, RoutedEventArgs e) { if (GuardFrequencyLock()) return; _core.CopyAtoB(); }
        private void btnCLS_Click(object sender, RoutedEventArgs e) { _core.ClearRitXit(); _cat?.SendCustomCommand("RC;"); _lastRitOffsetCentiKhz = 0; SetActive(btnRit, false); SetActive(btnXit, false); UpdateLcdBadges(); UpdateRitDisplay(); UpdateFrequencyReadout(); }
        private void btnXit_Click(object sender, RoutedEventArgs e) { _core.ToggleXit(); SetActive(btnXit, _state.XitOn); UpdateLcdBadges(); }
        private void btnScan_Click(object sender, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            _core.ToggleScan();
            if (_state.ScanOn)
            {
                // Manual behavior: entering scan clears RIT/XIT offsets in operation.
                _state.RitOn = false;
                _state.XitOn = false;
                SetActive(btnRit, false);
                SetActive(btnXit, false);
                _scanHoldTicksRemaining = 0;
                UpdateRitDisplay();
            }
            SetActive(btnScan, _state.ScanOn);
            if (_state.ScanOn) _scanTimer.Start();
            else _scanTimer.Stop();
            txtStatusRight.Text = _state.ScanOn ? "  SCAN ON" : "  SCAN OFF";
        }

        private void btnMVfo_Click(object sender, RoutedEventArgs e)
        {
            StopScanIfRunning("M>VFO");
            if (_state.MemMode && _core.RecallMemoryChannel(_state.SelectedMemoryChannel))
            {
                _state.MemMode = false;
                SetActive(btnMV, false);
                UpdateLcdBadges();
                UpdateModeButtonStyles(_state.Mode);
                UpdateFrequencyReadout();
                UpdateAuxDisplay();
                txtStatusRight.Text = $"  M>VFO CH {_state.SelectedMemoryChannel:00}";
                return;
            }

            _core.RecallQuickMemory();
            UpdateFrequencyReadout();
        }

        private void btnMIn_Click(object sender, RoutedEventArgs e)
        {
            if (_state.MemMode)
            {
                _core.StoreMemoryChannel(_state.SelectedMemoryChannel);
                UpdateAuxDisplay();
                txtStatusRight.Text = $"  M.IN CH {_state.SelectedMemoryChannel:00}";
                return;
            }

            _core.StoreQuickMemory();
            txtStatusRight.Text = "  QUICK M.IN";
        }
        private void btnQmMR_Click(object s, RoutedEventArgs e) { _core.RecallQuickMemory(); UpdateFrequencyReadout(); }
        private void btnQmPlus_Click(object s, RoutedEventArgs e) => _core.NextQuickMemChannel();
        private void btnQmMIn_Click(object s, RoutedEventArgs e) => _core.StoreQuickMemory();
        private void btnQmMinus_Click(object s, RoutedEventArgs e) => _core.PrevQuickMemChannel();

        private void btnCH1_Click(object s, RoutedEventArgs e) => _state.QuickMemChannel = 1;
        private void btnCH2_Click(object s, RoutedEventArgs e) => _state.QuickMemChannel = 2;
        private void btnCH3_Click(object s, RoutedEventArgs e) => _state.QuickMemChannel = 3;
        private void btnKey1_Click(object s, RoutedEventArgs e) => btnCH1_Click(s, e);
        private void btnKey2_Click(object s, RoutedEventArgs e) => btnCH2_Click(s, e);
        private void btnKey3_Click(object s, RoutedEventArgs e) => btnCH3_Click(s, e);
        private void btnKey4_Click(object s, RoutedEventArgs e) { _core.ToggleAnt(); UpdateLcdBadges(); }
        private void btnKey5_Click(object s, RoutedEventArgs e) => _core.StoreQuickMemory();
        private void btnKey6_Click(object s, RoutedEventArgs e) { _core.ToggleFine(); SetActive(btnFine, _state.FineOn); UpdateLcdBadges(); }
        private void btnKey7_Click(object s, RoutedEventArgs e) { _core.ToggleNb(); SetActive(btnNB, _state.NbOn); UpdateLcdBadges(); }
        private void btnKey8_Click(object s, RoutedEventArgs e) { _core.ToggleAgcFast(); SetActive(btnAGC, _state.AgcFast); UpdateLcdBadges(); }
        private void btnKey9_Click(object s, RoutedEventArgs e)
        {
            // Keep REV consistent with our current state model (LSB<->USB).
            // CW/FSK reverse sidebands require extra state not modeled yet.
            RadioMode next = GetReverseMode(_state.Mode);
            if (next == _state.Mode)
            {
                txtStatusRight.Text = "  REV: not mapped for this mode";
                return;
            }

            _state.Mode = next;
            txtMode.Text = GetModeName(_state.Mode);
            UpdateModeButtonStyles(_state.Mode);
            UpdateLcdBadges();
            UpdateFilterDisplay();
            txtStatusRight.Text = $"  REV: {_state.Mode}";
        }
        private void btnKey0_Click(object s, RoutedEventArgs e) { _core.ToggleFLock(); UpdateLcdBadges(); }

        private void btnKeypad_Click(object sender, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            if (sender is not Button btn || btn.Tag is not string digit) return;
            if (!_freqEntry) { _freqEntry = true; _freqBuffer = ""; }
            if (_freqBuffer.Length >= 8) return;
            _freqBuffer += digit;
            string padded = _freqBuffer.PadRight(9, '-');
            txtFrequency.Text = $"{padded[0..2]}.{padded[2..5]}.{padded[5..7]}";
        }

        private void btnKeyCLR_Click(object s, RoutedEventArgs e)
        {
            _freqEntry = false;
            _freqBuffer = "";
            UpdateFrequencyReadout();
        }

        private void btnKeyENT_Click(object s, RoutedEventArgs e)
        {
            if (GuardFrequencyLock()) return;
            if (!_freqEntry || _freqBuffer.Length < 3) return;
            StopScanIfRunning("direct freq entry");
            string padded = _freqBuffer.PadRight(8, '0');
            if (int.TryParse(padded, out int raw))
            {
                int hz = raw < 60_000 ? raw * 1_000 : raw * 100;
                if (_state.MemMode)
                {
                    _state.MemMode = false;
                    SetActive(btnMV, false);
                    UpdateLcdBadges();
                    UpdateAuxDisplay();
                }
                _core.SetCurrentFrequency(hz);
                UpdateFrequencyReadout();
            }
            _freqEntry = false;
            _freqBuffer = "";
        }

        private static double NormalizeAngle(double angle) => angle < 0 ? (angle % 360) + 360 : angle % 360;
        private static int ClampKnobValue(int value) => Math.Clamp(value, 0, 100);
        private static double AngleFromSteps(int steps) => steps * 2.2;
        private static int WheelSteps(MouseWheelEventArgs e) => e.Delta > 0 ? 2 : -2;

        private void AdjustVfoFrequency(int deltaHz, string sourceLabel)
        {
            _core.NudgeVfoFrequency(GetTuneVfoIndex(), deltaHz);
            UpdateFrequencyReadout();
            string sign = deltaHz > 0 ? "+" : "";
            txtStatusRight.Text = $"  {sourceLabel}: {sign}{deltaHz} Hz";
        }

        private bool StepUsedMemoryChannel(int direction, string sourceLabel)
        {
            int start = _state.SelectedMemoryChannel;
            for (int i = 0; i < 100; i++)
            {
                int ch = _core.StepMemoryChannel(direction);
                if (_core.IsMemoryChannelUsed(ch) && _core.RecallMemoryChannel(ch))
                {
                    UpdateModeButtonStyles(_state.Mode);
                    UpdateFrequencyReadout();
                    UpdateAuxDisplay();
                    txtStatusRight.Text = $"  {sourceLabel}: CH {ch:00}";
                    return true;
                }
            }

            _state.SelectedMemoryChannel = start;
            txtStatusRight.Text = $"  {sourceLabel}: no memories";
            return false;
        }

        private static double NormalizeSignedAngle(double angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
        }

        private static double GetPointerAngle(Point p, FrameworkElement element)
        {
            double cx = element.ActualWidth * 0.5;
            double cy = element.ActualHeight * 0.5;
            return Math.Atan2(p.Y - cy, p.X - cx) * 180.0 / Math.PI;
        }

        private int ComputeDragSteps(MouseEventArgs e, UIElement element)
        {
            Point current = e.GetPosition(element);
            if (element is not FrameworkElement fe || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                return 0;

            double currentAngle = GetPointerAngle(current, fe);
            double deltaAngle = NormalizeSignedAngle(currentAngle - _lastKnobAngle);

            _lastKnobPoint = current;
            _lastKnobAngle = currentAngle;

            _knobDragAccumulator += deltaAngle;
            const double degreesPerStep = 3.0;

            int steps = (int)(_knobDragAccumulator / degreesPerStep);
            if (steps != 0)
                _knobDragAccumulator -= steps * degreesPerStep;
            return steps;
        }

        private void BeginKnobDrag(MouseButtonEventArgs e, UIElement element)
        {
            _isDraggingKnob = true;
            _knobDragAccumulator = 0;
            _lastKnobPoint = e.GetPosition(element);
            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
                _lastKnobAngle = GetPointerAngle(_lastKnobPoint, fe);
            element.CaptureMouse();
            e.Handled = true;
        }

        private void EndKnobDrag(MouseButtonEventArgs e, UIElement element)
        {
            _isDraggingKnob = false;
            _knobDragAccumulator = 0;
            element.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ScanTimer_Tick(object? sender, EventArgs e)
        {
            if (!_state.ScanOn)
                return;

            if (_state.TfSetOn || _state.IsTx)
            {
                txtStatusRight.Text = "  SCAN paused";
                return;
            }

            if (_scanHoldTicksRemaining > 0)
            {
                _scanHoldTicksRemaining--;
                return;
            }

            int scanDirection = _state.ScanDirection >= 0 ? 1 : -1;

            if (_state.MemMode)
            {
                int start = _state.SelectedMemoryChannel;
                for (int i = 0; i < 100; i++)
                {
                    int ch = _core.StepMemoryChannel(scanDirection);
                    if (_core.IsMemoryChannelUsed(ch) && _core.RecallMemoryChannel(ch))
                    {
                        UpdateFrequencyReadout();
                        UpdateModeButtonStyles(_state.Mode);
                        UpdateLcdBadges();
                        UpdateAuxDisplay();
                        txtStatusRight.Text = $"  MEM SCAN {(scanDirection > 0 ? "UP" : "DN")} CH {ch:00}";
                        if (_state.ScanHoldOnSignal && IsSignalPresent(out int strength))
                        {
                            BeginScanHold($"MEM CH {ch:00}", strength);
                            return;
                        }
                        break;
                    }
                }

                // no memories in use: stop scan to avoid endless spin
                if (start == _state.SelectedMemoryChannel && !_core.IsMemoryChannelUsed(start))
                {
                    _state.ScanOn = false;
                    _scanTimer.Stop();
                    SetActive(btnScan, false);
                    txtStatusRight.Text = "  MEM SCAN stopped (empty)";
                }
                return;
            }

            int stepHz = _state.Step1MHz ? 1_000_000 : TuneSteps[_tuneStepIndex];
            if (_state.FineOn) stepHz = Math.Max(10, stepHz / 10);
            _core.NudgeVfoFrequency(GetTuneVfoIndex(), scanDirection * stepHz);
            UpdateFrequencyReadout();

            if (_state.ScanHoldOnSignal && IsSignalPresent(out int vfoStrength))
            {
                BeginScanHold("VFO", vfoStrength);
                return;
            }

            txtStatusRight.Text = $"  VFO SCAN {(scanDirection > 0 ? "UP" : "DN")}";
        }

        private void BeginScanHold(string source, int strength)
        {
            _scanHoldTicksRemaining = 12; // around 2.1s
            txtStatusRight.Text = $"  SCAN hold {source} S{strength:00}";
        }

        private bool IsSignalPresent(out int signalStrength)
        {
            int probeHz = GetDisplayedFrequencyHz();
            int modeSeed = (int)_state.Mode * 37;
            int deterministicStrength = (int)((probeHz / 100 + modeSeed) % 101);
            int jitter = _scanRandom.Next(-8, 9);
            signalStrength = Math.Clamp(deterministicStrength + jitter, 0, 100);

            if (_state.NbOn)
                signalStrength = Math.Max(0, signalStrength - 6);
            if (_state.BcOn)
                signalStrength = Math.Max(0, signalStrength - 4);

            int sqlThreshold = Math.Clamp(_state.SqlValue - (_state.AgcFast ? 6 : 0), 5, 95);
            bool periodicCarrier = ((probeHz / 1000) % 19) == 0;
            bool squelchOpen = signalStrength >= sqlThreshold || periodicCarrier;
            if (_state.Mode is RadioMode.Usb or RadioMode.Lsb or RadioMode.Cw or RadioMode.Fsk)
                squelchOpen = squelchOpen || signalStrength >= 78;
            return squelchOpen;
        }

        private void ApplyFilterAdjustment(int direction)
        {
            if (!_isFilterAdjustMode)
                return;
            _core.AdjustFilter(direction);
            UpdateFilterDisplay();
        }

        private void StepMultiChFrequency(int detent)
        {
            if (GuardFrequencyLock())
                return;
            StopScanIfRunning("MULTI CH");

            int baseHz = GetTuneVfoFrequency();
            int truncatedHz = (baseHz / 1000) * 1000;
            int targetHz = Math.Max(30_000, truncatedHz + (detent * 10_000));
            _core.SetScanDirection(detent >= 0 ? 1 : -1);
            _core.SetVfoFrequency(GetTuneVfoIndex(), targetHz);
            UpdateFrequencyReadout();
        }

        private void AfRfOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.RfValue = ClampKnobValue(_state.RfValue + step); _afRfOuterAngle = KnobAngleFromValue(_state.RfValue); rtAfRfOuter.Angle = _afRfOuterAngle; e.Handled = true; }
        private void AfRfInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.AfValue = ClampKnobValue(_state.AfValue + step); _afRfInnerAngle = KnobAngleFromValue(_state.AfValue); rtAfRfInner.Angle = _afRfInnerAngle; e.Handled = true; }
        private void IfSqlOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.IfShiftValue = ClampKnobValue(_state.IfShiftValue + step); _ifSqlOuterAngle = KnobAngleFromValue(_state.IfShiftValue); rtIfSqlOuter.Angle = _ifSqlOuterAngle; e.Handled = true; }
        private void IfSqlInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.SqlValue = ClampKnobValue(_state.SqlValue + step); _ifSqlInnerAngle = KnobAngleFromValue(_state.SqlValue); rtIfSqlInner.Angle = _ifSqlInnerAngle; e.Handled = true; }
        private void DspSlopeOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.DspHighValue = ClampKnobValue(_state.DspHighValue + step); _dspSlopeOuterAngle = KnobAngleFromValue(_state.DspHighValue); rtDspSlopeOuter.Angle = _dspSlopeOuterAngle; e.Handled = true; }
        private void DspSlopeInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.DspLowValue = ClampKnobValue(_state.DspLowValue + step); _dspSlopeInnerAngle = KnobAngleFromValue(_state.DspLowValue); rtDspSlopeInner.Angle = _dspSlopeInnerAngle; e.Handled = true; }
        private void PhonesKnob_MouseWheel(object sender, MouseWheelEventArgs e) { _state.PhonesValue = ClampKnobValue(_state.PhonesValue + WheelSteps(e)); UpdateGainMeters(); UpdatePhonesMicKnobsVisual(); SyncPhonesMonitorGain(); e.Handled = true; }
        private void MicGainKnob_MouseWheel(object sender, MouseWheelEventArgs e) { _state.MicGainValue = ClampKnobValue(_state.MicGainValue + WheelSteps(e)); UpdateGainMeters(); UpdatePhonesMicKnobsVisual(); SyncMicWindowsEndpointVolume(); e.Handled = true; }

        private void MultiChKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int detent = e.Delta > 0 ? 1 : -1;
            _multiChAngle = NormalizeAngle(_multiChAngle + detent * 8);
            rtMultiCh.Angle = _multiChAngle;

            if (_isFilterAdjustMode)
            {
                ApplyFilterAdjustment(detent);
            }
            else if (_state.MemMode)
            {
                _core.SetScanDirection(detent > 0 ? 1 : -1);
                int ch = _core.StepMemoryChannel(detent > 0 ? 1 : -1);
                _core.RecallMemoryChannel(ch);
                UpdateModeButtonStyles(_state.Mode);
                UpdateFrequencyReadout();
                UpdateAuxDisplay();
            }
            else if (_activeTxParam == TxParam.Mic)
            {
                _state.TxMicValue = ClampKnobValue(_state.TxMicValue + detent);
                _state.MultiChValue = _state.TxMicValue;
            }
            else if (_activeTxParam == TxParam.Pwr)
            {
                _state.TxPwrValue = _core.NormalizeTxPower(_state.TxPwrValue + detent * 5);
                _state.MultiChValue = _state.TxPwrValue;
                UpdateRitDisplay();
            }
            else if (_activeTxParam == TxParam.Key)
            {
                _state.TxKeyValue = ClampKnobValue(_state.TxKeyValue + detent);
                _state.MultiChValue = _state.TxKeyValue;
            }
            else if (_activeTxParam == TxParam.Delay)
            {
                _state.TxDelayValue = ClampKnobValue(_state.TxDelayValue + detent);
                _state.MultiChValue = _state.TxDelayValue;
            }
            else
            {
                _state.MultiChValue = Math.Clamp(_state.MultiChValue + detent, 1, 99);
                StepMultiChFrequency(detent);
            }

            e.Handled = true;
        }

        private void RitXitKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int detent = e.Delta > 0 ? 1 : -1;
            _core.AdjustRitOffset(detent);
            _ritXitAngle = NormalizeAngle(_ritXitAngle + AngleFromSteps(detent));
            rtRitXit.Angle = _ritXitAngle;

            if (_state.RitOn || _state.XitOn)
            {
                UpdateRitDisplay();
                UpdateFrequencyReadout();
            }

            e.Handled = true;
        }

        private void VfoMainKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_state.FLockOn) { e.Handled = true; return; }
            StopScanIfRunning("VFO");

            int ticks = e.Delta > 0 ? 1 : -1;
            _core.SetScanDirection(ticks >= 0 ? 1 : -1);
            _vfoMainAngle = NormalizeAngle(_vfoMainAngle + ticks * 3.6);
            rtVfoMain.Angle = _vfoMainAngle;
            if (_state.MemMode)
            {
                StepUsedMemoryChannel(ticks >= 0 ? 1 : -1, "MEM dial");
                e.Handled = true;
                return;
            }
            int stepHz = _state.Step1MHz ? 1_000_000 : TuneSteps[_tuneStepIndex];
            if (_state.FineOn) stepHz = Math.Max(10, stepHz / 10);
            AdjustVfoFrequency(ticks * stepHz, "VFO");
            e.Handled = true;
        }

        private void VfoMainKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingVfo = true;
            _lastVfoPoint = e.GetPosition((IInputElement)sender);
            _vfoDragAccumulator = 0;
            if (sender is FrameworkElement fe)
                _lastVfoAngle = GetPointerAngle(_lastVfoPoint, fe);
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void VfoMainKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingVfo = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private void VfoMainKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingVfo || e.LeftButton != MouseButtonState.Pressed || _state.FLockOn) return;
            StopScanIfRunning("VFO drag");

            if (sender is not FrameworkElement element)
                return;

            Point current = e.GetPosition((IInputElement)sender);
            double currentAngle = GetPointerAngle(current, element);
            double deltaAngle = NormalizeSignedAngle(currentAngle - _lastVfoAngle);
            _lastVfoPoint = current;
            _lastVfoAngle = currentAngle;
            _vfoDragAccumulator += deltaAngle;

            const double degreesPerDetent = 2.0;
            int stepHz = _state.Step1MHz ? 1_000_000 : TuneSteps[_tuneStepIndex];
            if (_state.FineOn) stepHz = Math.Max(10, stepHz / 10);

            while (_vfoDragAccumulator >= degreesPerDetent)
            {
                _vfoDragAccumulator -= degreesPerDetent;
                _core.SetScanDirection(1);
                _vfoMainAngle = NormalizeAngle(_vfoMainAngle + 1.8);
                rtVfoMain.Angle = _vfoMainAngle;
                if (_state.MemMode)
                    StepUsedMemoryChannel(1, "MEM drag");
                else
                    AdjustVfoFrequency(stepHz, "VFO drag");
            }

            while (_vfoDragAccumulator <= -degreesPerDetent)
            {
                _vfoDragAccumulator += degreesPerDetent;
                _core.SetScanDirection(-1);
                _vfoMainAngle = NormalizeAngle(_vfoMainAngle - 1.8);
                rtVfoMain.Angle = _vfoMainAngle;
                if (_state.MemMode)
                    StepUsedMemoryChannel(-1, "MEM drag");
                else
                    AdjustVfoFrequency(-stepHz, "VFO drag");
            }
        }

        private void StopScanIfRunning(string reason)
        {
            if (!_state.ScanOn)
                return;

            _state.ScanOn = false;
            _scanTimer.Stop();
            _scanHoldTicksRemaining = 0;
            SetActive(btnScan, false);
            txtStatusRight.Text = $"  SCAN OFF ({reason})";
        }

        private void PhonesKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void PhonesKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void PhonesKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps != 0) { _state.PhonesValue = ClampKnobValue(_state.PhonesValue + steps); UpdateGainMeters(); UpdatePhonesMicKnobsVisual(); SyncPhonesMonitorGain(); } }

        private void MicGainKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void MicGainKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void MicGainKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps != 0) { _state.MicGainValue = ClampKnobValue(_state.MicGainValue + steps); UpdateGainMeters(); UpdatePhonesMicKnobsVisual(); SyncMicWindowsEndpointVolume(); } }

        private void AfRfOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void AfRfOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void AfRfOuterKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.RfValue = ClampKnobValue(_state.RfValue + steps); _afRfOuterAngle = KnobAngleFromValue(_state.RfValue); rtAfRfOuter.Angle = _afRfOuterAngle; }

        private void AfRfInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void AfRfInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void AfRfInnerKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.AfValue = ClampKnobValue(_state.AfValue + steps); _afRfInnerAngle = KnobAngleFromValue(_state.AfValue); rtAfRfInner.Angle = _afRfInnerAngle; }

        private void IfSqlOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void IfSqlOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void IfSqlOuterKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.IfShiftValue = ClampKnobValue(_state.IfShiftValue + steps); _ifSqlOuterAngle = KnobAngleFromValue(_state.IfShiftValue); rtIfSqlOuter.Angle = _ifSqlOuterAngle; }

        private void IfSqlInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void IfSqlInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void IfSqlInnerKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.SqlValue = ClampKnobValue(_state.SqlValue + steps); _ifSqlInnerAngle = KnobAngleFromValue(_state.SqlValue); rtIfSqlInner.Angle = _ifSqlInnerAngle; }

        private void DspSlopeOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void DspSlopeOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void DspSlopeOuterKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.DspHighValue = ClampKnobValue(_state.DspHighValue + steps); _dspSlopeOuterAngle = KnobAngleFromValue(_state.DspHighValue); rtDspSlopeOuter.Angle = _dspSlopeOuterAngle; }

        private void DspSlopeInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void DspSlopeInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void DspSlopeInnerKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.DspLowValue = ClampKnobValue(_state.DspLowValue + steps); _dspSlopeInnerAngle = KnobAngleFromValue(_state.DspLowValue); rtDspSlopeInner.Angle = _dspSlopeInnerAngle; }

        private void RitXitKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void RitXitKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void RitXitKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;

            _core.AdjustRitOffset(steps);
            _ritXitAngle = NormalizeAngle(_ritXitAngle + AngleFromSteps(steps));
            rtRitXit.Angle = _ritXitAngle;

            if (_state.RitOn)
            {
                UpdateRitDisplay();
                UpdateFrequencyReadout();
            }
        }

        private void MultiChKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void MultiChKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void MultiChKnob_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return;
            int steps = ComputeDragSteps(e, el);
            if (steps == 0) return;

            _multiChAngle = NormalizeAngle(_multiChAngle + AngleFromSteps(steps));
            rtMultiCh.Angle = _multiChAngle;

            if (_isFilterAdjustMode)
            {
                ApplyFilterAdjustment(steps > 0 ? 1 : -1);
            }
            else if (_state.MemMode)
            {
                _core.SetScanDirection(steps > 0 ? 1 : -1);
                int ch = _core.StepMemoryChannel(steps > 0 ? 1 : -1);
                _core.RecallMemoryChannel(ch);
                UpdateModeButtonStyles(_state.Mode);
                UpdateFrequencyReadout();
                UpdateAuxDisplay();
            }
            else if (_activeTxParam == TxParam.Mic)
            {
                _state.TxMicValue = ClampKnobValue(_state.TxMicValue + steps);
                _state.MultiChValue = _state.TxMicValue;
            }
            else if (_activeTxParam == TxParam.Pwr)
            {
                _state.TxPwrValue = _core.NormalizeTxPower(_state.TxPwrValue + (steps * 5));
                _state.MultiChValue = _state.TxPwrValue;
                UpdateRitDisplay();
            }
            else if (_activeTxParam == TxParam.Key)
            {
                _state.TxKeyValue = ClampKnobValue(_state.TxKeyValue + steps);
                _state.MultiChValue = _state.TxKeyValue;
            }
            else if (_activeTxParam == TxParam.Delay)
            {
                _state.TxDelayValue = ClampKnobValue(_state.TxDelayValue + steps);
                _state.MultiChValue = _state.TxDelayValue;
            }
            else
            {
                _state.MultiChValue = Math.Clamp(_state.MultiChValue + steps, 1, 99);
                StepMultiChFrequency(steps);
            }
        }
    }
}
