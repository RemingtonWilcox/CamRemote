using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace CamRemote;

/// <summary>
/// The camera as another CamRemote holds it: the iPhone app, or the PC or Mac app with phone control on.
/// Its picture, its settings as it sends them, record, click to focus and the focus ring.
/// </summary>
public partial class JoinWindow : Window
{
    private readonly RemoteLink _link = null!;
    private readonly Settings _settings = null!;
    private readonly PictureAssist _assist = null!;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hideMark = new() { Interval = TimeSpan.FromSeconds(0.8) };
    private readonly DispatcherTimer _hideToast = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, RowView> _rows = [];
    private RemoteState? _state;
    private WriteableBitmap? _pictureBitmap, _assistBitmap, _histBitmap;
    private int _drawing; // 1 while a decoded frame waits for the UI
    private int _turn; // added to the host's own rotation, for this window only
    private int _misses; // state requests in a row that got no answer
    private DateTime _recBusyUntil;
    private bool _polling;

    public JoinWindow() => InitializeComponent(); // for the XAML loader

    public JoinWindow(RemoteLink link, Settings settings) : this()
    {
        _link = link;
        _settings = settings;
        _assist = new PictureAssist(new AvaloniaUi());
        _assist.Done += ShowAssist;
        HostText.Text = "Through the CamRemote at " + new Uri(link.Address).Host;
        CamSummary.Text = link.CameraName;
        HistToggle.IsChecked = settings.ShowHistogram;
        ZebraToggle.IsChecked = settings.ShowZebras;
        PeakToggle.IsChecked = settings.ShowPeaking;
        ApplyGuides();
        foreach (var (text, step) in new[] { ("◀◀◀", -7), ("◀◀", -3), ("◀", -1), ("▶", 1), ("▶▶", 3), ("▶▶▶", 7) })
        {
            // Held down, it keeps turning
            var b = new RepeatButton { Content = text, Delay = 200, Interval = 200, Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(2, 6), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            b.Click += (_, _) => _ = Do(_link.FocusStepAsync(step), quiet: true);
            RingButtons.Children.Add(b);
        }
        _poll.Tick += async (_, _) => await RefreshAsync();
        _hideMark.Tick += (_, _) => { FocusMark.IsVisible = false; _hideMark.Stop(); };
        _hideToast.Tick += (_, _) => { Toast.IsVisible = false; _hideToast.Stop(); };
        Opened += OnOpened;
        Closing += (_, _) =>
        {
            _poll.Stop();
            _stop.Cancel();
            _link.Dispose();
            Log.Write("Left the joined camera.");
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F9) { OnRecClick(this, e); e.Handled = true; }
        };
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Log.Write($"Joined the camera through {_link.Address}.");
        await RefreshAsync();
        _poll.Start();
        _ = Task.Run(() => _link.RunFramesAsync(OnJpeg, _stop.Token));
    }

    private void OnLeaveClick(object? sender, RoutedEventArgs e) => Close();

    private static IBrush Brush(string key) =>
        Application.Current!.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Gray;

    // ---- Picture ----

    /// <summary>On the frame loop's thread. Frames that arrive while the last is still being drawn are dropped.</summary>
    private void OnJpeg(byte[] jpeg)
    {
        if (Interlocked.CompareExchange(ref _drawing, 1, 0) != 0) return;
        byte[] pixels;
        int w, h;
        try
        {
            var info = SKBitmap.DecodeBounds(jpeg);
            using var bmp = SKBitmap.Decode(jpeg, new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            (pixels, w, h) = (bmp.GetPixelSpan().ToArray(), info.Width, info.Height);
        }
        catch
        {
            _drawing = 0;
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            _drawing = 0;
            Blit(ref _pictureBitmap, Picture, pixels, w, h);
            NoSignal.IsVisible = false;
            _assist.Offer(w, h, target => pixels.AsSpan(0, target.Length).CopyTo(target));
        });
    }

