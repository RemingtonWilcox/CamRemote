using System.Runtime.InteropServices;

namespace CamRemote;

public sealed record VideoDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A capture card's video as BGRA frames, dropping frames rather than lagging.</summary>
public sealed class VideoFeed(IUi ui)
{
    private byte[] _back = [];
    private int _busy; // 1 while a frame is waiting to be drawn
    private int _frames;

    public string Format { get; private set; } = "";

    public event Action<byte[], int, int>? Frame;

    /// <summary>Frames drawn since the last call.</summary>
    public int TakeFrameCount() => Interlocked.Exchange(ref _frames, 0);

    public static List<VideoDevice> List() => OperatingSystem.IsMacOS() ? MacNative.ListCaptureDevices() : [];

    /// <summary>Returns a problem to show, or null once it's running.</summary>
    public async Task<string?> StartAsync(string id)
    {
        var fn = _handler ??= OnFrame;
        int result = await Task.Run(() => MacNative.cb_cap_start(id, fn)); // the first time, macOS asks for camera permission here
        Format = MacNative.CaptureFormat();
        return result switch
        {
            0 => null,
            1 => "CamRemote isn't allowed to use the camera input. Turn it on in System Settings › Privacy & Security › Camera.",
            2 => "That device has gone.",
            _ => "It wouldn't start.",
        };
    }

    private MacNative.FrameFn? _handler; // kept here so the runtime does not collect it while capture runs

    private void OnFrame(IntPtr bgra, int w, int h, int stride)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return; // UI still drawing the last one
        int row = w * 4, size = row * h;
        if (_back.Length != size) _back = new byte[size];
        for (int y = 0; y < h; y++) Marshal.Copy(bgra + y * stride, _back, y * row, row);
        ui.Post(() =>
        {
            try
            {
                Frame?.Invoke(_back, w, h);
                Interlocked.Increment(ref _frames);
            }
            finally
            {
                _busy = 0;
            }
        });
    }

    public void Stop()
    {
        if (OperatingSystem.IsMacOS()) MacNative.cb_cap_stop();
        Format = "";
    }
}

/// <summary>Peak levels of the camera's audio as it arrives over HDMI at the capture card.</summary>
public sealed class AudioMeter : IDisposable
{
    public bool Running { get; private set; }

    /// <summary>Starts metering the audio input whose name contains <paramref name="match"/>. Returns a problem, or null.</summary>
    public string? Start(string match)
    {
        Stop();
        int result = MacNative.cb_audio_start(match);
        Running = result == 0;
        return result switch
        {
            0 => null,
            1 => "CamRemote isn't allowed to use the microphone input. Turn it on in System Settings › Privacy & Security › Microphone.",
            2 => $"Couldn't find an audio input for {match}.",
            _ => "Couldn't open the capture card's audio.",
        };
    }

    public void Stop()
    {
        if (Running) MacNative.cb_audio_stop();
        Running = false;
    }

    /// <summary>Peaks in dBFS (left, right) since the last call.</summary>
    public (double Left, double Right) TakePeaks()
    {
        MacNative.cb_audio_peaks(out float l, out float r);
        return (ToDb(l), ToDb(r));
    }

    private static double ToDb(float v) => v <= 0.00001f ? -100 : 20 * Math.Log10(v);

    public void Dispose() => Stop();
}
