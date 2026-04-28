using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TS570_Remote;

/// <summary>
/// NF desde USB/ACC2: captura WASAPI → volumen PHONES en monitor → salida WASAPI.
/// El nivel MIC se controla en Windows sobre la salida TX (<see cref="WindowsPlaybackEndpointVolume"/>), no aquí.
/// </summary>
internal sealed class Acc2AudioBridge : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _playback;
    private BufferedWaveProvider? _buffer;
    private IWaveProvider? _chain;
    private IDisposable? _chainDisposable;

    private float _playbackGain = 1f;
    private readonly object _gainLock = new();

    public bool IsRunning => _playback?.PlaybackState == PlaybackState.Playing;

    /// <summary>Solo PHONES: ganancia de monitor en software. MIC → endpoint Windows.</summary>
    public void SetPlaybackMonitorGain(float phones01)
    {
        lock (_gainLock)
            _playbackGain = Math.Clamp(phones01, 0f, 1f);
    }

    public void Start(string captureDeviceId, string playbackDeviceId)
    {
        Stop();

        var en = new MMDeviceEnumerator();
        MMDevice capDev = en.GetDevice(captureDeviceId);
        MMDevice playDev = en.GetDevice(playbackDeviceId);

        _capture = new WasapiCapture(capDev);
        WaveFormat capFmt = _capture.WaveFormat;

        _buffer = new BufferedWaveProvider(capFmt)
        {
            BufferLength = Math.Max(capFmt.AverageBytesPerSecond * 4, 400_000),
            DiscardOnBufferOverflow = true
        };

        _capture.DataAvailable += (_, e) =>
        {
            ApplyPhonesGain(e.Buffer, e.BytesRecorded, capFmt);
            _buffer!.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        WaveFormat playFmtTarget = WaveFormat.CreateIeeeFloatWaveFormat(
            playDev.AudioClient.MixFormat.SampleRate,
            playDev.AudioClient.MixFormat.Channels);

        _chain = _buffer;
        if (!FormatsEqualEnough(capFmt, playFmtTarget))
        {
            var resampler = new MediaFoundationResampler(_buffer, playFmtTarget) { ResamplerQuality = 60 };
            _chain = resampler;
            _chainDisposable = resampler;
        }

        _playback = new WasapiOut(playDev, AudioClientShareMode.Shared, false, 200);
        _playback.Init(_chain!);
        _capture.StartRecording();
        _playback.Play();
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { _playback?.Stop(); } catch (Exception ex) { Debug.WriteLine(ex); }

        _capture?.Dispose();
        _capture = null;

        _chainDisposable?.Dispose();
        _chainDisposable = null;
        _chain = null;
        _buffer = null;

        _playback?.Dispose();
        _playback = null;
    }

    private static bool FormatsEqualEnough(WaveFormat a, WaveFormat b)
    {
        return a.SampleRate == b.SampleRate
               && a.Channels == b.Channels
               && a.BitsPerSample == b.BitsPerSample
               && Equals(a.Encoding, b.Encoding);
    }

    private void ApplyPhonesGain(byte[] buffer, int bytes, WaveFormat wf)
    {
        float g;
        lock (_gainLock)
            g = _playbackGain;

        if (g >= 0.9999f || bytes <= 0)
            return;

        if (wf.Encoding == WaveFormatEncoding.IeeeFloat && wf.BitsPerSample == 32)
        {
            int n = bytes / 4;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                float v = BitConverter.ToSingle(buffer, o) * g;
                BitConverter.GetBytes(v).CopyTo(buffer, o);
            }
        }
        else if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < bytes; i += 2)
            {
                short s = BitConverter.ToInt16(buffer, i);
                int m = (int)Math.Round(s * g);
                s = (short)Math.Clamp(m, short.MinValue, short.MaxValue);
                BitConverter.GetBytes(s).CopyTo(buffer, i);
            }
        }
    }

    public void Dispose() => Stop();
}