    /// <summary>Copies premultiplied BGRA pixels into an image, making its bitmap the first time or when the size changes.</summary>
    private static void Blit(ref WriteableBitmap? bitmap, Image image, byte[] pixels, int w, int h)
    {
        if (bitmap == null || bitmap.PixelSize.Width != w || bitmap.PixelSize.Height != h)
        {
            bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            image.Source = bitmap;
        }
        using (var target = bitmap.Lock())
            for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w * 4, target.Address + y * target.RowBytes, w * 4);
        image.InvalidateVisual();
    }

    private void OnPicturePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_state?.Connected != true || Picture.Bounds.Width <= 0) return;
        var p = e.GetCurrentPoint(Picture).Position;
        ShowFocusMark(p.X, p.Y);
        _ = Do(_link.FocusAsync(p.X / Picture.Bounds.Width, p.Y / Picture.Bounds.Height));
    }

    private void ShowFocusMark(double x, double y)
    {
        Canvas.SetLeft(FocusMark, x - FocusMark.Width / 2);
        Canvas.SetTop(FocusMark, y - FocusMark.Height / 2);
        FocusMark.IsVisible = true;
        _hideMark.Stop();
        _hideMark.Start();
    }

    private void OnRotateClick(object? sender, RoutedEventArgs e)
    {
        _turn = (_turn + 90) % 360;
        ApplyRotation();
    }

    private void ApplyRotation() => PictureTurn.LayoutTransform = new RotateTransform(((_state?.Rotation ?? 0) + _turn) % 360);

    // ---- Guides ----

    private void OnGuideClick(object? sender, RoutedEventArgs e)
    {
        _settings.ShowHistogram = HistToggle.IsChecked == true;
        _settings.ShowZebras = ZebraToggle.IsChecked == true;
        _settings.ShowPeaking = PeakToggle.IsChecked == true;
        _settings.Save();
        ApplyGuides();
    }

    private void ApplyGuides()
    {
        var s = _state;
        bool zebra = ZebraToggle.IsChecked == true, peak = PeakToggle.IsChecked == true, hist = HistToggle.IsChecked == true;
        _assist.Options = new AssistOptions(hist || zebra || peak, zebra, s?.ZebraLevel ?? _settings.ZebraLevel, peak,
            s?.PeakSensitivity ?? _settings.PeakSensitivity, s?.PeakColor ?? _settings.PeakColor);
        Hud.IsVisible = hist || s?.Meter != null;
        Histogram.IsVisible = OverText.IsVisible = hist;
        if (!zebra && !peak) { _assistBitmap = null; AssistLayer.Source = null; }
    }

    private void ShowAssist(AssistResult r)
    {
        int level = _state?.ZebraLevel ?? _settings.ZebraLevel;
        if (HistToggle.IsChecked == true)
        {
            Blit(ref _histBitmap, Histogram, PictureAssist.DrawHistogram(r, 40, false, level), 256, 40);
            OverText.Text = $"Over {level}%: {r.OverPercent:0.0}%     Under 3%: {r.UnderPercent:0.0}%";
            OverText.Foreground = Brush(r.OverPercent >= 0.5 ? "Warn" : "Dim");
        }
        if (r.Overlay.Length > 0) Blit(ref _assistBitmap, AssistLayer, r.Overlay, r.Width, r.Height);
    }

    // ---- The host's camera ----

    private async Task RefreshAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await _link.StateAsync();
            if (s == null)
            {
                if (++_misses >= 3)
                {
                    LinkDot.Fill = Brush("Rec");
                    LinkText.Text = "Can't reach the other CamRemote";
                    RecButton.IsEnabled = false;
                }
                return;
            }
            _misses = 0;
            Show(s);
        }
        finally
        {
            _polling = false;
        }
    }

    private void Show(RemoteState s)
    {
        bool turned = s.Rotation != (_state?.Rotation ?? 0);
        _state = s;
        if (turned) ApplyRotation();
        bool on = s.Connected;
        LinkDot.Fill = on ? Brush("Ok") : s.Link == "connecting" ? Brush("Warn") : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        LinkText.Text = on ? "Connected" : s.Link == "connecting" ? "The other CamRemote is connecting…" : "The other CamRemote has no camera";
        BatteryText.Text = s.Battery.Length > 0 ? "Battery " + s.Battery : "";
        CardText.Text = s.Card;
        AlertText.Text = s.Alert;
        AlertBox.IsVisible = s.Alert.Length > 0;
        RecButton.IsEnabled = on;
        RecBadge.IsVisible = s.Recording;
        RecLabel.Text = s.Recording ? "Stop" : "Record";
        RecDot.Fill = s.Recording ? Brushes.White : Brush("Rec");
        if (s.Recording) RecButton.Background = Brush("Rec");
        else RecButton.ClearValue(BackgroundProperty);
        if (!on) NoSignal.Text = "The other CamRemote isn't connected to the camera";
        MeterText.Text = on && s.Meter is double m ? $"Meter {(m > 0 ? "+" : "")}{m:0.0} EV" : "";
        MeterText.IsVisible = MeterText.Text.Length > 0;
        ApplyGuides();

        BaseIsoPanel.Children.Clear();
        foreach (long iso in on ? s.BaseIso : [])
        {
            var b = new Button { Content = $"Base ISO {iso}", Margin = new Thickness(0, 0, 4, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            b.Click += (_, _) => _ = Do(_link.SetAsync("baseiso", iso.ToString()));
            BaseIsoPanel.Children.Add(b);
        }
        BaseIsoPanel.IsVisible = BaseIsoPanel.Children.Count > 0;
        FocusRing.IsVisible = on && s.ManualFocus;
        StopTracking.IsVisible = on && s.Tracking;
        ShowMagnify(s);

        var seen = new HashSet<string>();
        foreach (var row in s.Rows)
        {
            seen.Add(row.Id);
            var panel = row.Section switch { "exposure" => ExposureRows, "focus" => FocusRows, "audio" => AudioRows, _ => MoreRows };
            if (!_rows.TryGetValue(row.Id, out var view) || !view.Fits(row, panel))
            {
                if (view != null) ((Panel)view.Root.Parent!).Children.Remove(view.Root);
                view = new RowView(row, panel, (id, value) => _ = Do(_link.SetAsync(id, value)));
                _rows[row.Id] = view;
                panel.Children.Add(view.Root);
            }
            view.Update(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            ((Panel)_rows[gone].Root.Parent!).Children.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }
        AudioHead.IsVisible = AudioRows.Children.Count > 0;
        MoreHead.IsVisible = MoreRows.Children.Count > 0;
    }

    private void ShowMagnify(RemoteState s)
    {
        var ratios = s.Connected && s.MagnifyChoices.Length > 0 ? [0, .. s.MagnifyChoices] : Array.Empty<long>();
        if (MagnifyButtons.Children.Count != ratios.Length)
        {
            MagnifyButtons.Children.Clear();
            foreach (long r in ratios)
            {
                var b = new ToggleButton { Content = r == 0 ? "Off" : $"×{r / 10.0:0.#}", Tag = r, Margin = new Thickness(0, 0, 3, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                b.Click += (_, _) => _ = Do(_link.SetAsync("magnify", r.ToString()));
                MagnifyButtons.Children.Add(b);
            }
        }
        foreach (var b in MagnifyButtons.Children.OfType<ToggleButton>()) b.IsChecked = (long)b.Tag! == s.Magnify;
        MagnifyBox.IsVisible = ratios.Length > 0;
    }

    private void OnRecClick(object? sender, RoutedEventArgs e)
    {
        if (_state?.Connected != true || DateTime.Now < _recBusyUntil) return;
        _recBusyUntil = DateTime.Now.AddSeconds(1.5); // a slow link shouldn't let a double tap stop it again
        _ = Do(_link.RecordAsync(!_state.Recording));
    }

    private void OnStopTrackingClick(object? sender, RoutedEventArgs e) => _ = Do(_link.SetAsync("stoptracking", "0"));

    /// <summary>Waits for an action, says what went wrong if anything, then shows the camera as it is now.</summary>
    private async Task Do(Task<string?> action, bool quiet = false)
    {
        string? problem = await action;
        if (problem != null && !quiet) ShowToast(problem);
        if (!quiet) await RefreshAsync();
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.IsVisible = true;
        _hideToast.Stop();
        _hideToast.Start();
    }

    /// <summary>One setting from the host, drawn by its kind: a row of buttons, ◀ value ▶, or a list.</summary>
    private sealed class RowView
    {
        public readonly StackPanel Root = new() { Margin = new Thickness(0, 4, 0, 4) };
        private readonly Panel _panel;
        private readonly string _kind, _choices;
        private readonly ComboBox? _list;
        private readonly Button? _prev, _next;
        private readonly List<ToggleButton> _buttons = [];
        private RemoteRow _row;
        private bool _updating;

        public RowView(RemoteRow row, Panel panel, Action<string, string> pick)
        {
            _row = row;
            _panel = panel;
            _kind = row.Kind;
            _choices = Signature(row);
            var label = new TextBlock { Text = row.Label, Margin = new Thickness(0, 0, 0, 3) };
            label.Classes.Add("Label");
            Root.Children.Add(label);
            var stretch = Avalonia.Layout.HorizontalAlignment.Stretch;
            var center = Avalonia.Layout.HorizontalAlignment.Center;
            if (row.Kind == "Buttons" && row.Choices.Count <= 6)
            {
                var grid = new UniformGrid { Rows = 1 };
                foreach (var c in row.Choices)
                {
                    var b = new ToggleButton { Content = c.Text, Tag = c.Value, Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(4, 6), HorizontalAlignment = stretch, HorizontalContentAlignment = center };
                    b.Click += (_, _) => { b.IsChecked = b.Tag as string == _row.Value; pick(_row.Id, (string)b.Tag!); };
                    _buttons.Add(b);
                    grid.Children.Add(b);
                }
                Root.Children.Add(grid);
                return;
            }
            _list = new ComboBox { ItemsSource = row.Choices.Select(c => c.Text).ToList(), MaxDropDownHeight = 420, HorizontalAlignment = stretch };
            _list.SelectionChanged += (_, _) =>
            {
                int i = _list.SelectedIndex;
                if (!_updating && i >= 0 && i < _row.Choices.Count && _row.Choices[i].Value != _row.Value) pick(_row.Id, _row.Choices[i].Value);
            };
            if (row.Kind != "Stepper")
            {
                Root.Children.Add(_list);
                return;
            }
            _prev = new Button { Content = "◀", Width = 40, HorizontalContentAlignment = center };
            _next = new Button { Content = "▶", Width = 40, HorizontalContentAlignment = center };
            _prev.Click += (_, _) => Step(-1, pick);
            _next.Click += (_, _) => Step(1, pick);
            var line = new DockPanel();
            DockPanel.SetDock(_prev, Dock.Left);
            DockPanel.SetDock(_next, Dock.Right);
            _list.Margin = new Thickness(4, 0);
            line.Children.Add(_prev);
            line.Children.Add(_next);
            line.Children.Add(_list);
            Root.Children.Add(line);
        }

        private void Step(int by, Action<string, string> pick)
        {
            int i = _row.Choices.FindIndex(c => c.Value == _row.Value) + by;
            if (i >= 0 && i < _row.Choices.Count) pick(_row.Id, _row.Choices[i].Value);
        }

        private static string Signature(RemoteRow r) => string.Join("|", r.Choices.Select(c => c.Value + "=" + c.Text));

        /// <summary>False when the row has changed shape and has to be drawn again.</summary>
        public bool Fits(RemoteRow row, Panel panel) => panel == _panel && row.Kind == _kind && Signature(row) == _choices;

        public void Update(RemoteRow row)
        {
            _row = row;
            Root.IsEnabled = row.Enabled;
            Root.Opacity = row.Enabled ? 1 : 0.55;
            foreach (var b in _buttons) b.IsChecked = b.Tag as string == row.Value;
            if (_list == null || _list.IsDropDownOpen) return; // don't change a list under the mouse
            _updating = true;
            int i = row.Choices.FindIndex(c => c.Value == row.Value);
            _list.SelectedIndex = i;
            if (i < 0) _list.PlaceholderText = row.Text;
            _updating = false;
            if (_prev != null) _prev.IsEnabled = i > 0;
            if (_next != null) _next.IsEnabled = i >= 0 && i < row.Choices.Count - 1;
        }
    }
}
