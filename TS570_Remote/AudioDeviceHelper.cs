using NAudio.CoreAudioApi;

namespace TS570_Remote;

internal static class AudioDeviceHelper
{
    public sealed record AudioDeviceInfo(string Id, string FriendlyName);

    public static IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName));
                }
                finally
                {
                    d.Dispose();
                }
            }
        }
        catch
        {
            // ignored: no audio subsystem
        }

        return list;
    }

    /// <summary>Entradas de grabación (p. ej. interfaz USB en el camino ACC2 → PC).</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName));
                }
                finally
                {
                    d.Dispose();
                }
            }
        }
        catch
        {
            // ignored
        }

        return list;
    }
}
