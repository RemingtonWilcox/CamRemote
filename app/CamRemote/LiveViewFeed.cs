using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CamRemote;

/// <summary>Pulls the camera's live-view JPEGs over the SDK connection (Wi-Fi or USB) on its own thread.</summary>
public sealed class LiveViewFeed
{
    private readonly Dispatcher _ui;
    private readonly byte[] _buf = new byte[8 * 1024 * 1024];
    private Thread? _thread;
    private volatile bool _run;
    private int _busy; // 1 while a decoded frame is waiting for the UI
    private int _frames;

    public string Format { get; private set; } = "";

    public event Action<BitmapSource>? Frame;

    public LiveViewFeed(Dispatcher ui) => _ui = ui;

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
            if (n > 0) lastFrame = frameNo;
            if (n < 0)
            {
                Thread.Sleep(250); // not connected yet, or the camera is busy
                continue;
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) continue; // UI still drawing; drop this one
            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(_buf, 0, n, false))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                Format = $"{bmp.PixelWidth}×{bmp.PixelHeight} live view";
                _ui.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    _busy = 0;
                    if (!_run) return;
                    Frame?.Invoke(bmp);
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
