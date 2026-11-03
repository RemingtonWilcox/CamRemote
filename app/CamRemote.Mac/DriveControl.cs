using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace CamRemote;

/// <summary>
/// Drive in two short rows instead of one list of every mode: Single, Burst, Timer or Bracket, then the
/// speed, delay and photos, or step and photos, of the one picked. Anything else the camera has is under More.
/// </summary>
public sealed class DriveControl
{
    private static readonly IBrush PopupBg = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x14));
    private static readonly IBrush PopupLine = new SolidColorBrush(Color.FromRgb(0x30, 0x35, 0x3B));
    private static readonly TimeSpan WantedWait = TimeSpan.FromSeconds(2);

    private readonly CameraService _cam;
    private readonly UniformGrid _kinds = new() { Rows = 1 };
    private readonly DockPanel _kindRow = new();
    private readonly ToggleButton _more = new() { Content = "▾", Width = 34, Margin = new Thickness(2), Padding = new Thickness(0, 5, 0, 5),
                                                  HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _detail = new();
    private readonly UniformGrid _other = new() { Columns = 2, Margin = new Thickness(6) };
    private readonly Popup _otherPopup;
    private long[] _choices = [];
    private long _current;
    private long? _wanted;
    private DateTime _wantedUntil;
    private readonly Dictionary<DriveModes.Kind, long> _last = [];

    public StackPanel Root { get; } = new() { Margin = new Thickness(0, 4, 0, 6) };

    public DriveControl(CameraService cam)
    {
        _cam = cam;
        Root.Children.Add(new TextBlock { Text = "Drive", FontSize = 12, Margin = new Thickness(0, 0, 0, 3), Foreground = Brushes.Gray });
        ToolTip.SetTip(_more, "Other drive modes this camera has, like white balance and DRO brackets.");
        DockPanel.SetDock(_more, Dock.Right);
        _kindRow.Children.Add(_more);
        _kindRow.Children.Add(_kinds);
        Root.Children.Add(_kindRow);
        Root.Children.Add(_detail);
        _otherPopup = new Popup
        {
            PlacementTarget = _more, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true,
            Child = new Border { Background = PopupBg, BorderBrush = PopupLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _other },
        };
        Root.Children.Add(_otherPopup);
        // A click on ▾ while the list is open only closes it: Avalonia's light dismiss takes that click.
        _more.Click += (_, _) =>
        {
            _more.IsChecked = DriveModes.KindOf(_current) == DriveModes.Kind.Other; // the camera decides
            _other.Children.Clear();
            foreach (var v in _choices.Where(v => DriveModes.KindOf(v) == DriveModes.Kind.Other))
                _other.Children.Add(Button(Format.Drive(v), v == _current, () => Pick(v), 150));
            _otherPopup.IsOpen = true;
        };
    }

    public void Update(Prop? p)
    {
        Root.Opacity = p == null ? 0.45 : 1;
        var choices = p == null ? [] : p.Choices().Distinct().ToArray();
        if (p != null && !choices.Contains(p.Value)) choices = [p.Value, .. choices];
        if (p == null || p.Value == _wanted || DateTime.Now > _wantedUntil) _wanted = null;
        long current = _wanted ?? p?.Value ?? 0;
        if (choices.SequenceEqual(_choices) && current == _current && Root.IsEnabled == (p?.Writable == true)) return;
        _choices = choices;
        _current = current;
        if (p != null) _last[DriveModes.KindOf(current)] = current;
        Root.IsEnabled = p?.Writable == true && choices.Length > 1;
        Build();
    }

    private void Pick(long? v)
    {
        _otherPopup.IsOpen = false;
        if (v is not long value || value == _current) return;
        _current = value;
        _wanted = value;
        _wantedUntil = DateTime.Now + WantedWait;
        _last[DriveModes.KindOf(value)] = value;
        Build();
        _cam.Set(PropCode.DriveMode, value);
    }

    private void Build()
    {
        var kind = DriveModes.KindOf(_current);
        var others = _choices.Where(v => DriveModes.KindOf(v) == DriveModes.Kind.Other).ToArray();
        _kinds.Children.Clear();
        foreach (var k in new[] { DriveModes.Kind.Single, DriveModes.Kind.Burst, DriveModes.Kind.Timer, DriveModes.Kind.Bracket })
        {
            if (!_choices.Any(v => DriveModes.KindOf(v) == k)) continue;
            var kk = k;
            _kinds.Children.Add(Button(DriveModes.Name(k), kind == k, () => Pick(DriveModes.Default(kk, _choices, _last.GetValueOrDefault(kk)))));
        }
        _more.IsVisible = others.Length > 0;
        _more.IsChecked = kind == DriveModes.Kind.Other;
        _kinds.Columns = Math.Max(_kinds.Children.Count, 1);

        _detail.Children.Clear();
        switch (kind)
        {
            case DriveModes.Kind.Other:
                _detail.Children.Add(new TextBlock { Text = Format.Drive(_current), FontSize = 13, Margin = new Thickness(2, 6, 0, 0) });
                break;
            case DriveModes.Kind.Burst:
                Row(null, DriveModes.BurstSpeeds.Where(_choices.Contains).Select(v => (DriveModes.BurstName(v), v == _current, (Action)(() => Pick(v)))));
                break;
            case DriveModes.Kind.Timer when DriveModes.Timer(_current) is { } t:
                var timers = _choices.Where(v => DriveModes.Timer(v) != null).Select(v => DriveModes.Timer(v)!.Value).ToList();
                Row("Delay", DriveModes.TimerDelays.Where(d => timers.Any(x => x.Delay == d))
                    .Select(d => ($"{d} s", d == t.Delay, (Action)(() => Pick(DriveModes.PickTimer(_choices, d, t.Shots))))));
                Row("Photos", DriveModes.TimerShots.Where(n => timers.Any(x => x.Delay == t.Delay && x.Shots == n))
                    .Select(n => (n.ToString(), n == t.Shots, (Action)(() => Pick(DriveModes.PickTimer(_choices, t.Delay, n))))));
                break;
            case DriveModes.Kind.Bracket when DriveModes.Bracket(_current) is { } b:
                var brackets = _choices.Where(v => DriveModes.Bracket(v) != null).Select(v => DriveModes.Bracket(v)!.Value).ToList();
                Row(null, new[] { true, false }.Where(c => brackets.Any(x => x.Continuous == c))
                    .Select(c => (c ? "One press" : "Press each", c == b.Continuous, (Action)(() => Pick(DriveModes.PickBracket(_choices, c, b.Step, b.Shots))))),
                    "One press takes the whole bracket. Press each takes one photo of it per press.");
                var mine = brackets.Where(x => x.Continuous == b.Continuous).ToList();
                Row("Step (stops)", mine.Select(x => x.Step).Distinct().Order()
                    .Select(s => (DriveModes.StepName(s), s == b.Step, (Action)(() => Pick(DriveModes.PickBracket(_choices, b.Continuous, s, b.Shots))))), columns: 6);
                Row("Photos", DriveModes.BracketShots.Where(n => mine.Any(x => x.Step == b.Step && x.Shots == n))
                    .Select(n => (n, n == b.Shots, (Action)(() => Pick(DriveModes.PickBracket(_choices, b.Continuous, b.Step, n))))),
                    "2+ and 2− take the normal photo and one brighter or one darker.");
                break;
        }
    }

    private void Row(string? label, IEnumerable<(string Text, bool On, Action Click)> items, string? tip = null, int columns = 0)
    {
        var grid = new UniformGrid { Margin = new Thickness(0, 2, 0, 0) };
        if (tip != null) ToolTip.SetTip(grid, tip);
        foreach (var (text, on, click) in items) grid.Children.Add(Button(text, on, click));
        if (grid.Children.Count == 0) return;
        grid.Columns = columns > 0 ? Math.Min(columns, grid.Children.Count) : grid.Children.Count;
        if (label != null) _detail.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(2, 6, 0, 1) });
        _detail.Children.Add(grid);
    }

    private static ToggleButton Button(string text, bool on, Action click, double minWidth = 0)
    {
        var b = new ToggleButton
        {
            Content = text, IsChecked = on, Margin = new Thickness(2), Padding = new Thickness(6, 5, 6, 5), MinWidth = minWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        b.Click += (_, _) =>
        {
            b.IsChecked = on; // the camera decides
            click();
        };
        return b;
    }
}
