using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CamRemote;

/// <summary>
/// The camera as another CamRemote holds it: the iPhone app, or the PC or Mac app with phone control on.
/// Its picture, its settings as it sends them, record, click to focus and the focus ring.
/// </summary>
public partial class JoinWindow : Window
{
    private readonly RemoteLink _link;
    private readonly Settings _settings;
    private readonly PictureAssist _assist;
    private readonly DispatcherTimer _poll, _hideMark, _hideToast;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, RowView> _rows = [];
    private RemoteState? _state;
    private WriteableBitmap? _assistBitmap;
    private int _drawing; // 1 while a decoded frame waits for the UI
    private int _turn; // added to the host's own rotation, for this window only
    private int _misses; // state requests in a row that got no answer
    private DateTime _recBusyUntil;
    private bool _polling;

    public JoinWindow(RemoteLink link, Settings settings)
    {
        InitializeComponent();
        _link = link;
        _settings = settings;
        _assist = new PictureAssist(new WpfUi(Dispatcher));
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
            var b = new RepeatButton { Content = text, Delay = 200, Interval = 200, Style = (Style)FindResource("PanelButton"), Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(2, 6, 2, 6) };
            b.Click += (_, _) => _ = Do(_link.FocusStepAsync(step), quiet: true);
            RingButtons.Children.Add(b);
        }
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) => await RefreshAsync(), Dispatcher);
        _hideMark = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
        _hideMark.Tick += (_, _) => { FocusMark.Visibility = Visibility.Collapsed; _hideMark.Stop(); };
        _hideToast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hideToast.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _hideToast.Stop(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Write($"Joined the camera through {_link.Address}.");
        await RefreshAsync();
        _poll.Start();
        _ = Task.Run(() => _link.RunFramesAsync(OnJpeg, _stop.Token));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _poll.Stop();
        _stop.Cancel();
        _link.Dispose();
        Log.Write("Left the joined camera.");
    }

    private void OnLeaveClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9) { OnRecClick(this, e); e.Handled = true; }
    }

    // ---- Picture ----

    /// <summary>On the frame loop's thread. Frames that arrive while the last is still being drawn are dropped.</summary>
    private void OnJpeg(byte[] jpeg)
    {
        if (Interlocked.CompareExchange(ref _drawing, 1, 0) != 0) return;
        BitmapSource bmp;
        try
        {
            var img = new BitmapImage();
            using (var ms = new System.IO.MemoryStream(jpeg))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
            }
            img.Freeze();
            bmp = img;
        }
        catch
        {
            _drawing = 0;
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            _drawing = 0;
            Picture.Source = bmp;
            NoSignal.Visibility = Visibility.Collapsed;
            BitmapSource src = bmp.Format == PixelFormats.Bgr32 || bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
            _assist.Offer(src.PixelWidth, src.PixelHeight, px => src.CopyPixels(px, src.PixelWidth * 4, 0));
        });
    }

    private void OnPictureClick(object sender, MouseButtonEventArgs e)
    {
        if (_state?.Connected != true || Picture.ActualWidth <= 0) return;
        var p = e.GetPosition(Picture);
        ShowFocusMark(p.X, p.Y);
        _ = Do(_link.FocusAsync(p.X / Picture.ActualWidth, p.Y / Picture.ActualHeight));
    }

    private void ShowFocusMark(double x, double y)
    {
        Canvas.SetLeft(FocusMark, x - FocusMark.Width / 2);
        Canvas.SetTop(FocusMark, y - FocusMark.Height / 2);
        FocusMark.Visibility = Visibility.Visible;
        _hideMark.Stop();
        _hideMark.Start();
    }

    private void OnRotateClick(object sender, RoutedEventArgs e)
    {
        _turn = (_turn + 90) % 360;
        ApplyRotation();
    }

    private void ApplyRotation() => PictureRotation.Angle = ((_state?.Rotation ?? 0) + _turn) % 360;

    // ---- Guides ----

    private void OnGuideClick(object sender, RoutedEventArgs e)
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
        Hud.Visibility = hist || s?.Meter != null ? Visibility.Visible : Visibility.Collapsed;
        Histogram.Visibility = OverText.Visibility = hist ? Visibility.Visible : Visibility.Collapsed;
        if (!zebra && !peak) { _assistBitmap = null; AssistLayer.Source = null; }
    }

    private void ShowAssist(AssistResult r)
    {
        int level = _state?.ZebraLevel ?? _settings.ZebraLevel;
        if (HistToggle.IsChecked == true)
        {
            Histogram.Source = BitmapSource.Create(256, 40, 96, 96, PixelFormats.Pbgra32, null, PictureAssist.DrawHistogram(r, 40, false, level), 256 * 4);
            OverText.Text = $"Over {level}%: {r.OverPercent:0.0}%     Under 3%: {r.UnderPercent:0.0}%";
            OverText.Foreground = (Brush)FindResource(r.OverPercent >= 0.5 ? "Warn" : "Dim");
        }
        if (r.Overlay.Length > 0)
        {
            if (_assistBitmap == null || _assistBitmap.PixelWidth != r.Width || _assistBitmap.PixelHeight != r.Height)
            {
                _assistBitmap = new WriteableBitmap(r.Width, r.Height, 96, 96, PixelFormats.Pbgra32, null);
                AssistLayer.Source = _assistBitmap;
            }
            _assistBitmap.WritePixels(new Int32Rect(0, 0, r.Width, r.Height), r.Overlay, r.Width * 4, 0);
        }
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
                    LinkDot.Fill = (Brush)FindResource("Rec");
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
        LinkDot.Fill = on ? (Brush)FindResource("Ok") : s.Link == "connecting" ? (Brush)FindResource("Warn") : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        LinkText.Text = on ? "Connected" : s.Link == "connecting" ? "The other CamRemote is connecting…" : "The other CamRemote has no camera";
        BatteryText.Text = s.Battery.Length > 0 ? "Battery " + s.Battery : "";
        CardText.Text = s.Card;
        AlertText.Text = s.Alert;
        AlertBox.Visibility = s.Alert.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecButton.IsEnabled = on;
        RecBadge.Visibility = s.Recording ? Visibility.Visible : Visibility.Collapsed;
        RecLabel.Text = s.Recording ? "Stop" : "Record";
        RecDot.Fill = s.Recording ? Brushes.White : (Brush)FindResource("Rec");
        if (s.Recording) RecButton.Background = (Brush)FindResource("Rec");
        else RecButton.ClearValue(BackgroundProperty);
        if (!on) NoSignal.Text = "The other CamRemote isn't connected to the camera";
        MeterText.Text = on && s.Meter is double m ? $"Meter {(m > 0 ? "+" : "")}{m:0.0} EV" : "";
        MeterText.Visibility = MeterText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyGuides();

        BaseIsoPanel.Children.Clear();
        foreach (long iso in on ? s.BaseIso : [])
        {
            var b = new Button { Content = $"Base ISO {iso}", Style = (Style)FindResource("PanelButton"), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) => _ = Do(_link.SetAsync("baseiso", iso.ToString()));
            BaseIsoPanel.Children.Add(b);
        }
        BaseIsoPanel.Visibility = BaseIsoPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FocusRing.Visibility = on && s.ManualFocus ? Visibility.Visible : Visibility.Collapsed;
        StopTracking.Visibility = on && s.Tracking ? Visibility.Visible : Visibility.Collapsed;
        ShowMagnify(s);

        var seen = new HashSet<string>();
        foreach (var row in s.Rows)
        {
            seen.Add(row.Id);
            var panel = row.Section switch { "exposure" => ExposureRows, "focus" => FocusRows, "audio" => AudioRows, _ => MoreRows };
            if (!_rows.TryGetValue(row.Id, out var view) || !view.Fits(row, panel))
            {
                if (view != null) ((Panel)view.Root.Parent).Children.Remove(view.Root);
                view = new RowView(row, panel, (id, value) => _ = Do(_link.SetAsync(id, value)));
                _rows[row.Id] = view;
                panel.Children.Add(view.Root);
            }
            view.Update(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            ((Panel)_rows[gone].Root.Parent).Children.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }
        AudioHead.Visibility = AudioRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreHead.Visibility = MoreRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMagnify(RemoteState s)
    {
        var ratios = s.Connected && s.MagnifyChoices.Length > 0 ? [0, .. s.MagnifyChoices] : Array.Empty<long>();
        if (MagnifyButtons.Children.Count != ratios.Length)
        {
            MagnifyButtons.Children.Clear();
            foreach (long r in ratios)
            {
                var b = new ToggleButton { Content = r == 0 ? "Off" : $"×{r / 10.0:0.#}", Tag = r, Style = (Style)FindResource("PanelToggle"), Margin = new Thickness(0, 0, 3, 0) };
                b.Click += (_, _) => _ = Do(_link.SetAsync("magnify", r.ToString()));
                MagnifyButtons.Children.Add(b);
            }
        }
        foreach (ToggleButton b in MagnifyButtons.Children) b.IsChecked = (long)b.Tag == s.Magnify;
        MagnifyBox.Visibility = ratios.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRecClick(object sender, RoutedEventArgs e)
    {
        if (_state?.Connected != true || DateTime.Now < _recBusyUntil) return;
        _recBusyUntil = DateTime.Now.AddSeconds(1.5); // a slow link shouldn't let a double tap stop it again
        _ = Do(_link.RecordAsync(!_state.Recording));
    }

    private void OnStopTrackingClick(object sender, RoutedEventArgs e) => _ = Do(_link.SetAsync("stoptracking", "0"));

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
        Toast.Visibility = Visibility.Visible;
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
            Root.Children.Add(new TextBlock { Text = row.Label, Style = (Style)Application.Current.FindResource("Label"), Margin = new Thickness(0, 0, 0, 3) });
            if (row.Kind == "Buttons" && row.Choices.Count <= 6)
            {
                var grid = new UniformGrid { Rows = 1 };
                foreach (var c in row.Choices)
                {
                    var b = new ToggleButton { Content = c.Text, Tag = c.Value, Style = (Style)Application.Current.FindResource("PanelToggle"), Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(4, 6, 4, 6) };
                    b.Click += (_, _) => { b.IsChecked = b.Tag as string == _row.Value; pick(_row.Id, (string)b.Tag); };
                    _buttons.Add(b);
                    grid.Children.Add(b);
                }
                Root.Children.Add(grid);
                return;
            }
            _list = new ComboBox { DisplayMemberPath = "Text", ItemsSource = row.Choices, MaxDropDownHeight = 420 };
            _list.SelectionChanged += (_, _) =>
            {
                if (!_updating && _list.SelectedItem is RemoteChoice c && c.Value != _row.Value) pick(_row.Id, c.Value);
            };
            if (row.Kind != "Stepper")
            {
                Root.Children.Add(_list);
                return;
            }
            _prev = new Button { Content = "◀", Width = 40, Style = (Style)Application.Current.FindResource("PanelButton") };
            _next = new Button { Content = "▶", Width = 40, Style = (Style)Application.Current.FindResource("PanelButton") };
            _prev.Click += (_, _) => Step(-1, pick);
            _next.Click += (_, _) => Step(1, pick);
            var line = new DockPanel();
            DockPanel.SetDock(_prev, Dock.Left);
            DockPanel.SetDock(_next, Dock.Right);
            _list.Margin = new Thickness(4, 0, 4, 0);
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
            _list.ToolTip = i < 0 ? row.Text : null; // a value the camera has but doesn't offer to set
            _updating = false;
            if (_prev != null) _prev.IsEnabled = i > 0;
            if (_next != null) _next.IsEnabled = i >= 0 && i < row.Choices.Count - 1;
        }
    }
}
