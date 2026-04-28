using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace TS570_Remote;

/// <summary>
/// Windows playback endpoint master volume.
/// Used for the MIC knob: level toward the radio via the Speakers (USB) style endpoint, without touching RX capture feeding the monitor.
/// </summary>
internal static class WindowsPlaybackEndpointVolume
{
    public static void SetMasterVolumeScalar(string? deviceId, float scalar01)
    {
        if (string.IsNullOrEmpty(deviceId))
            return;

        try
        {
            using var en = new MMDeviceEnumerator();
            using MMDevice dev = en.GetDevice(deviceId);
            if (dev.DataFlow != DataFlow.Render)
                return;

            float v = Math.Clamp(scalar01, 0f, 1f);
            dev.AudioEndpointVolume.MasterVolumeLevelScalar = v;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Playback endpoint volume: {ex.Message}");
        }
    }

    public static bool TryGetMasterVolumeScalar(string? deviceId, out float scalar01)
    {
        scalar01 = 0.5f;
        if (string.IsNullOrEmpty(deviceId))
            return false;

        try
        {
            using var en = new MMDeviceEnumerator();
            using MMDevice dev = en.GetDevice(deviceId);
            if (dev.DataFlow != DataFlow.Render)
                return false;

            scalar01 = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
