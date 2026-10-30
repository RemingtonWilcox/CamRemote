using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace CamRemote;

public sealed record VideoDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Shows a capture card's video in a WriteableBitmap, dropping frames rather than lagging.</summary>
public sealed class VideoFeed : IAsyncDisposable
{
    private readonly Dispatcher _ui;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[] _back = [];
    private int _busy; // 1 while a frame is waiting to be drawn
    private int _frames;

    public WriteableBitmap? Bitmap { get; private set; }
    public string Format { get; private set; } = "";

    public event Action? BitmapChanged;

    /// <summary>Frames drawn since the last call.</summary>
    public int TakeFrameCount() => Interlocked.Exchange(ref _frames, 0);

    public VideoFeed(Dispatcher ui) => _ui = ui;

    public static async Task<List<VideoDevice>> ListAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups
            .Where(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color))
            .Select(g => new VideoDevice(g.Id, g.DisplayName))
            .ToList();
    }

    public async Task StartAsync(string groupId)
    {
        await StopAsync();
        var group = await MediaFrameSourceGroup.FromIdAsync(groupId);
        _capture = new MediaCapture();
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });

        var source = _capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        // Largest picture first, then the highest frame rate.
        var best = source.SupportedFormats
            .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : f.FrameRate.Numerator / (double)f.FrameRate.Denominator)
            .FirstOrDefault();
        if (best != null)
        {
            await source.SetFormatAsync(best);
            double fps = best.FrameRate.Denominator == 0 ? 0 : best.FrameRate.Numerator / (double)best.FrameRate.Denominator;
            Format = $"{best.VideoFormat.Width}×{best.VideoFormat.Height} @ {fps:0.##} ({best.Subtype})";
        }

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        await _reader.StartAsync();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var sb = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sb == null) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return; // UI still drawing the last one

        SoftwareBitmap? converted = null;
        try
        {
            if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Ignore)
                sb = converted = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            int w = sb.PixelWidth, h = sb.PixelHeight, size = w * h * 4;
            if (_back.Length != size) _back = new byte[size];
            sb.CopyToBuffer(_back.AsBuffer());
            _ui.BeginInvoke(DispatcherPriority.Render, () => Draw(w, h));
        }
        catch
        {
            _busy = 0;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void Draw(int w, int h)
    {
        try
        {
            if (Bitmap == null || Bitmap.PixelWidth != w || Bitmap.PixelHeight != h)
            {
                Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                BitmapChanged?.Invoke();
            }
            Bitmap.WritePixels(new Int32Rect(0, 0, w, h), _back, w * 4, 0);
            Interlocked.Increment(ref _frames);
        }
        finally
        {
            _busy = 0;
        }
    }

    public async Task StopAsync()
    {
        if (_reader != null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            await _reader.StopAsync();
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
        Bitmap = null; // so the next start announces a fresh bitmap
        Format = "";
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
