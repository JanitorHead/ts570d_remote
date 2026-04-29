using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TS570_Remote;

public partial class DisplayColorWindow : Window
{
    private bool _updating;
    private bool _svDragging;
    private double _hue;
    private double _sat;
    private double _val;
    public Color SelectedColor { get; private set; }

    public DisplayColorWindow(Color initialColor)
    {
        InitializeComponent();
        SetFromColor(initialColor);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => UpdateSvVisuals();

    private void SvPickerGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSvVisuals();

    private void SetFromColor(Color color)
    {
        RgbToHsv(color, out _hue, out _sat, out _val);
        _updating = true;
        sliderHue.Value = _hue;
        sliderR.Value = color.R;
        sliderG.Value = color.G;
        sliderB.Value = color.B;
        ApplySelectedColor(color, updateHex: true);
        _updating = false;
        UpdateSvVisuals();
    }

    private void SliderRgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
            return;

        var color = Color.FromRgb((byte)sliderR.Value, (byte)sliderG.Value, (byte)sliderB.Value);
        RgbToHsv(color, out _hue, out _sat, out _val);
        _updating = true;
        sliderHue.Value = _hue;
        ApplySelectedColor(color, updateHex: true);
        _updating = false;
        UpdateSvVisuals();
    }

    private void SliderHue_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
            return;

        _hue = sliderHue.Value;
        var color = HsvToRgb(_hue, _sat, _val);
        _updating = true;
        sliderR.Value = color.R;
        sliderG.Value = color.G;
        sliderB.Value = color.B;
        ApplySelectedColor(color, updateHex: true);
        _updating = false;
        UpdateSvVisuals();
    }

    private void TxtHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating)
            return;

        if (!TryParseHex(txtHex.Text, out Color color))
            return;

        RgbToHsv(color, out _hue, out _sat, out _val);
        _updating = true;
        sliderHue.Value = _hue;
        sliderR.Value = color.R;
        sliderG.Value = color.G;
        sliderB.Value = color.B;
        ApplySelectedColor(color, updateHex: false);
        _updating = false;
        UpdateSvVisuals();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } || !TryParseHex(hex, out Color color))
            return;

        SetFromColor(color);
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        SetFromColor(Color.FromRgb(255, 146, 0));
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SvPicker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _svDragging = true;
        svPickerGrid.CaptureMouse();
        UpdateFromSvPointer(e.GetPosition(svPickerGrid));
    }

    private void SvPicker_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_svDragging)
            return;
        UpdateFromSvPointer(e.GetPosition(svPickerGrid));
    }

    private void SvPicker_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_svDragging)
            return;
        _svDragging = false;
        svPickerGrid.ReleaseMouseCapture();
        UpdateFromSvPointer(e.GetPosition(svPickerGrid));
    }

    private void UpdateFromSvPointer(Point p)
    {
        double w = Math.Max(1.0, svPickerGrid.ActualWidth);
        double h = Math.Max(1.0, svPickerGrid.ActualHeight);
        double x = Math.Clamp(p.X, 0.0, w);
        double y = Math.Clamp(p.Y, 0.0, h);
        _sat = x / w;
        _val = 1.0 - (y / h);

        var color = HsvToRgb(_hue, _sat, _val);
        _updating = true;
        sliderR.Value = color.R;
        sliderG.Value = color.G;
        sliderB.Value = color.B;
        ApplySelectedColor(color, updateHex: true);
        _updating = false;
        UpdateSvVisuals();
    }

    private void ApplySelectedColor(Color color, bool updateHex)
    {
        if (updateHex)
            txtHex.Text = ToHex(color);
        previewBrush.Color = color;
        SelectedColor = color;
    }

    private void UpdateSvVisuals()
    {
        svHueBrush.Color = HsvToRgb(_hue, 1.0, 1.0);
        double w = Math.Max(1.0, svPickerGrid.ActualWidth);
        double h = Math.Max(1.0, svPickerGrid.ActualHeight);
        svMarker.Margin = new Thickness((_sat * w) - 6.0, ((1.0 - _val) * h) - 6.0, 0, 0);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryParseHex(string text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string hex = text.Trim();
        if (hex.StartsWith("#"))
            hex = hex[1..];
        if (hex.Length != 6)
            return false;

        if (!byte.TryParse(hex[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r))
            return false;
        if (!byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g))
            return false;
        if (!byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return false;

        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);

        if (s <= 0.00001)
        {
            byte gray = (byte)Math.Round(v * 255.0);
            return Color.FromRgb(gray, gray, gray);
        }

        double sector = h / 60.0;
        int i = (int)Math.Floor(sector);
        double f = sector - i;
        double p = v * (1.0 - s);
        double q = v * (1.0 - s * f);
        double t = v * (1.0 - s * (1.0 - f));

        (double r, double g, double b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };

        return Color.FromRgb(
            (byte)Math.Round(r * 255.0),
            (byte)Math.Round(g * 255.0),
            (byte)Math.Round(b * 255.0));
    }

    private static void RgbToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max <= 0.00001 ? 0.0 : delta / max;

        if (delta <= 0.00001)
        {
            h = 0.0;
            return;
        }

        if (Math.Abs(max - r) < 0.00001)
            h = 60.0 * (((g - b) / delta) % 6.0);
        else if (Math.Abs(max - g) < 0.00001)
            h = 60.0 * (((b - r) / delta) + 2.0);
        else
            h = 60.0 * (((r - g) / delta) + 4.0);

        if (h < 0.0)
            h += 360.0;
    }
}
