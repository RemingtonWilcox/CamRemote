namespace CamRemote;

/// <summary>The Sony camera as the phone page sees it. Every camera call hops to the UI thread, where CameraService lives.</summary>
public sealed class SonyRemote(IUi ui, CameraService cam, Func<(byte[] Jpeg, long No)?> latest, Settings settings, IReadOnlyList<IPropRow> rows,
    Func<(string Battery, string Card, string Alert)> status) : IRemoteCamera
{
    public string Id => "sony";
    public string Name => CameraModels.Label(settings.CameraModel);

    private const int PresetSlots = 4;
    private int _press; // counts shutter presses, so a late safety release doesn't cut a newer one short

    public object State() => ui.Invoke<object>(() =>
    {
        var (battery, card, alert) = status();
        long focusMode = cam.Get(PropCode.FocusMode)?.Value ?? 0;
        bool photo = cam.IsPhotoMode;
        // Only the saved slots for the page the camera is on, in the lines its own C buttons show.
        var presets = Enumerable.Range(1, PresetSlots).Select(slot => (slot, preset: cam.PresetIn(settings, slot))).Where(p => p.preset != null).ToList();
        int? active = presets.Where(p => cam.Matches(p.preset!)).Select(p => (int?)p.slot).FirstOrDefault();
        return new
        {
            link = cam.State.ToString().ToLowerInvariant(),
            recording = cam.IsRecording,
            rotation = settings.Rotation,
            battery, card, alert,
            baseIso = cam.State == LinkState.Connected && CameraModels.Find(settings.CameraModel) is { HasBaseIso: true } m ? new[] { m.LowIso, m.HighIso } : [],
            manualFocus = focusMode is 1 or 6,
            meter = cam.MeterStops,
            magnify = cam.MagnifyRatio,
            magnifyChoices = cam.MagnifyChoices(),
            tracking = cam.IsTracking,
            zebraLevel = settings.ZebraLevel,
            peakSensitivity = settings.PeakSensitivity,
            peakColor = settings.PeakColor,
            rows = rows.Select(r => r.Snapshot()).Where(r => r != null).ToList(),
            photo,
            slowQuick = cam.IsSlowQuick,
            offersSlowQuick = cam.OffersSlowQuick,
            presets = presets.Select(p => new { slot = p.slot, lines = Lines(p.preset!, photo) }).ToList(),
            activeSlot = active,
        };
    });

    private static string[] Lines(Preset preset, bool photo)
    {
        var (top, middle, bottom) = CameraService.Describe(preset, photo);
        var lines = new[] { top, middle, bottom }.Where(s => s.Length > 0).ToArray();
        return lines.Length > 0 ? lines : ["Saved"];
    }

    public (byte[] Jpeg, long No)? LatestFrame() => latest();

    public string? Set(string id, string value) => ui.Invoke<string?>(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        if (!long.TryParse(value, out long v)) return "That value isn't valid.";
        if (id == "baseiso") return cam.SetBaseIso(v);
        if (id == "magnify") { cam.Magnify(v); return null; }
        if (id == "stoptracking") return cam.CancelTouch();
        var row = rows.FirstOrDefault(r => r.Code.ToString() == id);
        if (row == null || !row.Allows(v)) return "The camera isn't offering that right now.";
        row.Pick(v);
        return null;
    });

    public string? Record(bool on) => ui.Invoke<string?>(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        if (cam.IsRecording != on) cam.ToggleRecord(); // a repeated tap on a slow link can't flip it back
        return (string?)null;
    });

    public string? Focus(double x, double y) => ui.Invoke<string?>(() =>
        cam.State == LinkState.Connected ? cam.TouchFocus(x, y) : "The camera isn't connected.");

    public string? FocusStep(int step) => ui.Invoke<string?>(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        cam.FocusStep(step);
        return (string?)null;
    });

    /// <summary>A C button, as on this computer: puts the slot back, or saves the camera's settings over it.</summary>
    public string? Preset(int slot, bool save) => ui.Invoke<string?>(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        if (slot is < 1 or > PresetSlots) return "There's no such preset.";
        if (save) return cam.SavePreset(settings, slot) == null ? "The camera isn't connected." : null;
        if (cam.PresetIn(settings, slot) is not { } preset) return $"Nothing is saved in C{slot} yet.";
        return cam.ApplyPreset(preset, $"C{slot}");
    });

    public string? Mode(bool photo) => ui.Invoke<string?>(() =>
        cam.State == LinkState.Connected ? cam.SwitchPage(photo) : "The camera isn't connected.");

    public string? SlowQuick(bool on) => ui.Invoke<string?>(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        if (on == cam.IsSlowQuick) return null;
        return cam.OffersSlowQuick ? cam.SetSlowQuick(on) : "The camera isn't offering S&Q from here. Switch to Video, or turn its mode dial.";
    });

    /// <summary>Down then up takes one photo; held, the camera bursts in a continuous drive mode.</summary>
    public string? Shutter(bool down) => ui.Invoke<string?>(() =>
    {
        if (!down)
        {
            _press++;
            cam.ShutterUp();
            return null;
        }
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        if (!cam.IsPhotoMode) return "Switch to Photo first.";
        cam.ShutterDown();
        int press = ++_press;
        // Lets go by itself if the release never comes, say when the link drops in the middle of a burst.
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => ui.Post(() => { if (press == _press) cam.ShutterUp(); }));
        return null;
    });
}
