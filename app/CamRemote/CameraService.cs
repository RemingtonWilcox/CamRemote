using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Threading;

namespace CamRemote;

public sealed class Prop
{
    public uint Code { get; init; }
    public uint Type { get; init; }
    public int Enable { get; init; }
    public bool Writable { get; init; }
    public long Value { get; init; }
    public string? Str { get; init; }
    public long[] Candidates { get; init; } = [];

    public bool IsRange => (Type & 0x4000) != 0;

    /// <summary>The values a user can pick: the candidate list, or every step of a min/max/step range.</summary>
    public IEnumerable<long> Choices()
    {
        if (!IsRange) return Candidates;
        if (Candidates.Length < 3 || Candidates[2] <= 0) return [];
        long min = Candidates[0], max = Candidates[1], step = Candidates[2];
        if ((max - min) / step > 2000) return [];
        var list = new List<long>();
        for (long v = min; v <= max; v += step) list.Add(v);
        return list;
    }
}

public sealed record FoundCamera(int Index, string Model, string Conn, string Ip, string Mac, bool Ssh)
{
    public override string ToString() => Conn == "IP" ? $"{Model}  —  Wi-Fi {Ip}" : $"{Model}  —  {Conn}";
}

public enum LinkState { Disconnected, Connecting, Connected }

/// <summary>
/// Owns the SDK session. Every SDK call runs on one worker thread, in order; results and
/// camera events are delivered on the UI thread.
/// </summary>
public sealed class CameraService : IDisposable
{
    private const uint TypeUInt32 = 0x0003, TypeInt16 = 0x1002;
    private const uint CmdMovieRecord = 1, CmdCancelRemoteTouch = 21;
    private const uint ParamUp = 0, ParamDown = 1;
    private const long PriorityPcRemote = 2;
    private const long LiveViewQualityHigh = 1;

    private readonly Dispatcher _ui;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly Native.EventFn _eventFn; // kept alive for the native side
    private readonly DispatcherTimer _poll;
    private bool _refreshQueued;

    public Dictionary<uint, Prop> Props { get; private set; } = new();
    public LinkState State { get; private set; } = LinkState.Disconnected;

    /// <summary>
    /// Set when automatic reconnecting should pause until the user connects by hand: after they
    /// disconnect, after a rejected password, or after they decline a changed fingerprint.
    /// </summary>
    public bool HoldReconnect { get; set; }

    public event Action? PropsChanged;
    public event Action? StateChanged;
    public event Action<string>? Message;

