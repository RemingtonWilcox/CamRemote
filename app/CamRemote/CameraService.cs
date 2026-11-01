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

public enum FrameKind { Focus = 1, Face = 2, Tracking = 3 }

/// <summary>A box the camera is drawing on its own screen. X and Y are its centre, and all four run 0..1 across the picture.</summary>
public sealed record FocusFrame(FrameKind Kind, bool Focused, double X, double Y, double W, double H);

/// <summary>
/// Owns the SDK session. Every SDK call runs on one worker thread, in order; results and
/// camera events are delivered on the UI thread.
/// </summary>
public sealed class CameraService : IDisposable
{
    private const uint TypeUInt32 = 0x0003, TypeUInt64 = 0x0004, TypeInt16 = 0x1002;
    private const long FocusManual = 1, FocusContinuous = 3;
    private const int FrameFocused = 2;
    private const long TouchOn = 2, TouchFnOff = 1, TouchFnFocus = 3, TouchFnTracking = 4, TrackingActive = 3;
    private const uint CmdMovieRecord = 1, CmdCancelRemoteTouch = 12;
    private const uint ParamUp = 0, ParamDown = 1;
    private const long PriorityPcRemote = 2;
    private const long LiveViewQualityHigh = 1;
    private const int ControlTries = 5; // about 10 s at the 2 s poll
    private const int RefreshTries = 5;
    private const int FocusQuietMs = 700;
    private const double RingPixelsPerStep = 12;
    private const int FrameHoldMs = 500;

    private readonly IUi _ui;
    private readonly ICameraBridge _bridge;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly Native.EventFn _eventFn; // kept alive for the native side
    private readonly IDisposable _poll;
    private readonly IDisposable _quickPoll;
    private readonly Thread _focusThread, _frameThread;
    private readonly AutoResetEvent _focusWake = new(false), _frameWake = new(false);
    private volatile bool _closing;
    private volatile bool _propsDirty;
    private bool _refreshQueued;
    private int _controlMisses, _refreshFails;
    private bool _hadControl, _restartedLink;

    public Dictionary<uint, Prop> Props { get; private set; } = new();
    public LinkState State { get; private set; } = LinkState.Disconnected;

    /// <summary>
    /// Set when automatic reconnecting should pause until the user connects by hand: after they
    /// disconnect, after a rejected password, or after they decline a changed fingerprint.
    /// </summary>
    public bool HoldReconnect { get; set; }

    public event Action? PropsChanged;
    public event Action? StateChanged;
    public event Action? FramesChanged;
    public event Action? FocusMoved;
    public event Action<string>? Message;

    public CameraService(IUi ui) : this(ui, null) { }

