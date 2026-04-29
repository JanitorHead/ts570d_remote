using System.IO;
using System.Text.Json;

namespace TS570_Remote;

public sealed class AppSettings
{
    /// <summary>WASAPI playback device used for RX monitoring on the PC (PHONES knob applies software gain).</summary>
    public string? AudioPlaybackDeviceId { get; set; }

    /// <summary>WASAPI playback device whose Windows master level is driven by the MIC knob (PC→radio TX path; WSJT-X Output).</summary>
    public string? AudioTxPlaybackDeviceId { get; set; }

    /// <summary>WASAPI capture from ACC2→PC (audio from the radio; not CAT/OmniRig).</summary>
    public string? AudioCaptureDeviceId { get; set; }

    /// <summary>LCD display base color in #RRGGBB format.</summary>
    public string? DisplayColorHex { get; set; }

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TS570_Remote",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        string dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
