using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

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
    }

    private static readonly Brush PopupBg = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly Brush PopupLine = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

    private readonly CameraService _cam;
    private readonly Func<long, string> _fmt;
    private readonly Func<long, double>? _order;
    private readonly Kind _kind;
    private readonly TextBlock _label;
    private readonly UniformGrid? _buttons;
    private readonly Button? _value, _prev, _next;
    private readonly Popup? _popup;
    private readonly UniformGrid? _grid;
    private long[] _choices = [];
    private long _current;

    public PropCode Code { get; }
    public string Section { get; init; } = ""; // where the phone page shows it
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
        Root.Children.Add(_label);

        if (kind == Kind.Buttons)
        {
            _buttons = new UniformGrid { Rows = 1 };
            Root.Children.Add(_buttons);
            return;
        }

        _value = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = kind == Kind.Stepper ? 18 : 14,
                              FontWeight = kind == Kind.Stepper ? FontWeights.SemiBold : FontWeights.Normal, Padding = new Thickness(6, 4, 6, 4) };
        _value.Click += (_, _) => { if (_popup != null) _popup.IsOpen = true; };
        _grid = new UniformGrid { Columns = 4, Margin = new Thickness(6) };
        _popup = new Popup
        {
            PlacementTarget = _value,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = PopupBg, BorderBrush = PopupLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Child = new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid },
            },
        };

        if (kind == Kind.Picker)
        {
            Root.Children.Add(_value);
        }
        else
        {
            _prev = new Button { Content = "◀", Width = 44, FontSize = 16 };
            _next = new Button { Content = "▶", Width = 44, FontSize = 16 };
            _prev.Click += (_, _) => Step(-1);
            _next.Click += (_, _) => Step(+1);
            var row = new DockPanel();
            DockPanel.SetDock(_prev, Dock.Left);
            DockPanel.SetDock(_next, Dock.Right);
            _value.Margin = new Thickness(4, 0, 4, 0);
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
        Show();
        _cam.Set(Code, v);
    }

    public bool Allows(long v) => _cam.Get(Code) is { Writable: true } && _choices.Contains(v);

    /// <summary>This row for the phone page. Values are strings because some are too big for JavaScript numbers.</summary>
    public object? Snapshot() => _cam.Get(Code) is not { } p ? null : new
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
            _choices = [];
            Rebuild();
            if (_value != null) _value.Content = "—";
            SetEnabled(false);
            return;
        }

        var values = p.Choices().Distinct().ToList();
        if (_order != null) values = values.OrderBy(_order).ToList();
        if (!values.Contains(p.Value)) values.Insert(0, p.Value);
        if (!values.SequenceEqual(_choices))
        {
            _choices = values.ToArray();
            Rebuild();
        }
        _current = p.Value;
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
        var target = (Panel?)_buttons ?? _grid!;
        target.Children.Clear();
        if (_grid != null) _grid.Columns = _choices.Length <= 8 ? 2 : _choices.Length <= 24 ? 3 : 4;
        foreach (var v in _choices)
        {
            var b = new ToggleButton
            {
                Content = _fmt(v), Tag = v, Margin = new Thickness(2), Padding = new Thickness(8, 5, 8, 5),
                MinWidth = _grid != null ? 84 : 0, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            b.Click += (_, _) =>
            {
                b.IsChecked = (long)b.Tag == _current; // the camera decides; don't let the click toggle it
                Pick((long)b.Tag);
            };
            target.Children.Add(b);
        }
    }

    private void Show()
    {
        if (_value != null) _value.Content = _fmt(_current);
        var target = (Panel?)_buttons ?? _grid;
        if (target == null) return;
        foreach (ToggleButton b in target.Children) b.IsChecked = (long)b.Tag == _current;
    }
}
