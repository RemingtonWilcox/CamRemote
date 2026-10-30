using NAudio.CoreAudioApi;
using NAudio.Wave;

// NAudio 3 prefers its new WasapiRecorder; the classic capture class is still supported and is all a meter needs.
#pragma warning disable CS0618

namespace CamRemote;

/// <summary>
/// Peak levels of the camera's audio as it arrives over HDMI at the capture card. Over Wi-Fi the
/// camera sends no audio, so there is nothing to meter there.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private WasapiCapture? _capture;
    private readonly float[] _peak = new float[2]; // highest sample since the last read, per channel
    private readonly object _lock = new();
    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public string DeviceName { get; private set; } = "";
    public bool Running => _capture != null;

    /// <summary>Starts metering the capture input whose name contains <paramref name="match"/>. Returns false if none.</summary>
    public bool Start(string match)
    {
        Stop();
        using var devices = new MMDeviceEnumerator();
        var device = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(match, StringComparison.OrdinalIgnoreCase));
        if (device == null) return false;
        DeviceName = device.FriendlyName;
        _capture = new WasapiCapture(device);
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
        return true;
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        DeviceName = "";
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture!.WaveFormat;
        int channels = fmt.Channels, bytes = fmt.BitsPerSample / 8;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                       || (fmt is WaveFormatExtensible x && x.SubFormat == IeeeFloat);
        float l = 0, r = 0;
        for (int i = 0, ch = 0; i + bytes <= e.BytesRecorded; i += bytes, ch = (ch + 1) % channels)
        {
            float v = isFloat && bytes == 4 ? BitConverter.ToSingle(e.Buffer, i)
                    : bytes == 2 ? BitConverter.ToInt16(e.Buffer, i) / 32768f
                    : bytes == 4 ? BitConverter.ToInt32(e.Buffer, i) / 2147483648f
                    : 0;
            v = Math.Abs(v);
            if (ch == 0) l = Math.Max(l, v);
            else if (ch == 1) r = Math.Max(r, v);
        }
        if (channels == 1) r = l;
        lock (_lock)
        {
            _peak[0] = Math.Max(_peak[0], l);
            _peak[1] = Math.Max(_peak[1], r);
        }
    }

    /// <summary>Peaks in dBFS (left, right) since the last call.</summary>
    public (double Left, double Right) TakePeaks()
    {
        lock (_lock)
        {
            var result = (ToDb(_peak[0]), ToDb(_peak[1]));
            _peak[0] = _peak[1] = 0;
            return result;
        }
    }

    private static double ToDb(float v) => v <= 0.00001f ? -100 : 20 * Math.Log10(v);

    public void Dispose() => Stop();
}
