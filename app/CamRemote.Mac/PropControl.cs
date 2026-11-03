using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace CamRemote;

/// <summary>
/// One camera setting in the side panel. None of these react to the mouse wheel, so scrolling
/// the panel can never change the camera.
/// </summary>
public sealed class PropControl : IPropRow
{
    public enum Kind
    {
        Buttons, // a row of buttons, for short lists (on/off, AF/MF)
        Picker,  // a button showing the value; click it for a grid of every choice
        Stepper, // ◀ value ▶, plus the same grid
        Shortcuts, // the everyday choices as buttons, and More for the grid of every choice
    }

    private static readonly IBrush PopupBg = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x14));
    private static readonly IBrush PopupLine = new SolidColorBrush(Color.FromRgb(0x30, 0x35, 0x3B));

    private static readonly TimeSpan WantedWait = TimeSpan.FromSeconds(2);

    private readonly CameraService _cam;
    private readonly Func<long, string> _fmt;
    private readonly Func<long, double>? _order;
    private readonly Kind _kind;
    private readonly TextBlock _label;
    private readonly TextBlock? _valueText; // the value, in a picker or stepper
    private readonly UniformGrid? _buttons;
    private readonly Button? _value, _prev, _next;
    private readonly Popup? _popup;
    private readonly UniformGrid? _grid;
    private readonly ToggleButton? _more; // with Shortcuts: opens the grid, and names the choice when it isn't a shortcut
    private readonly TextBlock? _moreText;
    private long[] _choices = [];
    private long _current;
    private long? _wanted; // what the user last asked for, until the camera reports it
    private DateTime _wantedUntil;

    public PropCode Code { get; }
    public string Section { get; init; } = ""; // where the phone page shows it
    public long[]? Presets { get; init; } // a short list to offer instead of every step of a long range
    public long[] Shortcuts { get; set; } = []; // with Kind.Shortcuts, the choices shown as buttons
    public bool OnPhone { get; init; } = true; // false for a second copy of a setting shown elsewhere in the window
    public StackPanel Root { get; } = new() { Margin = new Thickness(0, 4, 0, 6) };

    public PropControl(CameraService cam, string label, PropCode code, Func<long, string> fmt, Kind kind,
        Func<long, double>? order = null)
    {
        _cam = cam;
        Code = code;
        _fmt = fmt;
        _kind = kind;
        _order = order;
        _label = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 3), Foreground = Brushes.Gray };

        if (kind is Kind.Buttons or Kind.Shortcuts)
        {
            Root.Children.Add(_label);
            _buttons = new UniformGrid { Rows = 1 };
            Root.Children.Add(_buttons);
            if (kind == Kind.Buttons) return;
            _moreText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            _more = new ToggleButton { Margin = new Thickness(2), Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Content = _moreText };
            _more.Click += (_, _) =>
            {
                Show(); // the click toggled it; what the camera has decides
                _popup!.IsOpen = true;
            };
        }
        else
        {
            // Its name sits inside, above the value, like a tile in the iPhone app.
            _label.FontSize = 11;
            _label.Margin = new Thickness(0);
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _valueText = new TextBlock { FontSize = kind == Kind.Stepper ? 18 : 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                                         TextTrimming = TextTrimming.CharacterEllipsis };
            _value = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = kind == Kind.Stepper ? 54 : 46, Padding = new Thickness(6, 4, 6, 4),
                                  Content = new StackPanel { Children = { _label, _valueText } } };
            _value.Click += (_, _) => { if (_popup != null) _popup.IsOpen = true; };
        }
        _grid = new UniformGrid { Columns = 4, Margin = new Thickness(6) };
        _popup = new Popup
        {
            PlacementTarget = (Control?)_more ?? _value,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = PopupBg, BorderBrush = PopupLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Child = new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid },
            },
        };

        if (kind == Kind.Picker)
        {
            Root.Children.Add(_value!);
        }
        else if (kind == Kind.Stepper)
        {
            _prev = new Button { Content = "◀", Width = 44, FontSize = 16, Padding = new Thickness(0, 4) };
            _next = new Button { Content = "▶", Width = 44, FontSize = 16, Padding = new Thickness(0, 4) };
            _prev.Click += (_, _) => Step(-1);
            _next.Click += (_, _) => Step(+1);
            var row = new DockPanel();
            DockPanel.SetDock(_prev, Dock.Left);
            DockPanel.SetDock(_next, Dock.Right);
            _value!.Margin = new Thickness(4, 0, 4, 0);
            row.Children.Add(_prev);
            row.Children.Add(_next);
            row.Children.Add(_value);
            Root.Children.Add(row);
        }
        Root.Children.Add(_popup);
    }

    private void Step(int dir)
    {
        int i = Array.IndexOf(_choices, _current);
        int j = i < 0 ? 0 : Math.Clamp(i + dir, 0, _choices.Length - 1);
        if (_choices.Length > 0 && j != i) Pick(_choices[j]);
    }

    public void Pick(long v)
    {
        if (_popup != null) _popup.IsOpen = false;
        if (v == _current) return;
        _current = v; // show it right away; the camera's answer follows
        _wanted = v;
        _wantedUntil = DateTime.Now + WantedWait;
        Show();
        _cam.Set(Code, v);
    }

    public bool Allows(long v) => _cam.Get(Code) is { Writable: true } && _choices.Contains(v);

    /// <summary>This row for the phone page. Values are strings because some are too big for JavaScript numbers.</summary>
    public object? Snapshot() => !OnPhone || _cam.Get(Code) is not { } p ? null : new
    {
        id = Code.ToString(),
        label = _label.Text,
        section = Section,
        kind = _kind.ToString(),
        value = _current.ToString(),
        text = _fmt(_current),
        enabled = p.Writable && _choices.Length > 1,
        choices = _choices.Select(v => new { value = v.ToString(), text = _fmt(v) }).ToList(),
    };

    public void Update(Prop? p)
    {
        bool usable = p != null && p.Writable;
        Root.Opacity = p == null ? 0.45 : 1;
        if (p == null)
        {
            _wanted = null;
            _choices = [];
            Rebuild();
            if (_valueText != null) _valueText.Text = "—";
            SetEnabled(false);
            return;
        }

        var values = (Presets != null ? p.Within(Presets) : p.Choices()).Distinct().ToList();
        if (_order != null) values = values.OrderBy(_order).ToList();
        if (!values.Contains(p.Value)) values.Insert(0, p.Value);
        if (!values.SequenceEqual(_choices))
        {
            _choices = values.ToArray();
            Rebuild();
        }
        // A read taken before a quick click finished still shows the old value. If the camera never
        // gets there, it refused, so go back to what it really has.
        if (p.Value == _wanted || DateTime.Now > _wantedUntil) _wanted = null;
        _current = _wanted ?? p.Value;
        Show();
        SetEnabled(usable && _choices.Length > 1);
    }

    private void SetEnabled(bool on)
    {
        if (_buttons != null) _buttons.IsEnabled = on;
        if (_value != null) _value.IsEnabled = on;
        if (_prev != null) _prev.IsEnabled = on;
        if (_next != null) _next.IsEnabled = on;
    }

    private void Rebuild()
    {
        if (_buttons != null)
        {
            Fill(_buttons, _more == null ? _choices : _choices.Where(Shortcuts.Contains).ToArray());
            if (_more != null && _choices.Any(v => !Shortcuts.Contains(v))) _buttons.Children.Add(_more); // only when there's more to see
            int n = _buttons.Children.Count;
            _buttons.Rows = 0;
            _buttons.Columns = _more != null ? Math.Clamp(n, 1, 3) : n <= 5 ? Math.Max(n, 1) : 4; // a long list wraps
        }
        if (_grid != null)
        {
            Fill(_grid, _choices);
            _grid.Columns = _choices.Length <= 8 ? 2 : _choices.Length <= 24 ? 3 : 4;
        }
    }

    private void Fill(UniformGrid target, long[] values)
    {
        target.Children.Clear();
        foreach (var v in values)
        {
            var b = new ToggleButton
            {
                Content = _fmt(v), Tag = v, Margin = new Thickness(2), Padding = new Thickness(8, 5, 8, 5),
                MinWidth = target == _grid ? 84 : 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            b.Click += (_, _) =>
            {
                b.IsChecked = (long)b.Tag! == _current; // the camera decides; don't let the click toggle it
                Pick((long)b.Tag!);
            };
            target.Children.Add(b);
        }
    }

    private void Show()
    {
        if (_valueText != null) _valueText.Text = _fmt(_current);
        foreach (var panel in new[] { _buttons, _grid })
            if (panel != null)
                foreach (var b in panel.Children.OfType<ToggleButton>().Where(b => b.Tag is long))
                    b.IsChecked = (long)b.Tag! == _current;
        if (_more == null) return;
        // Lit, with the choice's name, when the camera is on one the buttons don't show.
        bool elsewhere = !Shortcuts.Contains(_current);
        _more.IsChecked = elsewhere;
        _moreText!.Text = elsewhere ? _fmt(_current) + " ▾" : "More ▾";
    }
}