    public CameraService(Dispatcher ui)
    {
        _ui = ui;
        _eventFn = OnNativeEvent;
        _worker = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) Safe(a); })
        { IsBackground = true, Name = "CameraSDK" };
        _worker.Start();
        Run(() =>
        {
            if (Native.cb_init(_eventFn) != 0) Say("Sony SDK failed to start.");
        });
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RequestRefresh(), ui);
        _poll.Start();
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Say("Camera error: " + ex.Message); }
    }

    private void Run(Action a) => _queue.Add(a);

    private Task<T> Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        _queue.Add(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private void Say(string msg) => _ui.BeginInvoke(() => Message?.Invoke(msg));

    private void SetState(LinkState s) => _ui.BeginInvoke(() =>
    {
        State = s;
        StateChanged?.Invoke();
    });

    // ---- Connection ----

    public Task<List<FoundCamera>> DiscoverAsync() => Run(() =>
    {
        var json = Native.Enumerate(3);
        var list = new List<FoundCamera>();
        if (json == null) return list;
        foreach (var e in JsonDocument.Parse(json).RootElement.EnumerateArray())
            list.Add(new FoundCamera(e.GetProperty("index").GetInt32(), e.GetProperty("model").GetString() ?? "",
                e.GetProperty("conn").GetString() ?? "", e.GetProperty("ip").GetString() ?? "",
                e.GetProperty("mac").GetString() ?? "", e.GetProperty("ssh").GetBoolean()));
        return list;
    });

    /// <summary>Reads the camera's SSH fingerprint. Index -1 means a camera added by IP.</summary>
    public Task<string?> FingerprintAsync(int index) => Run(() => Native.Fingerprint(index));

    public Task<int> AddByIpAsync(string ip) => Run(() => Native.cb_make_ip_camera(ip, 1));

    public void Connect(int index, string user, string password, string fingerprint)
    {
        SetState(LinkState.Connecting);
        Run(() =>
        {
            int err = Native.cb_connect(index, user, password, fingerprint);
            if (err != 0)
            {
                Say("Connect failed: " + Native.ErrorName(err));
                SetState(LinkState.Disconnected);
            }
        });
    }

    public void Disconnect()
    {
        HoldReconnect = true;
        Run(() =>
        {
            Native.cb_disconnect();
            _ui.BeginInvoke(() =>
            {
                Props = new();
                PropsChanged?.Invoke();
            });
            SetState(LinkState.Disconnected);
        });
    }

    private void OnNativeEvent(int kind, uint a, uint b)
    {
        switch (kind)
        {
            case Native.EvConnected:
                SetState(LinkState.Connected);
                Say("Connected.");
                Run(() =>
                {
                    // Let the app, not the camera's dials, own exposure settings while connected.
                    Native.cb_set_prop((uint)PropCode.PriorityKeySettings, PriorityPcRemote, 0x2002);
                    // Ask for the sharper live-view stream; harmless if the camera ignores it.
                    Native.cb_set_prop((uint)PropCode.LiveView_Image_Quality, LiveViewQualityHigh, 0x2002);
                });
                RequestRefresh();
                break;
            case Native.EvDisconnected:
                _ui.BeginInvoke(() => Props = new()); // stale values (like "recording") would mislead
                SetState(LinkState.Disconnected);
                Say(a == 0 ? "Disconnected." : "Disconnected: " + Native.ErrorName((int)a));
                break;
            case Native.EvPropertyChanged:
                RequestRefresh();
                break;
            case Native.EvError:
                if (a == 0x8216) _ui.BeginInvoke(() => HoldReconnect = true);
                Say(a == 0x8216
                    ? "The camera rejected the user or password. Check MENU › Network › Network Option › Access Authen. Info (case-sensitive)."
                    : "Camera error: " + Native.ErrorName((int)a));
                if (State == LinkState.Connecting) SetState(LinkState.Disconnected);
                break;
            case Native.EvWarning:
                // 0x6xxxx are routine status notices (AF status and the like), not problems.
                if (a is < 0x60000 or >= 0x70000) Say("Camera: " + Native.ErrorName((int)a));
                break;
        }
    }

    // ---- Properties ----

    /// <summary>Re-reads all properties, coalescing bursts of change events into one read.</summary>
    public void RequestRefresh()
    {
        if (State == LinkState.Disconnected || _refreshQueued) return;
        _refreshQueued = true;
        Run(() =>
        {
            _refreshQueued = false;
            var json = Native.GetProps(null);
            if (json == null) return;
            var map = new Dictionary<uint, Prop>();
            foreach (var e in JsonDocument.Parse(json).RootElement.EnumerateArray())
            {
                var p = new Prop
                {
                    Code = e.GetProperty("c").GetUInt32(),
                    Type = e.GetProperty("t").GetUInt32(),
                    Enable = e.GetProperty("e").GetInt32(),
                    Writable = e.GetProperty("w").GetBoolean(),
                    Value = (long)e.GetProperty("v").GetUInt64(),
                    Str = e.TryGetProperty("s", out var s) ? s.GetString() : null,
                    Candidates = e.GetProperty("p").EnumerateArray().Select(x => x.GetInt64()).ToArray(),
                };
                map[p.Code] = p;
            }
            _ui.BeginInvoke(() =>
            {
                Props = map;
                PropsChanged?.Invoke();
            });
        });
    }

    public Prop? Get(PropCode code) => Props.GetValueOrDefault((uint)code);

    public void Set(PropCode code, long value)
    {
        var p = Get(code);
        if (p == null) return;
        Run(() =>
        {
            int err = Native.cb_set_prop((uint)code, (ulong)value, p.Type);
            if (err != 0) Say($"Couldn't set {code}: " + Native.ErrorName(err));
        });
        RequestRefresh();
    }

    // ---- Actions ----

    public bool IsRecording => Get(PropCode.RecordingState)?.Value == 1;

    /// <summary>The A7S III toggles recording on "Down"; Sony asks for "Up" right after.</summary>
    public void ToggleRecord() => Run(() =>
    {
        int err = Native.cb_command(CmdMovieRecord, ParamDown);
        Thread.Sleep(80);
        Native.cb_command(CmdMovieRecord, ParamUp);
        if (err != 0) Say("Record button failed: " + Native.ErrorName(err));
    });

    /// <summary>
    /// Focus on a point given as 0..1 of the picture. Uses the camera's remote touch (movie mode),
    /// else moves the AF frame when the focus area is a spot type.
    /// </summary>
    public string? TouchFocus(double fx, double fy)
    {
        uint x = (uint)Math.Clamp(Math.Round(fx * 639), 0, 639);
        uint y = (uint)Math.Clamp(Math.Round(fy * 479), 0, 479);
        uint xy = (x << 16) | y;
        if (Get(PropCode.RemoteTouchOperationEnableStatus)?.Value == 1)
        {
            Run(() =>
            {
                int err = Native.cb_set_prop((uint)PropCode.RemoteTouchOperation, xy, TypeUInt32);
                if (err != 0) Say("Touch focus failed: " + Native.ErrorName(err));
            });
            return null;
        }
        if (Get(PropCode.AF_Area_Position) is { Writable: true })
        {
            Run(() =>
            {
                int err = Native.cb_set_prop((uint)PropCode.AF_Area_Position, xy, TypeUInt32);
                if (err != 0) Say("Moving the AF frame failed: " + Native.ErrorName(err));
            });
            return null;
        }
        return "The camera isn't accepting touch focus right now. Put it in movie mode with Touch Operation on, " +
               "or set Focus Area to a Spot type.";
    }

    /// <summary>
    /// Turns the focus ring: negative steps go nearer, positive farther (-7..+7, bigger is a larger move).
    /// Works in MF and DMF. A step still in flight drops new ones so held buttons don't pile up.
    /// </summary>
    public void FocusStep(int step)
    {
        if (Interlocked.CompareExchange(ref _focusBusy, 1, 0) != 0) return;
        Run(() =>
        {
            int err = Native.cb_set_prop((uint)PropCode.NearFar, (ulong)(long)Math.Clamp(step, -7, 7), TypeInt16);
            _focusBusy = 0;
            if (err != 0) Say("Focus step failed: " + Native.ErrorName(err));
        });
    }

    private int _focusBusy;

    public void CancelTouch() => Run(() =>
    {
        Native.cb_command(CmdCancelRemoteTouch, ParamDown);
        Thread.Sleep(50);
        Native.cb_command(CmdCancelRemoteTouch, ParamUp);
    });

    public void Dispose()
    {
        _poll.Stop();
        Run(Native.cb_release);
        _queue.CompleteAdding();
        _worker.Join(3000);
    }
}
