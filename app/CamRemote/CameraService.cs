using System.Collections.Concurrent;
using System.Text.Json;

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
    private const uint TypeUInt32 = 0x0003, TypeUInt64 = 0x0004, TypeInt16 = 0x1002;
    private const long FocusManual = 1, FocusContinuous = 3;
    private const long TouchOn = 2, TouchFnFocus = 3, TouchFnTracking = 4, TrackingActive = 3;
    private const uint CmdMovieRecord = 1, CmdCancelRemoteTouch = 21;
    private const uint ParamUp = 0, ParamDown = 1;
    private const long PriorityPcRemote = 2;
    private const long LiveViewQualityHigh = 1;

    private readonly IUi _ui;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly Native.EventFn _eventFn; // kept alive for the native side
    private readonly IDisposable _poll;
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

    public CameraService(IUi ui)
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
        _poll = ui.Every(TimeSpan.FromSeconds(2), RequestRefresh);
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Say("Camera error: " + ex.Message); }
    }

    /// <summary>Queues camera work. Anything arriving after the app starts closing is dropped.</summary>
    private void Run(Action a)
    {
        try { _queue.Add(a); }
        catch (InvalidOperationException) { }
    }

    private Task<T> Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        Run(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private void Say(string msg) => _ui.Post(() => Message?.Invoke(msg));

    private void SetState(LinkState s) => _ui.Post(() =>
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

    public Task<int> AddByIpAsync(string ip, string model) => Run(() => Native.cb_make_ip_camera(ip, 1, CameraModels.Find(model)?.Sdk ?? model));

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
            _ui.Post(() =>
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
                _ui.Post(() => Props = new()); // stale values (like "recording") would mislead
                SetState(LinkState.Disconnected);
                Say(a == 0 ? "Disconnected." : "Disconnected: " + Native.ErrorName((int)a));
                break;
            case Native.EvPropertyChanged:
                RequestRefresh();
                break;
            case Native.EvError:
                if (a == 0x8216) _ui.Post(() => HoldReconnect = true);
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
            _ui.Post(() =>
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

    /// <summary>The camera toggles recording on "Down"; Sony asks for "Up" right after.</summary>
    public void ToggleRecord() => Run(() =>
    {
        int err = Native.cb_command(CmdMovieRecord, ParamDown);
        Thread.Sleep(80);
        Native.cb_command(CmdMovieRecord, ParamUp);
        if (err != 0) Say("Record button failed: " + Native.ErrorName(err));
    });

    public bool IsManualFocus => Get(PropCode.FocusMode)?.Value is FocusManual or 6;
    public bool IsTracking => Get(PropCode.FocusTrackingStatus)?.Value == TrackingActive;
    public bool TapTracks => Get(PropCode.FunctionOfTouchOperation)?.Value is TouchFnTracking or 0xA or 0xB;

    /// <summary>
    /// Handles a tap on the picture, given as 0..1. While magnified it moves the magnified area. In manual
    /// focus it focuses once and returns to manual. Otherwise it's the camera's remote touch (movie mode),
    /// or moves the AF frame when the focus area is a spot type.
    /// </summary>
    public string? TouchFocus(double fx, double fy)
    {
        if (MagnifyRatio > 0)
        {
            PanMagnifier(fx, fy);
            return null;
        }
        uint xy = TouchPoint(fx, fy);
        if (IsManualFocus)
        {
            if (Get(PropCode.FocusMode)?.Writable != true) return "The camera isn't letting focus mode change right now.";
            if (Interlocked.CompareExchange(ref _focusOnceBusy, 1, 0) != 0) return "Still focusing on the last tap.";
            long mode = Get(PropCode.FocusMode)!.Value;
            long function = Get(PropCode.FunctionOfTouchOperation)?.Value ?? TouchFnFocus;
            Say("Focusing once, then back to manual…");
            Run(() =>
            {
                EnableTouch(TouchFnFocus);
                SetNow(PropCode.FocusMode, FocusContinuous);
                Thread.Sleep(350); // the camera needs a moment to accept touch after leaving MF
                int err = Native.cb_set_prop((uint)PropCode.RemoteTouchOperation, xy, TypeUInt32);
                Thread.Sleep(1300);
                Native.cb_command(CmdCancelRemoteTouch, ParamDown);
                Thread.Sleep(50);
                Native.cb_command(CmdCancelRemoteTouch, ParamUp);
                SetNow(PropCode.FocusMode, mode);
                if (function != TouchFnFocus) SetNow(PropCode.FunctionOfTouchOperation, function);
                _focusOnceBusy = 0;
                Say(err == 0 ? "Focused. Back in manual." : "Tap focus failed: " + Native.ErrorName(err));
                RequestRefresh();
            });
            return null;
        }
        bool track = TapTracks;
        bool touch = Get(PropCode.RemoteTouchOperationEnableStatus)?.Value == 1 || Get(PropCode.TouchOperation)?.Value != TouchOn;
        if (touch)
        {
            Run(() =>
            {
                EnableTouch(track ? TouchFnTracking : TouchFnFocus);
                int err = Native.cb_set_prop((uint)PropCode.RemoteTouchOperation, xy, TypeUInt32);
                if (err != 0) Say("Touch focus failed: " + Native.ErrorName(err));
                else if (track)
                {
                    Thread.Sleep(1200);
                    _ui.Post(() => RequestRefresh());
                    Thread.Sleep(300);
                    _ui.Post(() =>
                    {
                        if (TapTracks && !IsTracking) Say("The camera focused there but didn't report tracking.");
                    });
                }
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

    private static uint TouchPoint(double fx, double fy)
    {
        uint x = (uint)Math.Clamp(Math.Round(fx * 639), 0, 639);
        uint y = (uint)Math.Clamp(Math.Round(fy * 479), 0, 479);
        return (x << 16) | y;
    }

    /// <summary>Runs on the camera thread. Turns touch on with the given function, if it isn't already.</summary>
    private void EnableTouch(long function)
    {
        if (Get(PropCode.TouchOperation)?.Value != TouchOn) SetNow(PropCode.TouchOperation, TouchOn);
        if (Get(PropCode.FunctionOfTouchOperation)?.Value != function) SetNow(PropCode.FunctionOfTouchOperation, function);
        Thread.Sleep(150);
    }

    private void SetNow(PropCode code, long value)
    {
        if (Get(code) is not { } p) return;
        int err = Native.cb_set_prop((uint)code, (ulong)value, p.Type);
        if (err != 0) Say($"Couldn't set {code}: " + Native.ErrorName(err));
    }

    private int _focusOnceBusy;

    // ---- Focus magnifier ----

    /// <summary>The current ratio x10 (40 is x4.0), or 0 when off.</summary>
    public long MagnifyRatio
    {
        get
        {
            long ratio = ((Get(PropCode.Focus_Magnifier_Setting)?.Value ?? 0) >> 32) & 0xFFFFFFFF;
            return ratio is 0 or 0xFFFFFFFF ? 0 : ratio;
        }
    }

    /// <summary>The ratios the camera lists, or the usual ones if it lists none (the camera has the last word).</summary>
    public List<long> MagnifyChoices()
    {
        var list = (Get(PropCode.Focus_Magnifier_Setting)?.Candidates ?? []).Select(v => (v >> 32) & 0xFFFFFFFF)
            .Where(r => r is > 0 and < 0xFFFFFFFF).Distinct().OrderBy(r => r).ToList();
        return list.Count > 0 ? list : [40, 80];
    }

    /// <summary>Magnifies the centre of the picture at a ratio x10; 0 turns it off.</summary>
    public void Magnify(long ratio) => Run(() =>
    {
        const long Invalid = 0xFFFFFFFF;
        _magX = 320;
        _magY = 240;
        if (ratio > 0)
        {
            // Sony: refresh with an invalid position before the first real one.
            SetMagnifier((ratio << 32) | Invalid);
            Thread.Sleep(200);
            SetMagnifier((ratio << 32) | (320L << 16) | 240);
        }
        else SetMagnifier(Invalid);
        RequestRefresh();
    });

    /// <summary>Moves the magnified area so the tapped spot becomes its centre.</summary>
    private void PanMagnifier(double fx, double fy)
    {
        long ratio = MagnifyRatio;
        double zoom = ratio / 10.0, halfW = 320 / zoom, halfH = 240 / zoom;
        _magX = (int)Math.Clamp(_magX + (fx - 0.5) * 2 * halfW, halfW, 639 - halfW);
        _magY = (int)Math.Clamp(_magY + (fy - 0.5) * 2 * halfH, halfH, 479 - halfH);
        long value = (ratio << 32) | ((long)_magX << 16) | (uint)_magY;
        Run(() => SetMagnifier(value));
    }

    private void SetMagnifier(long value)
    {
        int err = Native.cb_set_prop((uint)PropCode.Focus_Magnifier_Setting, (ulong)value, TypeUInt64);
        if (err != 0) Say("The camera refused the focus magnifier: " + Native.ErrorName(err));
    }

    private int _magX = 320, _magY = 240;

    /// <summary>
    /// Turns the focus ring: negative steps go nearer, positive farther (-7..+7, bigger is a larger move).
    /// Works in MF and DMF. A step still in flight drops new ones so held buttons don't pile up.
    /// </summary>
    public bool FocusStep(int step)
    {
        if (Interlocked.CompareExchange(ref _focusBusy, 1, 0) != 0) return false;
        Run(() =>
        {
            int err = Native.cb_set_prop((uint)PropCode.NearFar, (ulong)(long)Math.Clamp(step, -7, 7), TypeInt16);
            _focusBusy = 0;
            if (err != 0) Say("Focus step failed: " + Native.ErrorName(err));
        });
        return true;
    }

    private int _focusBusy;

    /// <summary>The camera's exposure meter in stops, or null when it isn't sending one.</summary>
    public double? MeterStops
    {
        get
        {
            if (Get(PropCode.MeteredManualLevel) is not { } p) return null;
            int thousandths = (short)p.Value;
            return Math.Abs(thousandths) > 12000 ? null : thousandths / 1000.0;
        }
    }

    /// <summary>Jumps to a native ISO. Returns a message if the camera isn't offering it.</summary>
    public string? SetBaseIso(long want)
    {
        var p = Get(PropCode.IsoSensitivity);
        if (p == null) return "The camera isn't reporting its ISO yet.";
        var choices = p.Choices().Where(v => (v & 0xFFFFFF) == want).ToList();
        if (choices.Count == 0) return $"The camera isn't offering ISO {want} right now (check the shooting mode).";
        // Prefer the plain value over an "expanded" variant with flag bits set.
        Set(PropCode.IsoSensitivity, choices.OrderBy(v => v >> 24).First());
        return null;
    }

    public void CancelTouch() => Run(() =>
    {
        Native.cb_command(CmdCancelRemoteTouch, ParamDown);
        Thread.Sleep(50);
        Native.cb_command(CmdCancelRemoteTouch, ParamUp);
    });

    public void Dispose()
    {
        _poll.Dispose();
        Run(Native.cb_release);
        _queue.CompleteAdding();
        _worker.Join(3000);
    }
}
