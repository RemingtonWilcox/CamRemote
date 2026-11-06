using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CamRemote;

/// <summary>
/// Something automatic recording can look for, as the range of COCO numbers the model uses for it.
/// They're all things the model was trained on. <see cref="StartAt"/> is the score a sighting needs to start a take:
/// the model calls seals and distant penguins people at about 0.3 to 0.4, while a real person scores far higher.
/// </summary>
public sealed record RecordSubject(string Name, string Plural, int First, int Last, string Tip, float StartAt = 0.25f)
{
    public static readonly RecordSubject[] All =
    [
        new("Animal", "animals", 16, 25, "Birds and a few other animals: cats, dogs, horses, sheep, cows, elephants, bears, zebras and giraffes."),
        new("Bird", "birds", 16, 16, "Birds only, so a passing cat or dog doesn't start it."),
        new("Person", "people", 1, 1, "People. Keep yourself out of the picture, or it will record you.", 0.5f),
    ];

    public static RecordSubject Named(string name) => All.FirstOrDefault(s => s.Name == name) ?? All[0];
}

/// <summary>
/// The live view as Size by Size RGB bytes for the model: all of it, or a square around a box (0..1 of the picture,
/// by its centre) for a closer look. X, Y, W and H say which part was shown, its top left corner and size as 0..1 of the picture.
/// </summary>
public delegate (byte[] Rgb, float X, float Y, float W, float H) BirdPicture(byte[] jpeg, int size, (float X, float Y, float W, float H)? around);

/// <summary>RF-DETR Small looking for an animal, or another subject, in one small square picture. It runs on this computer only.</summary>
public sealed class BirdModel : IDisposable
{
    public const int Size = 512;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly DenseTensor<float> _pixels = new([1, 3, Size, Size]);

    public const string FileName = "rfdetr_small.onnx"; // the full-size file: the half-size one is slower on a CPU

    // Beside the app on Windows, in Contents/Resources on a Mac.
    public static string FilePath => OperatingSystem.IsMacOS()
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", FileName))
        : Path.Combine(AppContext.BaseDirectory, FileName);

    [DllImport("libc")]
    private static extern int setenv(string name, string value, int overwrite);

    // ONNX Runtime reports its use to Microsoft unless this is set before it loads.
    // .NET's own call doesn't reach native code on a Mac, which is why that one goes through libc.
    static BirdModel()
    {
        if (OperatingSystem.IsMacOS()) setenv("ORT_DISABLE_TELEMETRY", "1", 1);
        else Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
    }

    /// <summary>Whether it runs on the graphics card, which is about seven times as fast as the processor.</summary>
    public bool OnGraphics { get; }

    /// <summary>
    /// On the graphics card through WebGPU when it can be, otherwise on the processor.
    /// The first check on a graphics card takes seconds while it gets ready, so that happens here, on a blank picture.
    /// </summary>
    public BirdModel(bool graphics = true)
    {
        OrtEnv.Instance().DisableTelemetryEvents();
        if (graphics)
            try
            {
                _session = Open(true);
                _input = _session.InputMetadata.Keys.First();
                using (_session.Run([NamedOnnxValue.CreateFromTensor(_input, _pixels)])) { }
                OnGraphics = true;
                Log.Write("The animal detector runs on the graphics card.");
                return;
            }
            catch (Exception ex)
            {
                _session?.Dispose();
                Log.Write("The animal detector can't use the graphics card, so it runs on the processor: " + ex.Message);
            }
        _session = Open(false);
        _input = _session.InputMetadata.Keys.First();
    }

