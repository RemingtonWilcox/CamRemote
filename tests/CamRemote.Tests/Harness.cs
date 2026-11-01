using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace CamRemote.Tests;

internal static class TestSetup
{
    /// <summary>Keeps every test's settings, log and "saved camera" in a throwaway folder, never the real one.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CamRemoteTests-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        Settings.FolderOverride = dir;
    }
}

/// <summary>
/// A stand-in for the window's UI thread: one real thread running posted work in order. Timers from
/// <see cref="Every"/> never fire on their own; tests call <see cref="Tick"/> so nothing depends on timing.
/// </summary>
public sealed class TestUi : IUi, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly List<Action> _timers = new();
    public readonly ConcurrentQueue<Exception> Errors = new();

    public TestUi()
    {
        _thread = new Thread(() =>
        {
            foreach (var a in _queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Errors.Enqueue(ex); }
            }
        }) { IsBackground = true, Name = "TestUi" };
        _thread.Start();
    }

    public bool OnUiThread => Thread.CurrentThread == _thread;

    public void Post(Action action)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { } // closed, like a window's dispatcher after it shuts down
    }

    public T Invoke<T>(Func<T> action)
    {
        if (OnUiThread) return action();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The UI thread didn't answer.");
        return tcs.Task.Result;
    }

    public void Invoke(Action action) => Invoke(() => { action(); return 0; });

    public IDisposable Every(TimeSpan interval, Action action)
    {
        lock (_timers) _timers.Add(action);
        return new Stop(() => { lock (_timers) _timers.Remove(action); });
    }

    /// <summary>Runs every timer once, on the UI thread.</summary>
    public void Tick()
    {
        Action[] all;
        lock (_timers) all = _timers.ToArray();
        Invoke(() => { foreach (var a in all) a(); });
    }

    /// <summary>Waits until everything posted so far has run.</summary>
    public void Drain() => Invoke(() => 0);

    public void Dispose() => _queue.CompleteAdding();

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

/// <summary>One property as the fake camera reports it.</summary>
public sealed class FakeProp
{
    public uint Type = 0x0002; // UInt16
    public int Enable = 1;
    public bool Writable = true;
    public long Value;
    public long[] Candidates = [];
    public bool Ignores; // accepts a new value but keeps the old one, like a camera whose dials have control
}

/// <summary>
/// A pretend camera behind the bridge. It answers the way CamBridge does (same JSON, same return codes),
/// records every call, and can be told to fail. Connection events arrive on another thread, as the SDK's do.
/// </summary>
internal sealed class FakeCamera : ICameraBridge
{
    public const uint CmdMovieRecord = 1, CmdCancelRemoteTouch = 12;

    private Native.EventFn? _event;
    private readonly object _gate = new();
    public readonly Dictionary<PropCode, FakeProp> Props = new();
    public readonly List<string> Calls = new();
    public readonly List<(PropCode Code, long Value)> Sets = new();
    public readonly List<(uint Command, uint Param)> Commands = new();

    public string EnumerateJson = "[]";
    public string? FingerprintText = "SHA256:fake";
    public int ConnectResult;
    public bool ConnectFiresEvent = true;
    public int GetPropsFailures; // how many reads in a row return nothing
    public int SetPropResult;
    public int CommandResult;
    public Exception? CommandThrows; // as if the bridge itself failed (a P/Invoke or marshalling error)
    public string? FramesJson;
    public bool Connected { get; private set; }
    public int Releases, Disconnects;

    /// <summary>While set, every call waits here: lets a test queue work up behind a busy camera.</summary>
    public readonly ManualResetEventSlim Open = new(true);

    public FakeCamera()
    {
        Set(PropCode.PriorityKeySettings, 2, type: 0x0002);
        Set(PropCode.RecordingState, 0);
    }

    public FakeProp Set(PropCode code, long value, uint type = 0x0002, long[]? candidates = null, bool writable = true)
    {
        lock (_gate)
        {
            if (!Props.TryGetValue(code, out var p)) Props[code] = p = new FakeProp();
            p.Value = value;
            p.Type = type;
            p.Writable = writable;
            if (candidates != null) p.Candidates = candidates;
            return p;
        }
    }

    public long Value(PropCode code)
    {
        lock (_gate) return Props[code].Value;
    }

    private void Note(string call)
    {
        Open.Wait(TimeSpan.FromSeconds(10));
        lock (_gate) Calls.Add(call);
    }

    public void Fire(int kind, uint a = 0, uint b = 0) => _event?.Invoke(kind, a, b);

