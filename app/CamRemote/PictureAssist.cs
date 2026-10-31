using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CamRemote;

public sealed record AssistOptions(bool Active, bool Zebra, int ZebraLevel, bool Peaking, int PeakSensitivity, uint PeakColor);

public sealed class AssistResult
{
    public int[] Luma = new int[256], Red = new int[256], Green = new int[256], Blue = new int[256];
    public double OverPercent, UnderPercent;
    public int Width, Height;
    public byte[] Overlay = []; // premultiplied BGRA; empty when zebras and peaking are off
}

/// <summary>
/// Reads the picture on its own thread for the histogram, zebras and focus peaking. The A7S III can't send
/// these over the SDK, so they're worked out here from the same picture you see. Frames are dropped rather than queued.
/// </summary>
public sealed class PictureAssist(Dispatcher ui)
{
    private const int MinFrameMs = 66; // about 15 updates a second is plenty for a guide
    private const int UnderLevel = 8; // about 3%
    private static readonly int[] PeakContrast = [48, 30, 18]; // low, medium, high sensitivity: the faintest edge that counts
    private const double PeakCrispness = 0.4; // share of an edge's contrast that must land in one pixel
    private const int PeakReach = 3; // pixels either side an edge is measured over
    private const int PeakGap = 1; // pixels that can separate two parts of one edge
    private const int PeakMinRun = 24; // the shortest edge, in pixels, that counts as focus and not speckle

    private readonly AssistResult _result = new();
    private byte[] _pixels = [];
    private byte[] _luma = [];
    private volatile AssistOptions _options = new(false, false, 90, false, 1, 0);
    private int _busy;
    private long _lastAt;

    public event Action<AssistResult>? Done;

    public AssistOptions Options { get => _options; set => _options = value; }

    /// <summary>Call on the UI thread with each new frame.</summary>
    public void Offer(BitmapSource frame)
    {
        var options = _options;
        long now = Environment.TickCount64;
        if (!options.Active || now - _lastAt < MinFrameMs) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _lastAt = now;
        try
        {
            BitmapSource source = frame.Format == PixelFormats.Bgr32 || frame.Format == PixelFormats.Bgra32
                ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            int w = source.PixelWidth, h = source.PixelHeight;
            if (_pixels.Length != w * h * 4) _pixels = new byte[w * h * 4];
            source.CopyPixels(_pixels, w * 4, 0);
            Task.Run(() =>
            {
                try
                {
                    Analyze(_pixels, w, h, options, _result, ref _luma);
                    ui.BeginInvoke(() =>
                    {
                        _busy = 0;
                        if (_options.Active) Done?.Invoke(_result);
                    });
                }
                catch
                {
                    _busy = 0;
                }
            });
        }
        catch
        {
            _busy = 0; // a frame that couldn't be read; skip it
        }
    }

    /// <summary>Fills in the result for one BGRA frame. Levels are 0–100% of the 8-bit picture.</summary>
    public static void Analyze(byte[] px, int w, int h, AssistOptions o, AssistResult r, ref byte[] lumaBuf)
    {
        Array.Clear(r.Luma); Array.Clear(r.Red); Array.Clear(r.Green); Array.Clear(r.Blue);
        int pixels = w * h;
        if (lumaBuf.Length != pixels) lumaBuf = new byte[pixels];
        byte[] luma = lumaBuf;
        for (int i = 0; i < pixels; i++)
        {
            int b = px[i * 4], g = px[i * 4 + 1], red = px[i * 4 + 2];
            int y = (red * 54 + g * 183 + b * 19) >> 8; // Rec.709
            luma[i] = (byte)y;
            r.Luma[y]++; r.Red[red]++; r.Green[g]++; r.Blue[b]++;
        }

        int level = o.ZebraLevel * 255 / 100;
        long over = 0, under = 0;
        for (int i = level; i < 256; i++) over += r.Luma[i];
        for (int i = 0; i <= UnderLevel; i++) under += r.Luma[i];
        r.OverPercent = 100.0 * over / pixels;
        r.UnderPercent = 100.0 * under / pixels;
        r.Width = w;
        r.Height = h;

        if (!o.Zebra && !o.Peaking)
        {
            r.Overlay = [];
            return;
        }
        if (r.Overlay.Length != pixels * 4) r.Overlay = new byte[pixels * 4];
        else Array.Clear(r.Overlay);
        if (o.Zebra) DrawZebras(luma, w, h, level, r.Overlay);
        if (o.Peaking) DrawPeaking(luma, w, h, o, r.Overlay);
    }

    /// <summary>Dark stripes, since they only ever land on the brightest parts of the picture.</summary>
    private static void DrawZebras(byte[] luma, int w, int h, int level, byte[] overlay)
    {
        int period = Math.Max(10, w / 110);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (luma[y * w + x] >= level && (x + y) % period < period / 2)
                    overlay[(y * w + x) * 4 + 3] = 200; // black at 78%
    }

