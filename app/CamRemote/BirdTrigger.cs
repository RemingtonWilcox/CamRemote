using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CamRemote;

/// <summary>RF-DETR Nano looking for an animal in one small square picture. It runs on this computer only.</summary>
public sealed class BirdModel : IDisposable
{
    public const int Size = 384;
    private const int FirstAnimal = 16, LastAnimal = 25; // the COCO numbers the model uses, bird through giraffe
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly DenseTensor<float> _pixels = new([1, 3, Size, Size]);

    private const string FileName = "rfdetr_nano_fp16.onnx";

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

    public BirdModel()
    {
        OrtEnv.Instance().DisableTelemetryEvents();
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), InterOpNumThreads = 1 };
        _session = new InferenceSession(FilePath, options);
        _input = _session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// The best animal in a picture of RGB bytes, Size by Size: its score from 0 to 1 and its box as 0..1 of that picture.
    /// The model is shown it the way up the viewer sees it, turned by rotation degrees clockwise, because it finds
    /// far more animals upright.
    /// </summary>
    public (float Score, float X, float Y, float W, float H) Look(byte[] rgb, int rotation)
    {
        int last = Size - 1;
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
                for (int c = 0; c < 3; c++) _pixels[0, c, y, x] = (rgb[i + c] / 255f - Mean[c]) / Std[c];
            }
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, _pixels)]);
        var logits = results.First(r => r.Name == "logits").AsTensor<float>();
        var boxes = results.First(r => r.Name == "pred_boxes").AsTensor<float>();
        float best = float.MinValue;
        int found = 0;
        for (int i = 0; i < logits.Dimensions[1]; i++)
            for (int animal = FirstAnimal; animal <= LastAnimal; animal++)
                if (logits[0, i, animal] > best)
                {
                    best = logits[0, i, animal];
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

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Checks the live view twice a second, starts recording after two sightings in three seconds and stops
/// once no animal has been seen for a while. It only ever stops a take it started itself. A confident
/// sighting also focuses there. Sightings that keep landing on one spot without ever being sure are scenery,
/// like splashing water.
/// </summary>
public sealed class BirdTrigger(IUi ui, CameraService cam, Func<(byte[] Jpeg, long No)?> latest, Func<byte[], int, byte[]> shrink,
    Action<double, double> focus, Func<int> rotation)
{
    private const float Bar = 0.25f;
    private const float Sure = 0.6f; // scenery never scores this high, so a spot that has is an animal
    private const float WorthFocus = 0.5f;
    private const float SameSpot = 0.015f;
    private const float SureSpot = 0.03f;
    private const float NewSpot = 0.1f;
    private const int SpotHits = 12; // this many hits on one spot within SpotTime makes it scenery
    private const int StartHits = 2;
    private static readonly TimeSpan Between = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan StartWithin = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SpotTime = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan SpotLasts = TimeSpan.FromSeconds(20); // no quicker, or a resting animal looks like scenery
    private static readonly TimeSpan BoxStays = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietStop = TimeSpan.FromSeconds(15);
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
    private readonly Queue<(float X, float Y, float Score, DateTime At)> _recent = new();
    private readonly List<(float X, float Y, DateTime Until)> _scenery = [];
    private readonly List<(float X, float Y, DateTime Until)> _animals = [];
    private (float X, float Y)? _focusedOn;
    private DateTime _focused;
    private DateTime _boxShown;

    public event Action<string>? Status;
    public event Action<(double X, double Y, double W, double H)?>? Spotted; // null when there's nothing to show

    public static bool Available => File.Exists(BirdModel.FilePath);
    public bool On => _run;

    public void Start()
    {
        if (_run) return;
        _run = true;
        _sightings.Clear();
        _recent.Clear();
        _scenery.Clear();
        _animals.Clear();
        _focusedOn = null;
        _thread = new Thread(Loop) { IsBackground = true, Name = "BirdTrigger", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Status?.Invoke("Watching for animals.");
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
        using (model)
        {
            long last = -1;
            while (_run)
            {
                var began = DateTime.Now;
                if (latest() is { } frame && frame.No != last)
                {
                    last = frame.No;
                    try
                    {
                        var (score, x, y, w, h) = model.Look(shrink(frame.Jpeg, BirdModel.Size), rotation());
                        ui.Post(() => Decide(score, x, y, w, h));
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Bird check failed: " + ex.Message);
                    }
                }
                var rest = Between - (DateTime.Now - began);
                if (rest > TimeSpan.Zero) Thread.Sleep(rest);
            }
        }
    }

    private void Decide(float score, float x, float y, float w, float h)
    {
        if (!_run) return;
        var now = DateTime.Now;
        bool seen = Counts(score, x, y, now);
        if (seen) _sightings.Enqueue(now);
        while (_sightings.TryPeek(out var old) && now - old > StartWithin) _sightings.Dequeue();
        Act(score, seen, now);
        if (seen && score >= WorthFocus) FocusOn(x, y, now); // after Act, so focusing never holds up the record button
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
            if (now - began > Settle && !cam.IsRecording)
            {
                _took = null; // stopped by hand, or the camera never started
                _sightings.Clear();
                if (_rolled) Status?.Invoke("Watching for animals.");
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
                Status?.Invoke("Stopped. Watching for animals.");
            }
            else
            {
                int left = (int)Math.Ceiling((QuietStop - (now - _lastBird)).TotalSeconds);
                Status?.Invoke(seen ? "Animal seen. Recording." : $"Recording. Stops in {left} s if nothing shows up.");
            }
            return;
        }
        if (seen && _sightings.Count >= StartHits && now >= _restUntil
            && cam.State == LinkState.Connected && !cam.IsRecording)
        {
            cam.ToggleRecord();
            _took = _lastBird = now;
            _rolled = false;
            Log.Write($"Animal trigger started recording at score {score:0.00}.");
            Status?.Invoke("Animal seen. Recording.");
        }
    }

    /// <summary>Whether a check saw an animal, as opposed to nothing or a hit on a known piece of scenery.</summary>
    private bool Counts(float score, float x, float y, DateTime now)
    {
        while (_recent.TryPeek(out var old) && now - old.At > SpotTime) _recent.Dequeue();
        if (score < Bar) return false;

        var same = _recent.Where(s => Near(s.X, s.Y, x, y, SameSpot)).ToList();
        _animals.RemoveAll(s => s.Until <= now);
        if (score >= Sure) _animals.Add((x, y, now + SureFor));
        bool sure = _animals.Any(s => Near(s.X, s.Y, x, y, SureSpot));
        _recent.Enqueue((x, y, score, now));
        _scenery.RemoveAll(s => s.Until <= now || (sure && Near(s.X, s.Y, x, y, SureSpot)));
        if (same.Count >= SpotHits && now - same[0].At >= SpotLasts && !sure && !IsScenery(x, y))
        {
            _scenery.Add((x, y, now + SceneryFor));
            Log.Write($"Animal trigger is ignoring hits at {x:0.00}, {y:0.00}.");
        }
        return !IsScenery(x, y);
    }

    private bool IsScenery(float x, float y) => _scenery.Any(s => Near(s.X, s.Y, x, y, SameSpot));

    private static bool Near(float x1, float y1, float x2, float y2, float by) => Math.Abs(x1 - x2) < by && Math.Abs(y1 - y2) < by;

    /// <summary>Focuses once on an animal, and again only if it has moved somewhere new.</summary>
    private void FocusOn(float x, float y, DateTime now)
    {
        if (_focusedOn is { } was && (Near(was.X, was.Y, x, y, NewSpot) || now - _focused < FocusGap)) return;
        _focusedOn = (x, y);
        _focused = now;
        focus(x, y);
    }
}
