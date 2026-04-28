using System.Windows;
using System.Windows.Controls;

namespace TS570_Remote;

public partial class AudioSettingsWindow : Window
{
    private readonly AppSettings _settings;

    public AudioSettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadDevices();

        SelectBySavedId(cmbCapture, settings.AudioCaptureDeviceId);
        SelectBySavedId(cmbPlayback, settings.AudioPlaybackDeviceId);
        SelectBySavedId(cmbTxPlayback, settings.AudioTxPlaybackDeviceId);
    }

    private static void LoadCombo(ComboBox cmb, IReadOnlyList<AudioDeviceHelper.AudioDeviceInfo> devices, string emptyMessage)
    {
        cmb.Items.Clear();
        foreach (var d in devices)
            cmb.Items.Add(d);
        if (cmb.Items.Count == 0)
            cmb.Items.Add(new AudioDeviceHelper.AudioDeviceInfo("", emptyMessage));
    }

    private static void SelectBySavedId(ComboBox cmb, string? savedId)
    {
        if (cmb.Items.Count == 0)
            return;
        int idx = -1;
        if (!string.IsNullOrEmpty(savedId))
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (((AudioDeviceHelper.AudioDeviceInfo)cmb.Items[i]!).Id == savedId)
                {
                    idx = i;
                    break;
                }
            }
        }
        cmb.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void LoadDevices()
    {
        LoadCombo(cmbCapture, AudioDeviceHelper.GetCaptureDevices(), "(No hay dispositivos de entrada)");
        LoadCombo(cmbPlayback, AudioDeviceHelper.GetPlaybackDevices(), "(No hay dispositivos de salida)");
        LoadCombo(cmbTxPlayback, AudioDeviceHelper.GetPlaybackDevices(), "(No hay dispositivos de salida)");
    }

    private static void SaveIfValid(ComboBox cmb, Action<string?> setter)
    {
        if (cmb.SelectedItem is AudioDeviceHelper.AudioDeviceInfo { Id: var id } && !string.IsNullOrEmpty(id))
            setter(id);
        else
            setter(null);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        SaveIfValid(cmbCapture, id => _settings.AudioCaptureDeviceId = id);
        SaveIfValid(cmbPlayback, id => _settings.AudioPlaybackDeviceId = id);
        SaveIfValid(cmbTxPlayback, id => _settings.AudioTxPlaybackDeviceId = id);
        _settings.Save();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
