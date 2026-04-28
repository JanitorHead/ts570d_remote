using System.IO;
using System.Text.Json;

namespace TS570_Remote;

public sealed class AppSettings
{
    /// <summary>Salida WASAPI donde se monitoriza el audio de recepción en el PC (equivalente al monitor; knob PHONES en software).</summary>
    public string? AudioPlaybackDeviceId { get; set; }

    /// <summary>Salida WASAPI cuyo volumen maestro de Windows ajusta el knob MIC (audio PC→emisora; equivalente a “Output” en WSJT-X).</summary>
    public string? AudioTxPlaybackDeviceId { get; set; }

    /// <summary>Entrada WASAPI: interfaz USB en el camino ACC2 → PC (audio que sale de la emisora, no CAT/OmniRig).</summary>
    public string? AudioCaptureDeviceId { get; set; }

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
