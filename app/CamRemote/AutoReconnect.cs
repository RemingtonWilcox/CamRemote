namespace CamRemote;

/// <summary>
/// Keeps trying the saved camera while it's off or out of range, and after the link drops. Shared by both
/// windows, which call <see cref="TickAsync"/> about once a second and show <see cref="Status"/>.
///
/// After a drop it first waits <see cref="Grace"/>, because the SDK tries to resume the session by itself.
/// Then it searches the network, which also releases the SDK's stale session (see cb_enum), so a camera that
/// came back at a new IP is found by its MAC address (<see cref="CameraConnect.SavedAsync"/>). Tries back off
/// from <see cref="FirstRetry"/> to <see cref="MaxRetry"/>, so a camera that's simply off isn't searched
/// for every few seconds; each search holds the camera thread for a few seconds.
/// </summary>
public sealed class AutoReconnect
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StuckConnecting = TimeSpan.FromSeconds(45);

    private readonly CameraService _cam;
    private readonly Settings _settings;
    private readonly Action<string> _say;
    private readonly Func<Task<bool>> _askFingerprint;
    private readonly Func<DateTime> _now;
    private LinkState _lastState;
    private DateTime _since, _nextTry;
    private bool _first = true, _busy, _wasConnected;

    /// <summary>Tries since the camera was last connected.</summary>
    public int Attempts { get; private set; }

    /// <summary>When the next try is due, or null when nothing is waiting.</summary>
    public DateTime? NextTry => _cam.State == LinkState.Disconnected && !_cam.HoldReconnect ? _nextTry : null;

    /// <summary>A line for the window's banner while reconnecting, or null when there's nothing to say.</summary>
    public string? Status { get; private set; }

    public event Action? StatusChanged;

    public AutoReconnect(CameraService cam, Settings settings, Action<string> say, Func<Task<bool>> askFingerprint, Func<DateTime>? now = null)
    {
        _cam = cam;
        _settings = settings;
        _say = say;
        _askFingerprint = askFingerprint;
        _now = now ?? (() => DateTime.UtcNow);
        _since = _nextTry = _now();
        _lastState = cam.State;
        cam.StateChanged += OnStateChanged;
    }

    /// <summary>The delay before try number <paramref name="attempt"/> + 1: 10, 20, 40, 60, 60… seconds.</summary>
    public static TimeSpan Delay(int attempt)
    {
        double s = FirstRetry.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(s, MaxRetry.TotalSeconds));
    }

    /// <summary>Runs on the UI thread, from the camera's StateChanged.</summary>
    private void OnStateChanged()
    {
        var now = _now();
        var state = _cam.State;
        if (state == _lastState) return;
        _since = now;
        if (state == LinkState.Connected)
        {
            if (Attempts > 0) Log.Write($"Reconnected after {Attempts} tries.");
            _wasConnected = true;
            Attempts = 0;
            SetStatus(null);
        }
        else if (state == LinkState.Disconnected)
        {
            if (_lastState == LinkState.Connected)
            {
                // A drop: give the SDK's own resume a chance first.
                Attempts = 0;
                _nextTry = now + Grace;
                SetStatus("Camera connection lost — reconnecting…   (the camera keeps recording if it was)");
            }
            else
            {
                // A try that got as far as connecting, then failed.
                _nextTry = now + Delay(Attempts);
                ShowWaiting();
            }
        }
        _lastState = state;
    }

    /// <summary>
    /// Starts a try if one is due. <paramref name="allowed"/> is false while the window shouldn't
    /// reconnect by itself: before the first-launch agreement, while the Connect dialog is open,
    /// or with auto-connect turned off.
    /// </summary>
    public async Task TickAsync(bool allowed)
    {
        if (!allowed || _busy || _cam.HoldReconnect || _settings.CameraIp.Length == 0)
        {
            if (_cam.HoldReconnect && Status != null) SetStatus(null);
            return;
        }
        var now = _now();
        bool due = _cam.State switch
        {
            LinkState.Disconnected => _first || now >= _nextTry,
            LinkState.Connecting => now - _since > StuckConnecting,
            _ => false,
        };
        if (!due) return;
        bool first = _first;
        _first = false;
        _busy = true;
        Attempts++;
        bool started;
        try
        {
            if (Attempts > 1 || _wasConnected) Log.Write($"Reconnect try {Attempts}, looking for the camera (MAC {_settings.CameraMac}, last at {_settings.CameraIp}).");
            started = await CameraConnect.SavedAsync(_cam, _settings, _say, _askFingerprint, quiet: !first);
        }
        catch (Exception ex)
        {
            Log.Write("Reconnect try failed: " + ex);
            started = false;
        }
        finally
        {
            _busy = false;
        }
        _since = _now();
        _nextTry = _since + Delay(Attempts);
        if (!started) ShowWaiting();
    }

    private void ShowWaiting()
    {
        if (_cam.HoldReconnect) { SetStatus(null); return; }
        int secs = (int)Math.Round(Delay(Attempts).TotalSeconds);
        SetStatus(_wasConnected
            ? $"Camera connection lost — looking for it on the network (try {Attempts}, next in {secs} s)…   (the camera keeps recording if it was)"
            : $"Waiting for the camera (on, on Wi-Fi, Remote Shooting on). Trying again in {secs} s…");
    }

    private void SetStatus(string? s)
    {
        if (s == Status) return;
        Status = s;
        StatusChanged?.Invoke();
    }
}
