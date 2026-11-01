namespace CamRemote;

/// <summary>The Sony camera as the phone page sees it. Every camera call hops to the UI thread, where CameraService lives.</summary>
public sealed class SonyRemote(IUi ui, CameraService cam, Func<(byte[] Jpeg, long No)?> latest, Settings settings, IReadOnlyList<IPropRow> rows,
    Func<(string Battery, string Card, string Alert)> status) : IRemoteCamera
{
    public string Id => "sony";
    public string Name => CameraModels.Label(settings.CameraModel);

    public object State() => ui.Invoke<object>(() =>
    {
        var (battery, card, alert) = status();
        long focusMode = cam.Get(PropCode.FocusMode)?.Value ?? 0;
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
        };
    });

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
}