    /// <summary>Tests pass a fake camera here; the app uses the real bridge.</summary>
    internal CameraService(IUi ui, ICameraBridge? bridge)
    {
        _ui = ui;
        _bridge = bridge ?? NativeBridge.Instance;
        _eventFn = OnNativeEvent;
        _worker = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) Safe(a); })
        { IsBackground = true, Name = "CameraSDK" };
        _worker.Start();
        Run(() =>
        {
            if (_bridge.Init(_eventFn) != 0) Say("Sony SDK failed to start.");
        });
        _poll = ui.Every(TimeSpan.FromSeconds(2), () =>
        {
            if (!Focusing) RequestRefresh();
        });
        _quickPoll = ui.Every(TimeSpan.FromMilliseconds(300), () =>
        {
            if (Focusing) ReadFocusPosition();
            if (!_propsDirty || Focusing) return;
            _propsDirty = false;
            RequestRefresh();
        });
        _focusThread = new Thread(FocusLoop) { IsBackground = true, Name = "CameraFocus" };
        _focusThread.Start();
        _frameThread = new Thread(FrameLoop) { IsBackground = true, Name = "CameraFrames" };
        _frameThread.Start();
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            Log.Write("Camera error: " + ex);
            Say("Something went wrong talking to the camera.");
        }
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

    private void Say(string msg)
    {
        Log.Write(msg);
        _ui.Post(() => Message?.Invoke(msg));
    }

    /// <summary>Shows plain words, and keeps Sony's exact error in the log.</summary>
    private void SayFailed(string msg, int err)
    {
        Log.Write($"{msg} {Native.ErrorName(err)}");
        _ui.Post(() => Message?.Invoke(msg));
    }

    /// <summary>What a Sony connection error means for the person using the app.</summary>
    private static string Explain(int err) => Math.Abs((long)err) switch
    {
        0x8202 => "Couldn't reach the camera. It may be off, or another app like Imaging Edge or Monitor & Control may be using it.",
        0x820B or 0x8210 => "Another app is using the camera. Close Imaging Edge or Monitor & Control, then CamRemote can connect.",
        0x820A => "The camera refused the connection. Check that Remote Shooting is on.",
        0x8207 or 0x8208 or 0x8209 => "Lost contact with the camera. Check that it's on and on the same Wi-Fi.",
        0x8212 or 0x8213 or 0x8214 or 0x8215 => "Couldn't make a secure connection to the camera. Turn it off and on, then try again.",
        _ => $"Something went wrong talking to the camera (error 0x{Math.Abs((long)err):X}).",
    };

    /// <summary>The camera notices worth interrupting someone for. The rest only go in the log.</summary>
    private static string? ExplainNotice(uint notice) => notice switch
    {
        0x20002 => "Lost the camera for a moment. Reconnecting…",
        0x20001 => "Back in touch with the camera.",
        0x20003 => "The camera's memory card is full.",
        0x20007 => "The Wi-Fi connection to the camera is having trouble.",
        0x20008 => "The Wi-Fi connection to the camera is back.",
        0x2006A => "The camera couldn't start or stop recording.",
        _ => null,
    };

    private void SetState(LinkState s) => _ui.Post(() =>
    {
        if (State != s) Log.Write($"Link: {State} -> {s}");
        State = s;
        StateChanged?.Invoke();
    });

    // ---- Connection ----

    public Task<List<FoundCamera>> DiscoverAsync() => Run(() =>
    {
        var json = _bridge.Enumerate(3);
        var list = new List<FoundCamera>();
        if (json == null) return list;
        foreach (var e in JsonDocument.Parse(json).RootElement.EnumerateArray())
            list.Add(new FoundCamera(e.GetProperty("index").GetInt32(), e.GetProperty("model").GetString() ?? "",
                e.GetProperty("conn").GetString() ?? "", e.GetProperty("ip").GetString() ?? "",
                e.GetProperty("mac").GetString() ?? "", e.GetProperty("ssh").GetBoolean()));
        return list;
    });

    /// <summary>Reads the camera's SSH fingerprint. Index -1 means a camera added by IP.</summary>
    public Task<string?> FingerprintAsync(int index) => Run(() => _bridge.Fingerprint(index));

    public Task<int> AddByIpAsync(string ip, string model) => Run(() => _bridge.MakeIpCamera(ip, true, CameraModels.Find(model)?.Sdk ?? model));

    public void Connect(int index, string user, string password, string fingerprint)
    {
        SetState(LinkState.Connecting);
        Run(() =>
        {
            int err = _bridge.Connect(index, user, password, fingerprint);
            if (err != 0)
            {
                SayFailed(Explain(err), err);
                SetState(LinkState.Disconnected);
            }
        });
    }

    public void Disconnect()
    {
        HoldReconnect = true;
        Run(() =>
        {
            _bridge.Disconnect();
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
                    _controlMisses = _refreshFails = 0;
                    TakeControl();
                });
                _propsDirty = true;
                _frameWake.Set();
                break;
            case Native.EvDisconnected:
                _ui.Post(() => Props = new()); // stale values (like "recording") would mislead
                SetState(LinkState.Disconnected);
                if (a == 0) Say("Disconnected.");
                else SayFailed("Disconnected. " + Explain((int)a), (int)a);
                break;
            case Native.EvPropertyChanged:
                _propsDirty = true;
                break;
            case Native.EvLiveViewPropsChanged:
                _frameWake.Set();
                break;
            case Native.EvError:
                Log.Write("Camera error event: " + Native.ErrorName((int)a));
                if (a == 0x8216) _ui.Post(() => HoldReconnect = true);
                if (a == 0x8216) Say("The camera rejected the user or password. Check MENU › Network › Network Option › Access Authen. Info (case-sensitive).");
                else SayFailed(Explain((int)a), (int)a);
                if (State == LinkState.Connecting) SetState(LinkState.Disconnected);
                break;
            case Native.EvWarning:
                if (a != _lastWarning)
                {
                    Log.Write("Camera notice: " + Native.ErrorName((int)a));
                    if (ExplainNotice(a) is { } notice) Say(notice);
                }
                _lastWarning = a;
                _propsDirty = true; // a resumed session sends no connect event of its own on every camera
                break;
        }
    }

    private uint _lastWarning;
    private readonly ConcurrentDictionary<PropCode, long> _latestSet = new();
    private const string RefusedSetting = "The camera didn't accept that setting. It may be locked in this mode or while recording.";

    /// <summary>
    /// Runs on the camera thread. Asks for control of the settings. The camera hands control back to its dials
    /// whenever a session closes, so this has to happen again after a power cycle. Sony: "set to 0x00 when the session is closed".
    /// </summary>
    private void TakeControl()
    {
        int err = _bridge.SetProp((uint)PropCode.PriorityKeySettings, PriorityPcRemote, 0x2002);
        Log.Write(err == 0 ? "Asked the camera for control of its settings." : "Asking for control failed: " + Native.ErrorName(err));
        // Ask for the sharper live-view stream; harmless if the camera ignores it.
        _bridge.SetProp((uint)PropCode.LiveView_Image_Quality, LiveViewQualityHigh, 0x2002);
    }

    /// <summary>
    /// Runs on the camera thread after each read, and repairs a link that shows a picture but ignores settings.
    /// Returns false if it restarted the link, which makes what was just read out of date.
    /// </summary>
    private bool CheckControl(Dictionary<uint, Prop> props)
    {
        _refreshFails = 0;
        if (!props.TryGetValue((uint)PropCode.PriorityKeySettings, out var key)) return true;
        if (key.Value == PriorityPcRemote)
        {
            if (_controlMisses > 0) Log.Write("The camera gave control of its settings back.");
            _controlMisses = 0;
            if (!_hadControl) LogFocusOptions(props);
            _hadControl = true;
            _restartedLink = false;
            return true;
        }
        _controlMisses++;
        Log.Write($"The camera's dials have control (value {key.Value}, writable {key.Writable}), try {_controlMisses}.");
        if (_controlMisses < ControlTries) TakeControl();
        else if (_controlMisses == ControlTries && _hadControl) return !RestartLink("The camera didn't hand back control of its settings.");
        return true;
    }

    private static void LogFocusOptions(Dictionary<uint, Prop> props)
    {
        foreach (var code in new[] { PropCode.FocusMode, PropCode.RecognitionTarget, PropCode.SubjectRecognitionInAF, PropCode.RightLeftEyeSelect, PropCode.Focus_Magnifier_Setting,
                     PropCode.FocusPositionCurrentValue, PropCode.FocusPositionSetting, PropCode.FocalDistanceInMeter })
            Log.Write(props.TryGetValue((uint)code, out var p)
                ? $"{code}: value 0x{p.Value:X}, writable {p.Writable}, choices {string.Join(' ', p.Candidates.Select(v => v.ToString("X")))}"
                : $"{code}: not reported");
    }

    /// <summary>Runs on the camera thread. Does what Disconnect then Connect does by hand. Recording carries on in the camera.</summary>
    private bool RestartLink(string why)
    {
        if (_restartedLink)
        {
            Say(why + " Press Disconnect, then Connect.");
            return false;
        }
        _restartedLink = true;
        Say(why + " Reconnecting…");
        _bridge.Disconnect();
        _ui.Post(() =>
        {
            Props = new();
            PropsChanged?.Invoke();
        });
        SetState(LinkState.Disconnected);
        return true;
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
            var json = _bridge.GetProps(null);
            if (json == null)
            {
                Log.Write($"Couldn't read the camera's settings, try {++_refreshFails}.");
                if (_refreshFails == RefreshTries) RestartLink("The camera stopped answering.");
                return;
            }
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
            if (!CheckControl(map)) return;
            _ui.Post(() =>
            {
                Props = map;
                _focusRaw = -1;
                PropsChanged?.Invoke();
            });
        });
    }

    public Prop? Get(PropCode code) => Props.GetValueOrDefault((uint)code);

    public void Set(PropCode code, long value)
    {
        var p = Get(code);
        if (p == null)
        {
            Log.Write($"Set {code}: the camera isn't reporting it.");
            return;
        }
        Log.Write($"Set {code} to {value} (writable {p.Writable}).");
        _latestSet[code] = value;
        Run(() =>
        {
            // Quick clicks queue up: send the newest value once rather than walking through each one.
            if (!_latestSet.TryRemove(code, out var latest)) return;
            int err = _bridge.SetProp((uint)code, (ulong)latest, p.Type);
            if (err != 0) SayFailed(RefusedSetting, err);
        });
        RequestRefresh();
    }

    // ---- Actions ----

    public bool IsRecording => Get(PropCode.RecordingState)?.Value == 1;

    /// <summary>The camera toggles recording on "Down"; Sony asks for "Up" right after.</summary>
    public void ToggleRecord() => Run(() =>
    {
        int err = _bridge.Command(CmdMovieRecord, ParamDown);
        Thread.Sleep(80);
        _bridge.Command(CmdMovieRecord, ParamUp);
        if (err != 0) SayFailed("The camera didn't respond to the record button.", err);
    });

    public bool IsManualFocus => Get(PropCode.FocusMode)?.Value is FocusManual or 6;
    public bool IsTracking => Get(PropCode.FocusTrackingStatus)?.Value == TrackingActive || Frames.Any(f => f.Kind == FrameKind.Tracking);
    public bool CanCancelTouch => Get(PropCode.CancelRemoteTouchOperationEnableStatus)?.Value == 1;
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
        if (Get(PropCode.FunctionOfTouchOperation)?.Value == TouchFnOff)
            return "Tap does is set to Off, so clicks do nothing. Pick Focus or Tracking.";
        uint xy = TouchPoint(fx, fy);
        Log.Write($"Tap at {fx:0.00}, {fy:0.00}: focus mode {Get(PropCode.FocusMode)?.Value}, tap function {Get(PropCode.FunctionOfTouchOperation)?.Value}, " +
                  $"touch allowed {Get(PropCode.RemoteTouchOperationEnableStatus)?.Value}");
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
                int err = _bridge.SetProp((uint)PropCode.RemoteTouchOperation, xy, TypeUInt32);
                Thread.Sleep(1300);
                _bridge.Command(CmdCancelRemoteTouch, ParamDown);
                Thread.Sleep(50);
                _bridge.Command(CmdCancelRemoteTouch, ParamUp);
                SetNow(PropCode.FocusMode, mode);
                if (function != TouchFnFocus) SetNow(PropCode.FunctionOfTouchOperation, function);
                _focusOnceBusy = 0;
                if (err == 0) Say("Focused. Back in manual.");
                else SayFailed("The camera couldn't focus there.", err);
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
                int err = _bridge.SetProp((uint)PropCode.RemoteTouchOperation, xy, TypeUInt32);
                if (err != 0) SayFailed("The camera couldn't focus there.", err);
                else if (track)
                {
                    Thread.Sleep(1200);
                    _ui.Post(() => RequestRefresh());
                    Thread.Sleep(300);
                    _ui.Post(() =>
                    {
                        if (TapTracks && !IsTracking) Say("The camera didn't start tracking there.");
                    });
                }
            });
            return null;
        }
        if (Get(PropCode.AF_Area_Position) is { Writable: true })
        {
            Run(() =>
            {
                int err = _bridge.SetProp((uint)PropCode.AF_Area_Position, xy, TypeUInt32);
                if (err != 0) SayFailed("The camera couldn't move its focus area there.", err);
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
        int err = _bridge.SetProp((uint)code, (ulong)value, p.Type);
        if (err != 0) SayFailed(RefusedSetting, err);
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

    /// <summary>The ratios the camera will magnify to right now. Empty when it won't, as on the A7S III in movie mode.</summary>
    public List<long> MagnifyChoices()
    {
        if (Get(PropCode.Focus_Magnifier_Setting) is not { Writable: true } setting) return [];
        return setting.Candidates.Select(v => (v >> 32) & 0xFFFFFFFF)
            .Where(r => r is > 10 and < 0xFFFFFFFF).Distinct().OrderBy(r => r).ToList(); // ×1 shows no change
    }

    /// <summary>Magnifies the centre of the picture at a ratio x10; 0 turns it off.</summary>
    public void Magnify(long ratio) => Run(() =>
    {
        const long Invalid = 0xFFFFFFFF;
        var setting = Get(PropCode.Focus_Magnifier_Setting);
        Log.Write($"Magnifier to x{ratio / 10.0}: writable {setting?.Writable}, enable {setting?.Enable}, value 0x{setting?.Value:X}, " +
                  $"choices {string.Join(' ', (setting?.Candidates ?? []).Select(v => v.ToString("X")))}");
        var (xMax, yMax) = MagnifierGrid();
        _magX = xMax / 2;
        _magY = yMax / 2;
        if (ratio > 0)
        {
            // Sony: refresh with an invalid position before the first real one.
            SetMagnifier((ratio << 32) | Invalid);
            WaitForQueue();
            Thread.Sleep(200);
            SetMagnifier((ratio << 32) | ((long)_magX << 16) | (uint)_magY);
        }
        else SetMagnifier(Invalid);
        _ = CheckMagnifierAsync(ratio);
    });

    private async Task CheckMagnifierAsync(long ratio)
    {
        await Task.Delay(500);
        _ui.Post(RequestRefresh);
        await Task.Delay(800);
        _ui.Post(() =>
        {
            if (MagnifyRatio != ratio) Say($"The camera didn't change the magnifier (it reports x{MagnifyRatio / 10.0}).");
        });
    }

    private (int X, int Y) MagnifierGrid() =>
        _magnifier is { XMax: > 0, YMax: > 0 } m ? (m.XMax, m.YMax) : (640, 480);

    /// <summary>Moves the magnified area so the tapped spot becomes its centre.</summary>
    private void PanMagnifier(double fx, double fy)
    {
        long ratio = MagnifyRatio;
        var (xMax, yMax) = MagnifierGrid();
        double zoom = ratio / 10.0;
        double halfW = (_magnifier is { Width: > 0 } a ? a.Width : xMax / zoom) / 2;
        double halfH = (_magnifier is { Height: > 0 } b ? b.Height : yMax / zoom) / 2;
        _magX = (int)Math.Clamp(_magX + (fx - 0.5) * 2 * halfW, halfW, xMax - 1 - halfW);
        _magY = (int)Math.Clamp(_magY + (fy - 0.5) * 2 * halfH, halfH, yMax - 1 - halfH);
        long value = (ratio << 32) | ((long)_magX << 16) | (uint)_magY;
        Run(() => SetMagnifier(value));
    }

    private void SetMagnifier(long value)
    {
        int err = _bridge.SetProp((uint)PropCode.Focus_Magnifier_Setting, (ulong)value, TypeUInt64);
        Log.Write($"Magnifier set 0x{value:X}: {(err == 0 ? "sent" : Native.ErrorName(err))}");
        if (err != 0) SayFailed("The camera refused the focus magnifier.", err);
    }

    private int _magX = 320, _magY = 240;

    /// <summary>
    /// Turns the focus ring: negative steps go nearer, positive farther (-7..+7, bigger is a larger move).
    /// Works in MF and DMF. Only the newest step waits to be sent, so the ring stops when the mouse stops.
    /// </summary>
    public void FocusStep(int step)
    {
        Volatile.Write(ref _focusPending, Math.Clamp(step, -7, 7));
        Volatile.Write(ref _focusLastAt, Environment.TickCount64);
        _focusWake.Set();
    }

    /// <summary>Turns the ring by a drag or scroll of this many pixels. A slow drag makes fine moves and a fast one big moves.</summary>
    public void TurnRing(double pixels)
    {
        long now = Environment.TickCount64;
        double speed = Math.Abs(pixels) / Math.Max(now - _ringAt, 1);
        _ringAt = now;
        _ringSpeed = _ringSpeed * 0.7 + speed * 0.3;
        _ringOwed += pixels;
        if (Math.Abs(_ringOwed) < RingPixelsPerStep) return;
        int size = _ringSpeed < 0.4 ? 1 : _ringSpeed < 1.5 ? 3 : 7;
        FocusStep(Math.Sign(_ringOwed) * size);
        _ringOwed = 0;
    }

    /// <summary>Where focus is now, 0 close up to 1 at infinity, or null if the camera and lens don't say.</summary>
    public double? FocusPosition
    {
        get
        {
            if (Get(PropCode.FocusPositionCurrentValue) is not { Enable: > 0 } p) return null;
            var (min, max) = FocusRange(p);
            return Math.Clamp(((_focusRaw >= 0 ? _focusRaw : p.Value) - min) / (double)(max - min), 0, 1);
        }
    }

    /// <summary>Jumps focus to a spot on the range, 0..1. Returns a message if the camera won't.</summary>
    public string? FocusTo(double spot)
    {
        if (Get(PropCode.FocusPositionSetting) is not { Writable: true } p)
            return "This camera or lens won't jump to a focus position. Use the focus ring instead.";
        var (min, max) = FocusRange(p);
        Volatile.Write(ref _focusLastAt, Environment.TickCount64);
        Set(PropCode.FocusPositionSetting, min + (long)(Math.Clamp(spot, 0, 1) * (max - min)));
        return null;
    }

    private static (long Min, long Max) FocusRange(Prop p) =>
        p.Candidates.Length >= 2 && p.Candidates[1] > p.Candidates[0] ? (p.Candidates[0], p.Candidates[1]) : (0, 0xFFFF);

    /// <summary>Reads just the focus position, so the bar keeps moving while the full read is paused.</summary>
    private void ReadFocusPosition() => Run(() =>
    {
        var json = _bridge.GetProps([(uint)PropCode.FocusPositionCurrentValue]);
        if (json == null) return;
        var list = JsonDocument.Parse(json).RootElement;
        if (list.GetArrayLength() == 0) return;
        long raw = (long)list[0].GetProperty("v").GetUInt64();
        _ui.Post(() =>
        {
            _focusRaw = raw;
            FocusMoved?.Invoke();
        });
    });

    private long _focusRaw = -1;
    private int _focusPending;
    private long _focusLastAt;
    private long _ringAt;
    private double _ringSpeed, _ringOwed;

    /// <summary>True just after a focus move, when reading every setting would hold up the next one.</summary>
    private bool Focusing => Environment.TickCount64 - Volatile.Read(ref _focusLastAt) < FocusQuietMs;

    /// <summary>Waits for the SDK to send what it has queued. Sony warns that a backlog stalls the picture and settings.</summary>
    private bool WaitForQueue(int maxMs = 600)
    {
        long until = Environment.TickCount64 + maxMs;
        while (_bridge.QueueCount() > 0)
        {
            if (Environment.TickCount64 > until) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    private void FocusLoop()
    {
        while (true)
        {
            _focusWake.WaitOne();
            if (_closing) return;
            int step;
            while ((step = Interlocked.Exchange(ref _focusPending, 0)) != 0)
            {
                if (!WaitForQueue())
                {
                    Log.Write($"Focus step {step} dropped: the camera is still busy with the last one.");
                    continue;
                }
                int err = _bridge.SetProp((uint)PropCode.NearFar, (ulong)(long)step, TypeInt16);
                if (err != 0) SayFailed("The camera didn't move its focus.", err);
            }
        }
    }

    // ---- The camera's own focus boxes ----

    private sealed record Magnifier(int X, int XMax, int Y, int YMax, int Width, int Height);

    private volatile Magnifier? _magnifier;

    /// <summary>The boxes on the camera's own screen right now.</summary>
    public IReadOnlyList<FocusFrame> Frames { get; private set; } = [];

    /// <summary>The boxes worth drawing. The outline of the whole focus area and any focus box in MF only get in the way.</summary>
    public IEnumerable<FocusFrame> FramesToShow =>
        Frames.Where(f => f.Kind != FrameKind.Focus || (!IsManualFocus && f.W < 0.3));

    private void FrameLoop()
    {
        string last = "", lastKinds = "";
        long sawFramesAt = 0;
        while (true)
        {
            _frameWake.WaitOne(1000);
            if (_closing) return;
            try
            {
                var json = (State == LinkState.Connected ? _bridge.FocusFrames() : null) ?? "";
                if (json != last)
                {
                    last = json;
                    var frames = ParseFrames(json);
                    if (frames.Count > 0) sawFramesAt = Environment.TickCount64;
                    // The camera drops its boxes for a moment every few seconds; holding on stops them flickering.
                    else if (json.Length > 0 && Environment.TickCount64 - sawFramesAt < FrameHoldMs)
                    {
                        last = "";
                        Thread.Sleep(120);
                        continue;
                    }
                    string kinds = $"{frames.Count(f => f.Kind == FrameKind.Focus)} focus, {frames.Count(f => f.Kind == FrameKind.Face)} face, " +
                                   $"{frames.Count(f => f.Kind == FrameKind.Tracking)} tracking";
                    if (kinds != lastKinds) Log.Write($"Camera boxes: {kinds}: {json[..Math.Min(json.Length, 300)]}");
                    lastKinds = kinds;
                    _ui.Post(() =>
                    {
                        Frames = frames;
                        FramesChanged?.Invoke();
                    });
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
            {
                Log.Write("Couldn't read the camera's focus boxes: " + ex.Message);
            }
            Thread.Sleep(120); // these reads share the link with the picture
        }
    }

    private List<FocusFrame> ParseFrames(string json)
    {
        var list = new List<FocusFrame>();
        _magnifier = null;
        if (json.Length == 0) return list;
        var root = JsonDocument.Parse(json).RootElement;
        foreach (var f in root.GetProperty("f").EnumerateArray())
        {
            var v = f.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (v[4] <= 0 || v[6] <= 0 || list.Count >= 40) continue;
            list.Add(new FocusFrame((FrameKind)(int)v[0], v[2] == FrameFocused, v[3] / v[4], v[5] / v[6], v[7] / v[4], v[8] / v[6]));
        }
        if (root.TryGetProperty("m", out var m))
        {
            var v = m.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            _magnifier = new Magnifier(v[0], v[1], v[2], v[3], v[4], v[5]);
        }
        return list;
    }

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

    /// <summary>Stops the camera's touch focus or tracking. Returns a message if there's nothing to stop.</summary>
    public string? CancelTouch()
    {
        if (!CanCancelTouch && !IsTracking) return "Nothing to cancel. The camera isn't holding a touch focus or tracking.";
        Run(() =>
        {
            int down = _bridge.Command(CmdCancelRemoteTouch, ParamDown);
            Thread.Sleep(50);
            _bridge.Command(CmdCancelRemoteTouch, ParamUp);
            Log.Write(down == 0 ? "Touch focus cancelled." : "Cancelling touch focus failed: " + Native.ErrorName(down));
            _propsDirty = true;
            if (down != 0) ReleaseTouch();
            else _ = ReleaseIfStillTrackingAsync();
        });
        return null;
    }

    private async Task ReleaseIfStillTrackingAsync()
    {
        await Task.Delay(1000);
        _ui.Post(() =>
        {
            if (IsTracking) Run(ReleaseTouch);
        });
    }

    /// <summary>Runs on the camera thread. Switching the tap function off and back also stops tracking.</summary>
    private void ReleaseTouch()
    {
        if (Get(PropCode.FunctionOfTouchOperation) is not { } function) return;
        Log.Write("Stopping tracking by switching the tap function off and back.");
        SetNow(PropCode.FunctionOfTouchOperation, TouchFnOff);
        Thread.Sleep(300);
        if (function.Value != TouchFnOff) SetNow(PropCode.FunctionOfTouchOperation, function.Value);
        _propsDirty = true;
    }

    public void Dispose()
    {
        _poll.Dispose();
        _quickPoll.Dispose();
        _closing = true;
        _focusWake.Set();
        _frameWake.Set();
        _focusThread.Join(1000);
        _frameThread.Join(1000);
        Run(_bridge.Release);
        _queue.CompleteAdding();
        _worker.Join(3000);
    }
}
