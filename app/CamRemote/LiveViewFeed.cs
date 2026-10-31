using System.IO;
using System.Runtime.CompilerServices;

namespace CamRemote;

/// <summary>Pulls the camera's live-view JPEGs over the SDK connection (Wi-Fi or USB) on its own thread.</summary>
public sealed class LiveViewFeed<TFrame>
{
    private readonly IUi _ui;
    private readonly Func<byte[], int, (TFrame Frame, int Width, int Height)> _decode;
    private readonly byte[] _buf = new byte[8 * 1024 * 1024];
    private Thread? _thread;
    private volatile bool _run;
    private int _busy; // 1 while a decoded frame is waiting for the UI
    private int _frames;

    public string Format { get; private set; } = "";

    /// <summary>The newest JPEG as it came from the camera, for the phone page.</summary>
    public (byte[] Jpeg, long No)? Latest => _latest?.Value;
    private volatile StrongBox<(byte[] Jpeg, long No)>? _latest;

    public event Action<TFrame>? Frame;

    /// <param name="decode">Turns a JPEG (the first n bytes of the buffer) into a frame the window can show.</param>
    public LiveViewFeed(IUi ui, Func<byte[], int, (TFrame Frame, int Width, int Height)> decode)
    {
        _ui = ui;
        _decode = decode;
    }

    public bool Running => _run;

    /// <summary>Frames shown since the last call.</summary>
    public int TakeFrameCount() => Interlocked.Exchange(ref _frames, 0);

    public void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "LiveView" };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(2000);
        _thread = null;
        Format = "";
        _latest = null;
    }

    private void Loop()
    {
        uint lastFrame = uint.MaxValue;
        while (_run)
        {
            int n = Native.cb_liveview(_buf, _buf.Length, out uint frameNo);
            // The camera can hand back the same frame again; only new ones are worth decoding.
            if (n == 0 || (n > 0 && frameNo == lastFrame))
            {
                Thread.Sleep(5);
                continue;
            }
            if (n > 0)
            {
                lastFrame = frameNo;
                _latest = new((_buf.AsSpan(0, n).ToArray(), frameNo));
            }
            if (n < 0)
            {
                Thread.Sleep(250); // not connected yet, or the camera is busy
                continue;
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) continue; // UI still drawing; drop this one
            try
            {
                var (frame, width, height) = _decode(_buf, n);
                Format = $"{width}×{height} live view";
                _ui.Post(() =>
                {
                    _busy = 0;
                    if (!_run) return;
                    Frame?.Invoke(frame);
                    Interlocked.Increment(ref _frames);
                });
            }
            catch
            {
                _busy = 0; // a corrupt frame; skip it
            }
        }
    }
}
