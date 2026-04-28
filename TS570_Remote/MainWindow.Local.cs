using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        private Acc2AudioBridge? _acc2Bridge;
        private int _tuneStepIndex = 0;

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

            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            ledCatGreen.Visibility = Visibility.Hidden;
            ledCatAmber.Visibility = Visibility.Visible;
            txtStatusLeft.Text = "Local: simulated panel · CAT/OmniRig pending · audio via USB/ACC2";

            if (!string.IsNullOrEmpty(_settings.AudioTxPlaybackDeviceId)
                && WindowsPlaybackEndpointVolume.TryGetMasterVolumeScalar(_settings.AudioTxPlaybackDeviceId, out float micScalar))
            {
                _state.MicGainValue = (int)Math.Clamp(Math.Round(micScalar * 100.0), 0, 100);
            }

            ApplyUiFromState();
            txtStatusRight.Text = "  TS-570 local core ready";

            meterPhonesTrack.SizeChanged += (_, _) => UpdateGainMeters();
            meterMicTrack.SizeChanged += (_, _) => UpdateGainMeters();
            ContentRendered += (_, _) => UpdateGainMeters();

            Closing += (_, _) => _acc2Bridge?.Dispose();
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

        private void MenuArchivoSalir_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MenuConfigAudio_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AudioSettingsWindow(_settings) { Owner = this };
            if (dlg.ShowDialog() != true)
                return;

            txtStatusRight.Text = "  Audio USB/ACC2 saved";
            if (menuMonitorAcc2.IsChecked == true)
                TryStartAcc2Monitor();
        }

        private void MenuConfigOpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TS570_Remote");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        private void MenuAyudaAcerca_Click(object sender, RoutedEventArgs e)
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
            SyncPhonesMonitorGain();
            SyncMicWindowsEndpointVolume();
        }

        private void UpdatePhonesMicKnobsVisual()
        {
            rtPhones.Angle = (_state.PhonesValue - 50) * 2.4;
            rtMicGain.Angle = (_state.MicGainValue - 50) * 2.4;
        }

        private void UpdateLcdBadges()
        {
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
            Show(badgeVfoA, !_state.MemMode && _state.ActiveVfo == 0);
            Show(badgeVfoB, !_state.MemMode && _state.ActiveVfo == 1);
            Show(badgeVfoM, _state.MemMode);
            Show(badgeFine, _state.FineOn);
            Show(badgeFLock, _state.FLockOn);
            Show(badge1MHz, _state.Step1MHz);
            Show(badgeBC, _state.BcOn);
            Show(badgeTxPwr, true);
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

        private void btnPf_Click(object sender, RoutedEventArgs e) => txtStatusRight.Text = "  PF triggered (local stub)";
        private void btnPower_Click(object sender, RoutedEventArgs e) => txtStatusRight.Text = "  Power command simulated";

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
        private void btnCwTune_Click(object sender, RoutedEventArgs e) => txtStatusRight.Text = "  CW TUNE simulated";

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
            SetActive(btnMic, _activeTxParam == TxParam.Mic);
            SetActive(btnPwr, _activeTxParam == TxParam.Pwr);
            SetActive(btnKey, _activeTxParam == TxParam.Key);
            SetActive(btnDelay, _activeTxParam == TxParam.Delay);
        }

        private void btnMic_Click(object s, RoutedEventArgs e) => SelectTxParam(TxParam.Mic);
        private void btnPwr_Click(object s, RoutedEventArgs e) { SelectTxParam(TxParam.Pwr); _state.TxPwrValue = _core.NormalizeTxPower(_state.TxPwrValue); _state.MultiChValue = _state.TxPwrValue; }
        private void btnKey_Click(object s, RoutedEventArgs e) => SelectTxParam(TxParam.Key);
        private void btnDelay_Click(object s, RoutedEventArgs e) => SelectTxParam(TxParam.Delay);

        private void btnLsbUsb_Click(object sender, RoutedEventArgs e) { StopScanIfRunning("mode changed"); _core.CycleLsbUsb(); txtMode.Text = GetModeName(_state.Mode); UpdateModeButtonStyles(_state.Mode); UpdateLcdBadges(); UpdateFilterDisplay(); }
        private void btnCwFsk_Click(object sender, RoutedEventArgs e) { StopScanIfRunning("mode changed"); _core.CycleCwFsk(); txtMode.Text = GetModeName(_state.Mode); UpdateModeButtonStyles(_state.Mode); UpdateLcdBadges(); UpdateFilterDisplay(); }
        private void btnFmAm_Click(object sender, RoutedEventArgs e) { StopScanIfRunning("mode changed"); _core.CycleFmAm(); txtMode.Text = GetModeName(_state.Mode); UpdateModeButtonStyles(_state.Mode); UpdateLcdBadges(); UpdateFilterDisplay(); }
        private void btnMenu_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Menu simulation pending.", "MENU", MessageBoxButton.OK, MessageBoxImage.Information);

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
        private void btnCLS_Click(object sender, RoutedEventArgs e) { _core.ClearRitXit(); SetActive(btnRit, false); SetActive(btnXit, false); UpdateLcdBadges(); UpdateRitDisplay(); UpdateFrequencyReadout(); }
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
        private void btnKey9_Click(object s, RoutedEventArgs e) => txtStatusRight.Text = "  REV simulated";
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

        private void AfRfOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.RfValue = ClampKnobValue(_state.RfValue + step); _afRfOuterAngle = NormalizeAngle(_afRfOuterAngle + AngleFromSteps(step)); rtAfRfOuter.Angle = _afRfOuterAngle; e.Handled = true; }
        private void AfRfInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.AfValue = ClampKnobValue(_state.AfValue + step); _afRfInnerAngle = NormalizeAngle(_afRfInnerAngle + AngleFromSteps(step)); rtAfRfInner.Angle = _afRfInnerAngle; e.Handled = true; }
        private void IfSqlOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.IfShiftValue = ClampKnobValue(_state.IfShiftValue + step); _ifSqlOuterAngle = NormalizeAngle(_ifSqlOuterAngle + AngleFromSteps(step)); rtIfSqlOuter.Angle = _ifSqlOuterAngle; e.Handled = true; }
        private void IfSqlInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.SqlValue = ClampKnobValue(_state.SqlValue + step); _ifSqlInnerAngle = NormalizeAngle(_ifSqlInnerAngle + AngleFromSteps(step)); rtIfSqlInner.Angle = _ifSqlInnerAngle; e.Handled = true; }
        private void DspSlopeOuterKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.DspHighValue = ClampKnobValue(_state.DspHighValue + step); _dspSlopeOuterAngle = NormalizeAngle(_dspSlopeOuterAngle + AngleFromSteps(step)); rtDspSlopeOuter.Angle = _dspSlopeOuterAngle; e.Handled = true; }
        private void DspSlopeInnerKnob_MouseWheel(object sender, MouseWheelEventArgs e) { int step = WheelSteps(e); _state.DspLowValue = ClampKnobValue(_state.DspLowValue + step); _dspSlopeInnerAngle = NormalizeAngle(_dspSlopeInnerAngle + AngleFromSteps(step)); rtDspSlopeInner.Angle = _dspSlopeInnerAngle; e.Handled = true; }
        private void PhonesKnob_MouseWheel(object sender, MouseWheelEventArgs e) { _state.PhonesValue = ClampKnobValue(_state.PhonesValue + WheelSteps(e)); UpdateGainMeters(); UpdatePhonesMicKnobsVisual(); SyncPhonesMonitorGain(); e.Handled = true; }
        private void MicGainKnob_MouseWheel(object sender, MouseWheelEventArgs e) { _state.MicGainValue = ClampKnobValue(_state.MicGainValue + WheelSteps(e)); UpdateGainMeters(); UpdatePhonesMicKnobsVisual(); SyncMicWindowsEndpointVolume(); e.Handled = true; }

        private void MultiChKnob_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int detent = e.Delta > 0 ? 1 : -1;
            _state.MultiChValue = Math.Clamp(_state.MultiChValue + detent, 1, 99);
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
            else if (_activeTxParam == TxParam.Pwr)
            {
                _state.TxPwrValue = _core.NormalizeTxPower(_state.TxPwrValue + detent * 5);
            }
            else
            {
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

            if (_state.RitOn)
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
        private void AfRfOuterKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.RfValue = ClampKnobValue(_state.RfValue + steps); _afRfOuterAngle = NormalizeAngle(_afRfOuterAngle + AngleFromSteps(steps)); rtAfRfOuter.Angle = _afRfOuterAngle; }

        private void AfRfInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void AfRfInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void AfRfInnerKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.AfValue = ClampKnobValue(_state.AfValue + steps); _afRfInnerAngle = NormalizeAngle(_afRfInnerAngle + AngleFromSteps(steps)); rtAfRfInner.Angle = _afRfInnerAngle; }

        private void IfSqlOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void IfSqlOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void IfSqlOuterKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.IfShiftValue = ClampKnobValue(_state.IfShiftValue + steps); _ifSqlOuterAngle = NormalizeAngle(_ifSqlOuterAngle + AngleFromSteps(steps)); rtIfSqlOuter.Angle = _ifSqlOuterAngle; }

        private void IfSqlInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void IfSqlInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void IfSqlInnerKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.SqlValue = ClampKnobValue(_state.SqlValue + steps); _ifSqlInnerAngle = NormalizeAngle(_ifSqlInnerAngle + AngleFromSteps(steps)); rtIfSqlInner.Angle = _ifSqlInnerAngle; }

        private void DspSlopeOuterKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void DspSlopeOuterKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void DspSlopeOuterKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.DspHighValue = ClampKnobValue(_state.DspHighValue + steps); _dspSlopeOuterAngle = NormalizeAngle(_dspSlopeOuterAngle + AngleFromSteps(steps)); rtDspSlopeOuter.Angle = _dspSlopeOuterAngle; }

        private void DspSlopeInnerKnob_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginKnobDrag(e, (UIElement)sender);
        private void DspSlopeInnerKnob_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndKnobDrag(e, (UIElement)sender);
        private void DspSlopeInnerKnob_MouseMove(object sender, MouseEventArgs e) { if (!_isDraggingKnob || sender is not UIElement el || e.LeftButton != MouseButtonState.Pressed) return; int steps = ComputeDragSteps(e, el); if (steps == 0) return; _state.DspLowValue = ClampKnobValue(_state.DspLowValue + steps); _dspSlopeInnerAngle = NormalizeAngle(_dspSlopeInnerAngle + AngleFromSteps(steps)); rtDspSlopeInner.Angle = _dspSlopeInnerAngle; }

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

            _state.MultiChValue = Math.Clamp(_state.MultiChValue + steps, 1, 99);
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
            else if (_activeTxParam == TxParam.Pwr)
            {
                _state.TxPwrValue = _core.NormalizeTxPower(_state.TxPwrValue + (steps * 5));
            }
            else
            {
                StepMultiChFrequency(steps);
            }
        }
    }
}
