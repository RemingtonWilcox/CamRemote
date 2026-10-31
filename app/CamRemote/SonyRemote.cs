using System.Windows.Threading;

namespace CamRemote;

/// <summary>The A7S III as the phone page sees it. Every camera call hops to the UI thread, where CameraService lives.</summary>
public sealed class SonyRemote(Dispatcher ui, CameraService cam, LiveViewFeed live, Settings settings, IReadOnlyList<PropControl> rows,
    Func<(string Battery, string Card, string Alert)> status) : IRemoteCamera
{
    public string Id => "sony";
    public string Name => "A7S III";

    public object State() => ui.Invoke(() =>
    {
        var (battery, card, alert) = status();
        long focusMode = cam.Get(PropCode.FocusMode)?.Value ?? 0;
        return new
        {
            link = cam.State.ToString().ToLowerInvariant(),
            recording = cam.IsRecording,
            rotation = settings.Rotation,
            battery, card, alert,
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

    public (byte[] Jpeg, long No)? LatestFrame() => live.Latest;

    public string? Set(string id, string value) => ui.Invoke(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        if (!long.TryParse(value, out long v)) return "That value isn't valid.";
        if (id == "baseiso") return cam.SetBaseIso(v);
        if (id == "magnify") { cam.Magnify(v); return null; }
        if (id == "stoptracking") { cam.CancelTouch(); return null; }
        var row = rows.FirstOrDefault(r => r.Code.ToString() == id);
        if (row == null || !row.Allows(v)) return "The camera isn't offering that right now.";
        row.Pick(v);
        return null;
    });

    public string? Record(bool on) => ui.Invoke(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        if (cam.IsRecording != on) cam.ToggleRecord(); // a repeated tap on a slow link can't flip it back
        return (string?)null;
    });

    public string? Focus(double x, double y) => ui.Invoke(() =>
        cam.State == LinkState.Connected ? cam.TouchFocus(x, y) : "The camera isn't connected.");

    public string? FocusStep(int step) => ui.Invoke(() =>
    {
        if (cam.State != LinkState.Connected) return "The camera isn't connected.";
        cam.FocusStep(step);
        return (string?)null;
    });
}