    public int Init(Native.EventFn onEvent) { _event = onEvent; Note("Init"); return 0; }
    public void Release() { Note("Release"); Releases++; }
    public string? Enumerate(int timeoutSec) { Note("Enumerate"); return EnumerateJson; }
    public int MakeIpCamera(string ip, bool ssh, string model) { Note($"MakeIpCamera {ip} {model}"); return 0; }
    public string? Fingerprint(int index) { Note($"Fingerprint {index}"); return FingerprintText; }

    public int Connect(int index, string user, string password, string fingerprint)
    {
        Note($"Connect {index} {user} {fingerprint}");
        if (ConnectResult != 0) return ConnectResult;
        if (ConnectFiresEvent)
            ThreadPool.QueueUserWorkItem(_ => { Connected = true; Fire(Native.EvConnected); });
        return 0;
    }

    public int Disconnect() { Note("Disconnect"); Disconnects++; Connected = false; return 0; }

    public string? GetProps(uint[]? codes)
    {
        Note(codes == null ? "GetProps" : $"GetProps {string.Join(',', codes)}");
        lock (_gate)
        {
            if (GetPropsFailures > 0) { GetPropsFailures--; return null; }
            var sb = new StringBuilder("[");
            foreach (var (code, p) in Props)
            {
                if (codes != null && !codes.Contains((uint)code)) continue;
                if (sb.Length > 1) sb.Append(',');
                sb.Append($"{{\"c\":{(uint)code},\"t\":{p.Type},\"e\":{p.Enable},\"w\":{(p.Writable ? "true" : "false")},\"v\":{(ulong)p.Value},\"p\":[{string.Join(',', p.Candidates)}]}}");
            }
            return sb.Append(']').ToString();
        }
    }

    public int SetProp(uint code, ulong value, uint dataType)
    {
        Note($"SetProp {(PropCode)code} {(long)value}");
        lock (_gate)
        {
            Sets.Add(((PropCode)code, (long)value));
            if (SetPropResult != 0) return SetPropResult;
            if (Props.TryGetValue((PropCode)code, out var p) && !p.Ignores) p.Value = (long)value;
        }
        return 0;
    }

    public int Command(uint command, uint param)
    {
        Note($"Command {command} {param}");
        lock (_gate)
        {
            Commands.Add((command, param));
            if (CommandThrows != null) throw CommandThrows;
            if (CommandResult != 0) return CommandResult;
            // Like the camera: "Down" on the record button starts or stops a take.
            if (command == CmdMovieRecord && param == 1 && Props.TryGetValue(PropCode.RecordingState, out var rec))
                rec.Value = rec.Value == 1 ? 0 : 1;
        }
        return 0;
    }

    public int QueueCount() => 0;

    public string? FocusFrames() => FramesJson;

    public List<(PropCode Code, long Value)> SetsOf(PropCode code)
    {
        lock (_gate) return Sets.Where(s => s.Code == code).ToList();
    }
}

/// <summary>A CameraService on a fake camera, with helpers to wait for its two threads.</summary>
internal sealed class Rig : IDisposable
{
    public readonly TestUi Ui = new();
    public readonly FakeCamera Camera = new();
    public readonly CameraService Cam;
    public readonly List<string> Messages = new();

    public Rig()
    {
        Cam = new CameraService(Ui, Camera);
        Cam.Message += m => { lock (Messages) Messages.Add(m); };
    }

    /// <summary>Waits until the camera thread and then the UI thread have finished what's queued.</summary>
    public void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Cam.FingerprintAsync(-99).Wait(TimeSpan.FromSeconds(10)); // anything queued before this has run
            Ui.Drain();
        }
    }

    /// <summary>Connects and reads the settings once, as the app does.</summary>
    /// <summary>Makes the camera thread wait inside a call until <see cref="FakeCamera.Open"/> is set again.</summary>
    public void Jam()
    {
        Camera.Open.Reset();
        _ = Cam.FingerprintAsync(-98);
        Thread.Sleep(50);
    }

    public void ConnectAndRead()
    {
        Ui.Invoke(() => Cam.Connect(0, "admin", "pw", "fp"));
        WaitFor(() => Ui.Invoke(() => Cam.State) == LinkState.Connected);
        Refresh();
    }

    public void Refresh()
    {
        Settle();
        Ui.Invoke(Cam.RequestRefresh);
        Settle();
    }

    public static void WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Gave up waiting.");
            Thread.Sleep(10);
        }
    }

    public bool Said(string part)
    {
        Ui.Drain();
        lock (Messages) return Messages.Any(m => m.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        Ui.Invoke(Cam.Dispose);
        Ui.Dispose();
    }
}