    private static InferenceSession Open(bool graphics)
    {
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), InterOpNumThreads = 1 };
        if (graphics)
        {
#if WINDOWS
            // On Windows WebGPU comes as a separate library, loaded once.
            var env = OrtEnv.Instance();
            lock (Mean)
                if (!_webGpuLoaded)
                {
                    env.RegisterExecutionProviderLibrary("webgpu_ep", Microsoft.ML.OnnxRuntime.EP.WebGpu.WebGpuEp.GetLibraryPath());
                    _webGpuLoaded = true;
                }
            var card = env.GetEpDevices().FirstOrDefault(d => d.EpName == Microsoft.ML.OnnxRuntime.EP.WebGpu.WebGpuEp.GetEpName())
                ?? throw new InvalidOperationException("No graphics card that WebGPU can use.");
            options.AppendExecutionProvider(env, [card], new Dictionary<string, string>());
#else
            options.AppendExecutionProvider("WebGPU", new Dictionary<string, string>());
#endif
        }
        return new InferenceSession(FilePath, options);
    }

#if WINDOWS
    private static bool _webGpuLoaded;
#endif

    /// <summary>
    /// The best animal in a picture of RGB bytes, Size by Size: its score from 0 to 1 and its box as 0..1 of that picture.
    /// The model is shown it the way up the viewer sees it, turned by rotation degrees clockwise, because it finds
    /// far more animals upright.
    /// </summary>
    public (float Score, float X, float Y, float W, float H) Look(byte[] rgb, int rotation, RecordSubject subject)
    {
        int last = Size - 1, plane = Size * Size;
        var pixels = _pixels.Buffer.Span;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var (fromX, fromY) = rotation switch
                {
                    90 => (y, last - x),
                    180 => (last - x, last - y),
                    270 => (last - y, x),
                    _ => (x, y),
                };
                int i = (fromY * Size + fromX) * 3;
                for (int c = 0; c < 3; c++) pixels[c * plane + y * Size + x] = (rgb[i + c] / 255f - Mean[c]) / Std[c];
            }
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, _pixels)]);
        var logits = results.First(r => r.Name == "logits").AsTensor<float>();
        var boxes = results.First(r => r.Name == "pred_boxes").AsTensor<float>();
        float best = float.MinValue;
        int found = 0;
        for (int i = 0; i < logits.Dimensions[1]; i++)
            for (int kind = subject.First; kind <= subject.Last; kind++)
                if (logits[0, i, kind] > best)
                {
                    best = logits[0, i, kind];
                    found = i;
                }
        float bx = boxes[0, found, 0], by = boxes[0, found, 1], bw = boxes[0, found, 2], bh = boxes[0, found, 3];
        var (cx, cy) = rotation switch
        {
            90 => (by, 1 - bx),
            180 => (1 - bx, 1 - by),
            270 => (1 - by, bx),
            _ => (bx, by),
        };
        return (1 / (1 + MathF.Exp(-best)), cx, cy, rotation is 90 or 270 ? bh : bw, rotation is 90 or 270 ? bw : bh);
    }

    /// <summary>
    /// The square of a width by height picture to look at closely around a box (0..1 of the picture, by its centre),
    /// in pixels: four times the box's longer edge, from 256 to 576 pixels, kept inside the picture.
    /// </summary>
    public static (int X, int Y, int Side) CloseLook(int width, int height, (float X, float Y, float W, float H) box)
    {
        int most = Math.Min(576, Math.Min(width, height));
        int side = (int)Math.Clamp(4 * Math.Max(box.W * width, box.H * height), Math.Min(256, most), most);
        return ((int)Math.Clamp(box.X * width - side / 2f, 0, width - side), (int)Math.Clamp(box.Y * height - side / 2f, 0, height - side), side);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>What the trigger needs from a camera: this app's own connection, or another RemoteCam's camera used through it.</summary>
public interface ITriggerCamera
{
    bool Connected { get; }
    bool IsRecording { get; }
    bool RecordWaiting { get; }
    DateTime RecordSent { get; }
    bool IsPhotoMode { get; }
    bool IsFormatting { get; }
    void ToggleRecord();
}

/// <summary>
/// Checks the live view twice a second, starts recording after two sightings in three seconds and stops
/// once no animal has been seen for a while. It only ever stops a take it started itself. A confident
/// sighting also focuses there. Sightings that keep landing in one small area without being sure twice running are
/// scenery, like splashing water. While it's following something, each check also looks closely around the last sighting,
/// which finds small birds the whole picture misses.
/// </summary>
public sealed class BirdTrigger(IUi ui, ITriggerCamera cam, Func<(byte[] Jpeg, long No)?> latest, BirdPicture picture,
    Action<double, double> focus, Func<int> rotation)
{
    private const float Bar = 0.25f;
    private static readonly TimeSpan Follow = TimeSpan.FromSeconds(3); // a sighting this recent gets the closer look
    private static readonly TimeSpan QuickCheck = TimeSpan.FromSeconds(0.4); // slower than this, there's no time for two looks
    private const float Sure = 0.6f; // scenery can score this high once, but not twice running, so a spot that has is an animal
    private const float WorthFocus = 0.5f;
    private const float SceneryArea = 0.04f; // splashing water lands on a few spots this close together, so they're one piece of scenery
    private const float SureSpot = 0.03f;
    private const float StillSpot = 0.006f; // a rock's box comes back this close every time; a live animal's moves
    private const int StillHits = 6;
    private static readonly TimeSpan StillLasts = TimeSpan.FromSeconds(20); // the same box this long is a thing, even if it once looked sure
    private const float NewSpot = 0.1f;
    private const int SpotHits = 12; // this many hits on one spot within SpotTime makes it scenery
    private const int StartHits = 2;
    private static readonly TimeSpan Between = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan StartWithin = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SpotTime = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan SpotLasts = TimeSpan.FromSeconds(20); // no quicker, or a resting animal looks like scenery
    private static readonly TimeSpan BoxStays = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongestTake = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Rest = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5); // the camera's recording state arrives late
    private static readonly TimeSpan SceneryFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SureFor = TimeSpan.FromMinutes(5); // an animal can sit a long time scoring low
    private static readonly TimeSpan RefusedRest = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FocusGap = TimeSpan.FromSeconds(5);

    private Thread? _thread;
    private volatile bool _run;
    private readonly Queue<DateTime> _sightings = new();
    private DateTime? _took; // when this take began, if we started it
    private bool _rolled; // the camera has shown this take recording
    private DateTime _lastBird;
    private DateTime _restUntil;
    private readonly Queue<(float X, float Y, float W, float H, float Score, DateTime At)> _recent = new();
    private readonly List<(float X, float Y, DateTime Until)> _scenery = [];
    private readonly List<(float X, float Y, DateTime Until)> _animals = [];
    private (float X, float Y)? _lastSure; // the last check, if it was a confident one
    private (float X, float Y)? _focusedOn;
    private DateTime _focused;
    private DateTime _boxShown;

    public event Action<string>? Status;
    public event Action<(double X, double Y, double W, double H)?>? Spotted; // null when there's nothing to show

    public static bool Available => File.Exists(BirdModel.FilePath);
    public bool On => _run;
    public RecordSubject Subject { get; set; } = RecordSubject.All[0];
    public TimeSpan QuietStop { get; set; } = TimeSpan.FromSeconds(15); // nothing seen for this long stops the take
    public bool FocusOnIt { get; set; } = true;

    private string Watching => $"Watching for {Subject.Plural}.";

    public void Start()
    {
        if (_run) return;
        _run = true;
        _sightings.Clear();
        _recent.Clear();
        _scenery.Clear();
        _animals.Clear();
        _lastSure = null;
        _focusedOn = null;
        _thread = new Thread(Loop) { IsBackground = true, Name = "BirdTrigger", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Status?.Invoke(Watching);
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(2000);
        _thread = null;
        if (_took != null) Log.Write("Bird trigger turned off during a take; it keeps recording.");
        _took = null;
        Spotted?.Invoke(null);
    }

    private void Loop()
    {
        BirdModel model;
        try
        {
            model = new BirdModel();
        }
        catch (Exception ex)
        {
            Log.Write("Bird model failed to load: " + ex);
            _run = false;
            ui.Post(() => Status?.Invoke("The animal detector couldn't start."));
            return;
        }
        try
        {
            long last = -1;
            (float X, float Y, float W, float H, DateTime At)? following = null;
            bool slow = false;
            while (_run)
            {
                var began = DateTime.Now;
                if (latest() is { } frame && frame.No != last)
                {
                    last = frame.No;
                    try
                    {
                        int turn = rotation();
                        var seen = model.Look(picture(frame.Jpeg, BirdModel.Size, null).Rgb, turn, Subject);
                        var took = DateTime.Now - began;
                        if ((took > QuickCheck) != slow)
                        {
                            slow = !slow;
                            Log.Write($"An animal check took {took.TotalSeconds:0.00} s, so the closer look is {(slow ? "off" : "back on")}.");
                        }
                        if (following is { } f && began - f.At <= Follow && !slow)
                        {
                            var (rgb, ax, ay, aw, ah) = picture(frame.Jpeg, BirdModel.Size, (f.X, f.Y, f.W, f.H));
                            var close = model.Look(rgb, turn, Subject);
                            if (close.Score > seen.Score) seen = (close.Score, ax + close.X * aw, ay + close.Y * ah, close.W * aw, close.H * ah);
                        }
                        following = seen.Score >= Bar ? (seen.X, seen.Y, seen.W, seen.H, began) : null;
                        var (score, x, y, w, h) = seen;
                        ui.Post(() => Decide(score, x, y, w, h));
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Bird check failed: " + ex.Message);
                        if (model.OnGraphics) // a graphics card that started but then failed, after a driver update say
                        {
                            model.Dispose();
                            try
                            {
                                model = new BirdModel(graphics: false);
                                Log.Write("The animal detector moved to the processor.");
                            }
                            catch (Exception e)
                            {
                                Log.Write("Bird model failed to load: " + e);
                                _run = false;
                                ui.Post(() => Status?.Invoke("The animal detector couldn't start."));
                            }
                        }
                    }
                }
                var rest = Between - (DateTime.Now - began);
                if (rest > TimeSpan.Zero) Thread.Sleep(rest);
            }
        }
        finally
        {
            model.Dispose();
        }
    }

    private void Decide(float score, float x, float y, float w, float h)
    {
        if (!_run) return;
        var now = DateTime.Now;
        bool seen = Counts(score, x, y, w, h, now);
        if (seen) _sightings.Enqueue(now);
        while (_sightings.TryPeek(out var old) && now - old > StartWithin) _sightings.Dequeue();
        Act(score, seen, now);
        if (seen && score >= WorthFocus && FocusOnIt && _took != null && !cam.IsFormatting) FocusOn(x, y, now); // only once recording, so focusing never holds up the record button
        if (seen)
        {
            _boxShown = now;
            Spotted?.Invoke((x, y, w, h));
        }
        else if (now - _boxShown > BoxStays) Spotted?.Invoke(null);
    }

    private void Act(float score, bool seen, DateTime now)
    {
        if (_took is DateTime began)
        {
            if (seen) _lastBird = now;
            _rolled |= cam.IsRecording;
            if (!cam.RecordWaiting && now - cam.RecordSent > Settle && !cam.IsRecording)
            {
                _took = null; // stopped by hand, or the camera never started
                _sightings.Clear();
                if (_rolled)
                {
                    _restUntil = now + Rest; // so a camera that keeps stopping can't be started twice a second
                    Log.Write($"The animal trigger's take ended {(now - began).TotalSeconds:0} s in, by hand or by the camera.");
                    Status?.Invoke(Watching);
                }
                else
                {
                    _restUntil = now + RefusedRest;
                    Log.Write("The camera didn't start recording for the animal trigger.");
                    Status?.Invoke("The camera won't record. Check its card.");
                }
            }
            else if (now - _lastBird >= QuietStop || now - began >= LongestTake)
            {
                if (cam.IsRecording) cam.ToggleRecord();
                _took = null;
                _sightings.Clear();
                _restUntil = now + Rest;
                _focusedOn = null;
                Log.Write($"Animal trigger stopped recording; last sighting {(now - _lastBird).TotalSeconds:0} s ago.");
                Status?.Invoke("Stopped. " + Watching);
            }
            else
            {
                int left = (int)Math.Ceiling((QuietStop - (now - _lastBird)).TotalSeconds);
                Status?.Invoke(seen ? $"{Subject.Name} seen. Recording." : $"Recording. Stops in {left} s if nothing shows up.");
            }
            return;
        }
        if (seen && score >= Subject.StartAt && _sightings.Count >= StartHits && now >= _restUntil
            && cam.Connected && !cam.IsRecording && !cam.IsFormatting && !cam.IsPhotoMode)
        {
            cam.ToggleRecord();
            _took = _lastBird = now;
            _rolled = false;
            Log.Write($"Animal trigger started recording for {Subject.Name} at score {score:0.00}.");
            Status?.Invoke($"{Subject.Name} seen. Recording.");
        }
    }

    /// <summary>Whether a check saw an animal, as opposed to nothing or a hit on a known piece of scenery.</summary>
    private bool Counts(float score, float x, float y, float w, float h, DateTime now)
    {
        while (_recent.TryPeek(out var old) && now - old.At > SpotTime) _recent.Dequeue();
        if (score < Bar)
        {
            _lastSure = null;
            return false;
        }

        var same = _recent.Where(s => Near(s.X, s.Y, x, y, SceneryArea)).ToList();
        _animals.RemoveAll(s => s.Until <= now);
        if (score >= Sure)
        {
            if (_lastSure is { } was && Near(was.X, was.Y, x, y, SureSpot)) _animals.Add((x, y, now + SureFor));
            _lastSure = (x, y);
        }
        else _lastSure = null;
        var still = _recent.Where(s => Near(s.X, s.Y, x, y, StillSpot) && Similar(s.W, w) && Similar(s.H, h)).ToList();
        if (still.Count >= StillHits && now - still[0].At >= StillLasts)
            _animals.RemoveAll(s => Near(s.X, s.Y, x, y, SureSpot)); // it hasn't moved at all, so it isn't an animal, however sure it looked
        bool sure = _animals.Any(s => Near(s.X, s.Y, x, y, SureSpot));
        _recent.Enqueue((x, y, w, h, score, now));
        _scenery.RemoveAll(s => s.Until <= now || (sure && Near(s.X, s.Y, x, y, SceneryArea)));
        for (int i = 0; i < _scenery.Count; i++) // scenery that keeps scoring stays scenery, so a suet cake costs one take, not one every ten minutes
            if (Near(_scenery[i].X, _scenery[i].Y, x, y, SceneryArea)) _scenery[i] = (_scenery[i].X, _scenery[i].Y, now + SceneryFor);
        if (same.Count >= SpotHits && now - same[0].At >= SpotLasts && !sure && !IsScenery(x, y))
        {
            _scenery.Add((x, y, now + SceneryFor));
            Log.Write($"Animal trigger is ignoring hits at {x:0.00}, {y:0.00}.");
        }
        return !IsScenery(x, y);
    }

    private bool IsScenery(float x, float y) => _scenery.Any(s => Near(s.X, s.Y, x, y, SceneryArea));

    private static bool Near(float x1, float y1, float x2, float y2, float by) => Math.Abs(x1 - x2) < by && Math.Abs(y1 - y2) < by;

    private static bool Similar(float a, float b) => Math.Abs(a - b) <= 0.15f * Math.Max(a, b);

    /// <summary>Focuses once on an animal, and again only if it has moved somewhere new.</summary>
    private void FocusOn(float x, float y, DateTime now)
    {
        if (_focusedOn is { } was && (Near(was.X, was.Y, x, y, NewSpot) || now - _focused < FocusGap)) return;
        _focusedOn = (x, y);
        _focused = now;
        focus(x, y);
    }
}