    /// <summary>
    /// Marks edges by how crisp they are, not how strong: the share of an edge's whole contrast that happens
    /// within one pixel. A soft edge against bright sky has plenty of contrast but spreads it out, so it scores low.
    /// Then only connected runs are kept: in a small preview, sky through distant leaves also looks like crisp
    /// dots, but real focus shows up as lines.
    /// </summary>
    private static void DrawPeaking(byte[] luma, int w, int h, AssistOptions o, byte[] overlay)
    {
        int minContrast = PeakContrast[Math.Clamp(o.PeakSensitivity, 0, 2)];
        int minStep = (int)(minContrast * PeakCrispness);
        var crisp = new bool[w * h];
        for (int y = PeakReach; y < h - PeakReach - 1; y++)
            for (int x = PeakReach; x < w - PeakReach - 1; x++)
            {
                int i = y * w + x;
                int across = Math.Abs(luma[i + 1] - luma[i]), down = Math.Abs(luma[i + w] - luma[i]);
                int step = Math.Max(across, down);
                if (step < minStep) continue;
                int stride = across >= down ? 1 : w, low = 255, high = 0;
                for (int k = -PeakReach; k <= PeakReach + 1; k++)
                {
                    int v = luma[i + k * stride];
                    if (v < low) low = v;
                    if (v > high) high = v;
                }
                int contrast = high - low;
                crisp[i] = contrast >= minContrast && step >= contrast * PeakCrispness;
            }

        byte blue = (byte)o.PeakColor, green = (byte)(o.PeakColor >> 8), red = (byte)(o.PeakColor >> 16);
        void Mark(int i)
        {
            overlay[i * 4] = blue; overlay[i * 4 + 1] = green; overlay[i * 4 + 2] = red; overlay[i * 4 + 3] = 255;
        }
        var seen = new bool[w * h];
        var run = new List<int>();
        var todo = new Stack<int>();
        for (int start = 0; start < crisp.Length; start++)
        {
            if (!crisp[start] || seen[start]) continue;
            run.Clear();
            seen[start] = true;
            todo.Push(start);
            int left = w, right = 0, top = h, bottom = 0;
            while (todo.Count > 0)
            {
                int i = todo.Pop(), x = i % w, y = i / w;
                run.Add(i);
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (int dy = -PeakGap; dy <= PeakGap; dy++)
                    for (int dx = -PeakGap; dx <= PeakGap; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int n = ny * w + nx;
                        if (crisp[n] && !seen[n])
                        {
                            seen[n] = true;
                            todo.Push(n);
                        }
                    }
            }
            if (Math.Max(right - left, bottom - top) < PeakMinRun) continue;
            foreach (int i in run)
            {
                Mark(i); Mark(i + 1); Mark(i + w); Mark(i + w + 1); // 2 px wide, to survive being scaled down
            }
        }
    }

    /// <summary>Premultiplied BGRA, 256 wide. In colour it shows red, green and blue; otherwise brightness, tinted past the zebra level.</summary>
    public static byte[] DrawHistogram(AssistResult r, int height, bool colour, int zebraLevel)
    {
        var px = new byte[256 * height * 4];
        void Add(int[] bins, uint rgb, int from = 0)
        {
            double peak = 1;
            for (int i = 2; i < 254; i++) peak = Math.Max(peak, bins[i]); // a clipped end shouldn't flatten the rest
            for (int x = from; x < 256; x++)
            {
                int top = height - (int)Math.Min(height, Math.Sqrt(bins[x] / peak) * height);
                for (int y = top; y < height; y++)
                {
                    int i = (y * 256 + x) * 4;
                    px[i] = (byte)Math.Max(px[i], rgb & 255);
                    px[i + 1] = (byte)Math.Max(px[i + 1], (rgb >> 8) & 255);
                    px[i + 2] = (byte)Math.Max(px[i + 2], (rgb >> 16) & 255);
                    px[i + 3] = 255;
                }
            }
        }
        int level = zebraLevel * 255 / 100;
        if (colour)
        {
            Add(r.Red, 0xE05050);
            Add(r.Green, 0x50D070);
            Add(r.Blue, 0x5080F0);
        }
        else
        {
            var below = (int[])r.Luma.Clone();
            Add(below, 0xD0D0D0);
            for (int x = level; x < 256; x++)
                for (int y = 0; y < height; y++)
                    if (px[(y * 256 + x) * 4 + 3] != 0)
                    {
                        int i = (y * 256 + x) * 4;
                        px[i] = 0x39; px[i + 1] = 0xB3; px[i + 2] = 0xE8; // the Warn colour
                    }
        }
        for (int y = 0; y < height; y++)
        {
            int i = (y * 256 + level) * 4;
            px[i] = 0x39; px[i + 1] = 0xB3; px[i + 2] = 0xE8; px[i + 3] = 255;
        }
        return px;
    }
}
