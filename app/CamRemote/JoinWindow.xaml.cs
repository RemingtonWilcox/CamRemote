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
    private readonly DispatcherTimer _poll, _hideMark, _hideToast, _lapse;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, RowView> _rows = [];
    private RemoteState? _state;
    private WriteableBitmap? _assistBitmap;
    private int _drawing; // 1 while a decoded frame waits for the UI
    private int _turn; // added to the host's own rotation, for this window only
    private int _misses; // state requests in a row that got no answer
    private DateTime _recBusyUntil;
    private DateTime? _recSince; // when this window first saw the take rolling
    private DateTime _lastFrameAt;
    private static readonly TimeSpan FrameGap = TimeSpan.FromSeconds(5); // this long without a new picture and the last one comes down
    private bool _polling;
    private bool _setup; // the Setup tab is showing
    private bool _shutterHeld;
    private Task<string?> _shutterPress = Task.FromResult<string?>(null); // a release waits for its press to reach the host

    // Where the host's settings go, so they sit where MainWindow puts them. The iPhone sends PTP codes, the PC and Mac SDK names.
    private static readonly HashSet<string> RecordingIds = ["53825", "53826", "53894", "53330", "53456", "53457",
        "Movie_File_Format", "Movie_Recording_Setting", "Movie_Recording_FrameRateSetting", "SQFrameRate", "SQRecordingFrameRateSetting", "SQRecordingSetting"];
    private static readonly HashSet<string> DriveIds = ["20499", "DriveMode"];
    private static readonly HashSet<string> PhotoIds = ["53843", "53842", "53763", "53777", "20491", "53467"];

    public JoinWindow(RemoteLink link, Settings settings)
    {
        InitializeComponent();
        _link = link;
        _settings = settings;
        _assist = new PictureAssist(new WpfUi(Dispatcher));
        _assist.Done += ShowAssist;
        HostText.Text = link.HostName.Length > 0 ? "Through the Egret on " + link.HostName : "Through the Egret at " + new Uri(link.Address).Host;
        CamSummary.Text = link.CameraName;
        AboutText.Text = $"Egret {UpdateCheck.Current} · Made by Remington Wilcox";
        HistogramChip.IsChecked = settings.ShowHistogram;
        ZebraChip.IsChecked = settings.ShowZebras;
        PeakingChip.IsChecked = settings.ShowPeaking;
        GridChip.IsChecked = settings.ShowGrid;
        ApplyGuides();
        FocusWheel.SizeChanged += (_, _) => DrawWheel();
        MakePresetButtons();
        MakeTimeLapseButtons();
        _bird = new BirdTrigger(new WpfUi(Dispatcher), new JoinedCamera(this), () => _latestFrame is { } f ? (f.Item1, f.Item2) : null,
            MainWindow.ShrinkForBird, FocusOnAnimal, () => Rotation);
        _bird.Status += text => BirdStatus.Text = text;
        _bird.Spotted += ShowAnimal;
        BirdBox.Visibility = BirdTrigger.Available ? Visibility.Visible : Visibility.Collapsed;
        MakeRecordSubjectButtons();
        _lapse = new DispatcherTimer(TimeSpan.FromSeconds(0.2), DispatcherPriority.Normal, async (_, _) => await LapseTickAsync(), Dispatcher);
        _lapse.Stop();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) => await RefreshAsync(), Dispatcher);
        _hideMark = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
        _hideMark.Tick += (_, _) => { FocusMark.Visibility = Visibility.Collapsed; _hideMark.Stop(); };
        _hideToast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hideToast.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _hideToast.Stop(); };
    }

    /// <summary>Takes the camera from the host to hold it on this computer. Null when it worked (and this window is closed), else why not.</summary>
    public Func<Task<string?>>? UseHere { get; init; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Write($"Joined the camera through {_link.Address}.");
        UseHereButton.Visibility = UseHere != null ? Visibility.Visible : Visibility.Collapsed;
        await RefreshAsync();
        _poll.Start();
        _ = Task.Run(() => _link.RunFramesAsync(OnJpeg, _stop.Token));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_lapse.IsEnabled) Log.Write($"Time-lapse stopped by leaving, after {_lapseTaken} photos.");
        _lapse.Stop();
        _bird.Stop();
        _poll.Stop();
        _stop.Cancel();
        _link.Dispose();
        Log.Write("Left the joined camera.");
    }

    private void OnLeaveClick(object sender, RoutedEventArgs e) => Close();

    private async void OnUseHereClick(object sender, RoutedEventArgs e)
    {
        UseHereButton.IsEnabled = false;
        if (await UseHere!() is not { } problem) return;
        ShowToast(problem);
        UseHereButton.IsEnabled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            if (_state?.Photo == true) _ = TakePhotoAsync();
            else OnRecClick(this, e);
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        // The same letters as MainWindow
        ToggleButton? chip = e.Key switch { Key.G => GridChip, Key.Z => ZebraChip, Key.P => PeakingChip, Key.H => HistogramChip, _ => null };
        if (chip != null)
        {
            chip.IsChecked = chip.IsChecked != true;
            OnGuideClick(chip, e);
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            Turn(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -90 : 90);
            e.Handled = true;
        }
    }

    // ---- Picture ----

    /// <summary>On the frame loop's thread. Frames that arrive while the last is still being drawn are dropped.</summary>
    private void OnJpeg(byte[] jpeg)
    {
        _latestFrame = Tuple.Create(jpeg, ++_frameNo); // for the animal trigger, even when this one isn't drawn
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
            if (!Live) return; // an old picture the host still had, from before the camera went
            _lastFrameAt = DateTime.Now;
            Picture.Source = bmp;
            NoSignal.Visibility = Visibility.Collapsed;
            BitmapSource src = bmp.Format == PixelFormats.Bgr32 || bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
            _assist.Offer(src.PixelWidth, src.PixelHeight, px => src.CopyPixels(px, src.PixelWidth * 4, 0));
        });
    }

    private bool Live => _state?.Connected == true && _misses < 3;

    /// <summary>Takes the last picture down when no more are coming, so a frozen frame never passes for the live one.</summary>
    private void ShowNoPicture(string why)
    {
        NoSignal.Text = why;
        NoSignal.Visibility = Visibility.Visible;
        if (Picture.Source == null) return;
        Picture.Source = null;
        _assistBitmap = null;
        AssistLayer.Source = null;
        Histogram.Source = null;
        OverText.Text = UnderText.Text = "";
        AnimalBox.Visibility = AnimalLabel.Visibility = Visibility.Collapsed;
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

    private void OnRotateClick(object sender, RoutedEventArgs e) => Turn(int.Parse((string)((Button)sender).Tag));

    private void Turn(int by)
    {
        _turn = ((_turn + by) % 360 + 360) % 360;
        ApplyRotation();
    }

    private void ApplyRotation()
    {
        int angle = ((_state?.Rotation ?? 0) + _turn) % 360;
        PictureRotation.Angle = angle;
        RotationText.Text = angle == 0 ? "" : $"{angle}°";
    }

    // ---- Guides ----

    private void OnGuideClick(object sender, RoutedEventArgs e)
    {
        _settings.ShowHistogram = HistogramChip.IsChecked == true;
        _settings.ShowZebras = ZebraChip.IsChecked == true;
        _settings.ShowPeaking = PeakingChip.IsChecked == true;
        _settings.ShowGrid = GridChip.IsChecked == true;
        _settings.Save();
        ApplyGuides();
    }

    private void ApplyGuides()
    {
        var s = _state;
        bool zebra = ZebraChip.IsChecked == true, peak = PeakingChip.IsChecked == true, hist = HistogramChip.IsChecked == true;
        _assist.Options = new AssistOptions(hist || zebra || peak, zebra, s?.ZebraLevel ?? _settings.ZebraLevel, peak,
            s?.PeakSensitivity ?? _settings.PeakSensitivity, s?.PeakColor ?? _settings.PeakColor);
        bool meter = s?.Connected == true && s.Meter != null;
        Hud.Visibility = hist || meter ? Visibility.Visible : Visibility.Collapsed;
        HistogramPart.Visibility = hist ? Visibility.Visible : Visibility.Collapsed;
        GuideGrid.Visibility = GridChip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (!zebra && !peak) { _assistBitmap = null; AssistLayer.Source = null; }
    }

    private void ShowAssist(AssistResult r)
    {
        if (Picture.Source == null) return; // worked out from a picture that's since come down
        int level = _state?.ZebraLevel ?? _settings.ZebraLevel;
        if (HistogramChip.IsChecked == true)
        {
            Histogram.Source = BitmapSource.Create(256, 40, 96, 96, PixelFormats.Pbgra32, null, PictureAssist.DrawHistogram(r, 40, false, level), 256 * 4);
            OverText.Text = $"Over {level}%: {r.OverPercent:0.0}%";
            OverText.Foreground = (Brush)FindResource(r.OverPercent >= 0.5 ? "Warn" : "Dim");
            UnderText.Text = $"Under 3%: {r.UnderPercent:0.0}%";
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
                    LinkText.Text = "Can't reach the other Egret";
                    RecButton.IsEnabled = ShutterButton.IsEnabled = false;
                    ShowNoPicture("Can't reach the other Egret. This picks up again by itself when it's back.");
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
        LinkText.Text = on ? "Connected" : s.Link == "connecting" ? "The other Egret is connecting…" : "The other Egret has no camera";
        BatteryText.Text = s.Battery.Length == 0 || s.Battery.StartsWith("Battery") ? s.Battery : "Battery " + s.Battery; // an Egret host already sends "Battery 80%"
        CardText.Text = s.Card;
        AlertText.Text = s.Alert;
        AlertBox.Visibility = s.Alert.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecButton.IsEnabled = ShutterButton.IsEnabled = on;
        ShowPage();
        SlowQuickSwitch.Visibility = on && !s.Photo && s.OffersSlowQuick ? Visibility.Visible : Visibility.Collapsed;
        NormalSpeed.IsChecked = !s.SlowQuick;
        SlowQuick.IsChecked = s.SlowQuick;
        ShowPresets(s);
        if (s.Recording) _recSince ??= DateTime.Now;
        else _recSince = null;
        RecBadge.Visibility = s.Recording ? Visibility.Visible : Visibility.Collapsed;
        RecTime.Text = _recSince is { } since ? $"● REC {DateTime.Now - since:hh\\:mm\\:ss}" : "● REC";
        RecFrame.BorderBrush = s.Recording ? (Brush)FindResource("Rec") : Brushes.Transparent;
        RecLabel.Text = s.Recording ? "Stop" : "Record";
        RecDot.Fill = s.Recording ? Brushes.White : (Brush)FindResource("Rec");
        if (s.Recording) RecButton.Background = (Brush)FindResource("Rec");
        else RecButton.ClearValue(BackgroundProperty);
        TimeLapseNote.Text = "A time-lapse is running on the other Egret. Stop it there.";
        TimeLapseNote.Visibility = s.TimeLapse ? Visibility.Visible : Visibility.Collapsed;
        ShowTimeLapse();
        if (!on) ShowNoPicture(s.Link == "connecting" ? "The other Egret is connecting to the camera…"
            : "The other Egret isn't connected to the camera. The picture comes back when it is.");
        else if (Picture.Source != null && DateTime.Now - _lastFrameAt > FrameGap) ShowNoPicture("The picture stopped coming. Waiting for it…");
        ShowMeter();
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
            var panel = PanelFor(row);
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
        PhotoHead.Visibility = PhotoRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AudioHead.Visibility = AudioRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreHead.Visibility = MoreRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Panel PanelFor(RemoteRow row) =>
        RecordingIds.Contains(row.Id) ? RecordingRows
        : DriveIds.Contains(row.Id) ? DriveRows
        : PhotoIds.Contains(row.Id) || row.Section == "photo" ? PhotoRows
        : row.Section switch { "exposure" => ExposureRows, "focus" => FocusRows, "audio" => AudioRows, _ => MoreRows };

    /// <summary>Video, Photo or Setup. Video and Photo follow the camera, as on the other Egret.</summary>
    private void ShowPage()
    {
        bool photo = _state?.Photo == true;
        VideoTab.IsChecked = !_setup && !photo;
        PhotoTab.IsChecked = !_setup && photo;
        SetupTab.IsChecked = _setup;
        ShootPage.Visibility = _setup ? Visibility.Collapsed : Visibility.Visible;
        SetupPage.Visibility = _setup ? Visibility.Visible : Visibility.Collapsed;
        RecButton.Visibility = VideoShoot.Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
        BirdBox.Visibility = photo || !BirdTrigger.Available ? Visibility.Collapsed : Visibility.Visible;
        ShutterButton.Visibility = DriveRows.Visibility = PhotoModes.Visibility = photo ? Visibility.Visible : Visibility.Collapsed;
        TimeLapseBox.Visibility = photo && _timeLapse ? Visibility.Visible : Visibility.Collapsed;
        StillMode.IsChecked = !_timeLapse;
        TimeLapseMode.IsChecked = _timeLapse;
    }

    // ---- Time-lapse ----
    // The iPhone app takes no time-lapse orders, so this computer times it and presses the shutter through the host.

    private static readonly int[] LapseIntervals = [1, 2, 5, 10, 30, 60]; // seconds
    private static readonly int[] LapseCounts = [100, 300, 600, 1000, 0]; // 0: until stopped
    private bool _timeLapse; // the Time-lapse mode is picked
    private int _lapseEvery = 2, _lapseShots = 300, _lapseTaken, _lapseFails;
    private DateTime _lapseNext;
    private bool _lapseFiring;

    private void MakeTimeLapseButtons()
    {
        foreach (int every in LapseIntervals)
        {
            var b = new ToggleButton { Content = every < 60 ? $"{every} s" : $"{every / 60} min", Tag = every, Margin = new Thickness(2), Padding = new Thickness(2, 5, 2, 5) };
            b.Click += (_, _) => { if (!_lapse.IsEnabled) _lapseEvery = every; ShowTimeLapse(); };
            IntervalButtons.Children.Add(b);
        }
        foreach (int shots in LapseCounts)
        {
            var b = new ToggleButton { Content = shots == 0 ? "∞" : shots.ToString("N0"), Tag = shots, Margin = new Thickness(2), Padding = new Thickness(2, 5, 2, 5),
                ToolTip = shots == 0 ? "Until you stop it" : null };
            b.Click += (_, _) => { if (!_lapse.IsEnabled) _lapseShots = shots; ShowTimeLapse(); };
            ShotButtons.Children.Add(b);
        }
    }

    private void OnPhotoModeClick(object sender, RoutedEventArgs e)
    {
        if (_lapse.IsEnabled) ShowToast("Stop the time-lapse first.");
        else _timeLapse = sender == TimeLapseMode;
        ShowPage();
        ShowTimeLapse();
    }

    private void ShowTimeLapse()
    {
        bool running = _lapse.IsEnabled;
        foreach (ToggleButton b in IntervalButtons.Children) { b.IsChecked = (int)b.Tag == _lapseEvery; b.IsEnabled = !running; }
        foreach (ToggleButton b in ShotButtons.Children) { b.IsChecked = (int)b.Tag == _lapseShots; b.IsEnabled = !running; }
        string every = _lapseEvery < 60 ? $"{_lapseEvery} s" : $"{_lapseEvery / 60} min";
        if (running)
        {
            int next = Math.Max(0, (int)Math.Ceiling((_lapseNext - DateTime.Now).TotalSeconds));
            TimeLapsePlan.Text = (_lapseShots > 0 ? $"Photo {_lapseTaken:N0} of {_lapseShots:N0}" : $"{_lapseTaken:N0} photos so far") + $", the next in {next} s.";
        }
        else if (_lapseShots > 0)
        {
            var length = TimeSpan.FromSeconds((double)_lapseEvery * (_lapseShots - 1));
            string takes = length.TotalHours >= 1 ? $"{(int)length.TotalHours} h {length.Minutes} min" : length.TotalMinutes >= 1 ? $"{(int)length.TotalMinutes} min" : $"{length.Seconds} s";
            TimeLapsePlan.Text = $"{_lapseShots:N0} photos, one every {every}: about {takes}, and {_lapseShots / 24.0:0.#} s of video at 24p. Keep this window open while it runs.";
        }
        else TimeLapsePlan.Text = $"One photo every {every} until you stop it. Keep this window open while it runs.";
        ShutterLabel.Text = _state?.TimeLapse == true ? "Time-lapse running" : running ? "Stop time-lapse" : _timeLapse ? "Start time-lapse" : "Take photo";
        ShutterButton.ToolTip = _timeLapse ? "Start or stop the time-lapse (F9)" : "Take a photo (F9). Hold it down for a burst when Drive is set to continuous.";
    }

    private void StartOrStopTimeLapse()
    {
        if (_lapse.IsEnabled)
        {
            StopTimeLapse($"Time-lapse stopped after {_lapseTaken:N0} photos.");
            return;
        }
        if (_state?.Connected != true) return;
        _lapseTaken = _lapseFails = 0;
        _lapseNext = DateTime.Now; // the first one straight away
        _lapse.Start();
        Log.Write($"Time-lapse through the other Egret: {(_lapseShots > 0 ? _lapseShots.ToString() : "unlimited")} photos, one every {_lapseEvery} s.");
        ShowTimeLapse();
    }

    private void StopTimeLapse(string message)
    {
        _lapse.Stop();
        Log.Write(message);
        ShowToast(message);
        ShowTimeLapse();
    }

    private async Task LapseTickAsync()
    {
        if (_lapseFiring) return;
        if (DateTime.Now < _lapseNext) { ShowTimeLapse(); return; }
        _lapseFiring = true;
        _lapseNext = _lapseNext.AddSeconds(_lapseEvery);
        if (_lapseNext < DateTime.Now) _lapseNext = DateTime.Now.AddSeconds(_lapseEvery); // fell behind on a slow link: don't catch up in a rush
        try
        {
            string? problem = await _link.ShutterAsync(true);
            await _link.ShutterAsync(false);
            if (!_lapse.IsEnabled) return; // stopped while that photo was on its way
            if (problem != null)
            {
                if (++_lapseFails >= 3) StopTimeLapse($"Time-lapse stopped after {_lapseTaken:N0} photos: {problem}");
                else ShowToast(problem);
                return;
            }
            _lapseFails = 0;
            if (++_lapseTaken >= _lapseShots && _lapseShots > 0) StopTimeLapse($"Time-lapse done: {_lapseTaken:N0} photos.");
            else ShowTimeLapse();
        }
        finally
        {
            _lapseFiring = false;
        }
    }

    private void OnSetupClick(object sender, RoutedEventArgs e)
    {
        _setup = true;
        ShowPage();
    }

    // ---- The camera's meter, drawn as in MainWindow ----

    private void ShowMeter()
    {
        var stops = _state?.Connected == true ? _state.Meter : null;
        MeterBox.Visibility = stops == null ? Visibility.Collapsed : Visibility.Visible;
        if (stops is not double s) return;
        Canvas.SetLeft(ExposureNeedle, MeterX(s) - 1);
        ExposureText.Text = Format.Meter(s);
        ExposureNeedle.Fill = Math.Abs(s) <= 0.35 ? (Brush)FindResource("Ok") : Math.Abs(s) >= 2 ? (Brush)FindResource("Warn") : Brushes.White;
        ExposureText.Foreground = ExposureNeedle.Fill;
    }

    // -3 to +3 stops across the meter, with room at the ends for the numbers.
    private double MeterX(double stops) => 10 + (Math.Clamp(stops, -3.1, 3.1) + 3) * (ExposureScale.ActualWidth - 20) / 6;

    private void OnExposureScaleSized(object sender, SizeChangedEventArgs e)
    {
        ExposureScale.Children.Clear();
        for (int stop = -3; stop <= 3; stop++)
        {
            double h = stop == 0 ? 18 : 8;
            var tick = new System.Windows.Shapes.Rectangle { Width = 1, Height = h, Fill = (Brush)FindResource("Dim") };
            Canvas.SetLeft(tick, MeterX(stop));
            Canvas.SetTop(tick, (18 - h) / 2);
            var number = new TextBlock { Text = stop == 0 ? "0" : $"{stop:+0;-0}", Width = 24, TextAlignment = TextAlignment.Center, FontSize = 10, Foreground = (Brush)FindResource("Dim") };
            Canvas.SetLeft(number, MeterX(stop) - 12);
            Canvas.SetTop(number, 18);
            ExposureScale.Children.Add(tick);
            ExposureScale.Children.Add(number);
        }
        ExposureScale.Children.Add(ExposureNeedle);
        ShowMeter();
    }

    // ---- Focus ring ----

    private const double WheelTickGap = 14;
    private double _wheelLastX, _wheelTurned, _wheelOwed;

    private void DrawWheel()
    {
        WheelTicks.Children.Clear();
        for (int i = -1; i < 40; i++)
        {
            bool major = i % 5 == 0;
            var tick = new System.Windows.Shapes.Rectangle { Width = 1, Height = major ? 24 : 12, Fill = major ? Brushes.Gray : Brushes.DimGray };
            Canvas.SetLeft(tick, i * WheelTickGap + _wheelTurned % (WheelTickGap * 5));
            Canvas.SetTop(tick, major ? 10 : 16);
            WheelTicks.Children.Add(tick);
        }
    }

    /// <summary>The host turns the ring in steps, so a drag sends one for each tick it passes, bigger ones when it's quick.</summary>
    private void TurnWheel(double pixels)
    {
        _wheelTurned += pixels;
        _wheelOwed += pixels;
        DrawWheel();
        int ticks = (int)(_wheelOwed / WheelTickGap);
        if (ticks == 0) return;
        _wheelOwed -= ticks * WheelTickGap;
        int step = Math.Abs(ticks) >= 3 ? Math.Sign(ticks) * 3 : ticks;
        _ = Do(_link.FocusStepAsync(step), quiet: true);
    }

    private void OnWheelDown(object sender, MouseButtonEventArgs e)
    {
        _wheelLastX = e.GetPosition(FocusWheel).X;
        FocusWheel.CaptureMouse();
    }

    private void OnWheelMove(object sender, MouseEventArgs e)
    {
        if (!FocusWheel.IsMouseCaptured) return;
        double x = e.GetPosition(FocusWheel).X;
        TurnWheel(x - _wheelLastX);
        _wheelLastX = x;
    }

    private void OnWheelUp(object sender, MouseButtonEventArgs e) => FocusWheel.ReleaseMouseCapture();

    private void OnWheelScroll(object sender, MouseWheelEventArgs e)
    {
        _wheelTurned += e.Delta > 0 ? WheelTickGap : -WheelTickGap;
        DrawWheel();
        _ = Do(_link.FocusStepAsync(e.Delta > 0 ? 2 : -2), quiet: true);
        e.Handled = true;
    }

    private void OnFocusStep(object sender, RoutedEventArgs e) => _ = Do(_link.FocusStepAsync(int.Parse((string)((FrameworkElement)sender).Tag)), quiet: true);

    // ---- Setup ----

    private async void OnSendLogClick(object sender, RoutedEventArgs e)
    {
        SendLogButton.IsEnabled = false;
        SendLogResult.Text = "Sending…";
        var (reference, problem) = await LogSender.SendAsync(LogNote.Text);
        SendLogResult.Text = reference != null ? $"Sent, thank you. Its reference is {reference}." : problem;
        if (reference != null) LogNote.Text = "";
        SendLogButton.IsEnabled = true;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) => UpdateCheck.OpenHelp();

    private void OnInstagramClick(object sender, RoutedEventArgs e) => UpdateCheck.OpenInstagram();

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
        SendRecord(!_state.Recording);
    }

    private async void SendRecord(bool on)
    {
        _recordWaiting = true;
        _recordSent = DateTime.Now;
        try { await Do(_link.RecordAsync(on)); }
        finally { _recordWaiting = false; }
    }

    // ---- Automatic recording ----
    // The animal detector runs here on the picture the other Egret sends, and starts and stops the take through it.

    private readonly BirdTrigger _bird;
    private volatile Tuple<byte[], long>? _latestFrame;
    private long _frameNo;
    private bool _recordWaiting;
    private DateTime _recordSent;
    private static readonly int[] RecordStopChoices = [10, 15, 30, 60]; // seconds

    private int Rotation => ((_state?.Rotation ?? 0) + _turn) % 360;

    /// <summary>The other Egret's camera as the trigger sees it.</summary>
    private sealed class JoinedCamera(JoinWindow w) : ITriggerCamera
    {
        public bool Connected => w.Live;
        public bool IsRecording => w._state?.Recording == true;
        public bool RecordWaiting => w._recordWaiting;
        public DateTime RecordSent => w._recordSent;
        public bool IsPhotoMode => w._state?.Photo == true;
        public bool IsFormatting => false;
        public void ToggleRecord() => w.SendRecord(!IsRecording);
    }

    private void MakeRecordSubjectButtons()
    {
        _bird.Subject = RecordSubject.Named(_settings.RecordSubject);
        _bird.QuietStop = TimeSpan.FromSeconds(_settings.RecordStopAfter);
        _bird.FocusOnIt = _settings.RecordFocus;
        RecordFocus.IsChecked = _settings.RecordFocus;
        foreach (var subject in RecordSubject.All)
            RecordSubjectButtons.Children.Add(OptionButton(subject.Name, subject.Tip, () => _bird.Subject == subject, () =>
            {
                _bird.Subject = subject;
                _settings.RecordSubject = subject.Name;
                AnimalLabel.Text = subject.Name.ToLowerInvariant();
            }, RecordSubjectButtons));
        foreach (int seconds in RecordStopChoices)
            RecordStopButtons.Children.Add(OptionButton(seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min", null, () => _bird.QuietStop.TotalSeconds == seconds, () =>
            {
                _bird.QuietStop = TimeSpan.FromSeconds(seconds);
                _settings.RecordStopAfter = seconds;
            }, RecordStopButtons));
        AnimalLabel.Text = _bird.Subject.Name.ToLowerInvariant();
    }

    private ToggleButton OptionButton(string text, string? tip, Func<bool> isOn, Action pick, Panel row)
    {
        var b = new ToggleButton { Content = text, Margin = new Thickness(2), Padding = new Thickness(6, 5, 6, 5), HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = tip, IsChecked = isOn(), Tag = isOn };
        b.Click += (_, _) =>
        {
            pick();
            _settings.Save();
            foreach (var other in row.Children.OfType<ToggleButton>()) other.IsChecked = ((Func<bool>)other.Tag)();
        };
        return b;
    }

    private void OnRecordFocusClick(object sender, RoutedEventArgs e)
    {
        _bird.FocusOnIt = _settings.RecordFocus = RecordFocus.IsChecked == true;
        _settings.Save();
    }

    private void OnBirdWatchClick(object sender, RoutedEventArgs e)
    {
        bool on = BirdWatch.IsChecked == true;
        if (on) _bird.Start();
        else _bird.Stop();
        BirdStatus.Visibility = RecordSubjectPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusOnAnimal(double fx, double fy)
    {
        if (_state?.Connected != true) return;
        ShowFocusMark(fx * Picture.ActualWidth, fy * Picture.ActualHeight);
        _ = Do(_link.FocusAsync(fx, fy), quiet: true);
    }

    /// <summary>Draws the box around what the trigger sees. The label stays upright however the picture is turned.</summary>
    private void ShowAnimal((double X, double Y, double W, double H)? box)
    {
        double w = Picture.ActualWidth, h = Picture.ActualHeight;
        bool show = box != null && w > 0 && (_state?.Magnify ?? 0) == 0;
        AnimalBox.Visibility = AnimalLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var b = box!.Value;
        AnimalBox.Width = Math.Max(b.W * w, 24);
        AnimalBox.Height = Math.Max(b.H * h, 24);
        double left = b.X * w - AnimalBox.Width / 2, top = b.Y * h - AnimalBox.Height / 2;
        Canvas.SetLeft(AnimalBox, left);
        Canvas.SetTop(AnimalBox, top);
        int turn = Rotation;
        Canvas.SetLeft(AnimalLabel, turn is 0 or 90 ? left : left + AnimalBox.Width);
        Canvas.SetTop(AnimalLabel, turn is 0 or 270 ? top : top + AnimalBox.Height);
        AnimalLabelTurn.Angle = -turn;
    }

    private void OnStopTrackingClick(object sender, RoutedEventArgs e) => _ = Do(_link.SetAsync("stoptracking", "0"));

    /// <summary>The tabs always show what the camera is doing, so a click asks the host and waits for it to change.</summary>
    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        bool photo = sender == PhotoTab;
        if (!photo && _lapse.IsEnabled)
        {
            ShowToast("Stop the time-lapse first.");
            ShowPage();
            return;
        }
        _setup = false;
        ShowPage();
        if (photo != (_state?.Photo == true)) _ = Do(_link.ModeAsync(photo));
    }

    private void OnSlowQuickClick(object sender, RoutedEventArgs e)
    {
        NormalSpeed.IsChecked = _state?.SlowQuick != true;
        SlowQuick.IsChecked = _state?.SlowQuick == true;
        _ = Do(_link.SlowQuickAsync(sender == SlowQuick));
    }

    // ---- Photo ----

    // Holding it down is what makes a burst, so the press and the release go to the host separately.
    private void OnShutterDown(object sender, MouseButtonEventArgs e)
    {
        if (_timeLapse)
        {
            StartOrStopTimeLapse();
            return;
        }
        if (_state?.Connected != true || _shutterHeld) return;
        _shutterHeld = true;
        _shutterPress = _link.ShutterAsync(true);
        _ = Do(_shutterPress);
    }

    private async void OnShutterUp(object sender, MouseEventArgs e)
    {
        if (!_shutterHeld) return;
        _shutterHeld = false;
        await _shutterPress;
        await Do(_link.ShutterAsync(false), quiet: true);
    }

    private async Task TakePhotoAsync()
    {
        if (_timeLapse)
        {
            StartOrStopTimeLapse();
            return;
        }
        if (_state?.Connected != true || _shutterHeld) return;
        if (await _link.ShutterAsync(true) is { } problem)
        {
            ShowToast(problem);
            return;
        }
        await _link.ShutterAsync(false);
    }

    // ---- Presets ----

    private const int PresetSlots = 4;

    /// <summary>C1 to C4, kept on the other CamRemote. A click on an empty one saves the camera's settings there; on a full one, puts them back.</summary>
    private void MakePresetButtons()
    {
        for (int slot = 1; slot <= PresetSlots; slot++)
        {
            int s = slot;
            // A row each, as on the other CamRemote: its first line, with the second on the right, then the third below.
            var name = new TextBlock { Text = $"C{s}", FontSize = 12, Foreground = (Brush)FindResource("Dim"), Width = 28, Margin = new Thickness(0, 2, 0, 0) };
            var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var right = new TextBlock { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var detail = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("Dim"), TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(name, Dock.Left);
            var row = new DockPanel { Children = { name, new StackPanel { Children = { new DockPanel { Children = { right, title } }, detail } } } };
            var b = new Button { Style = (Style)FindResource("Tile"), Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(2), Tag = new[] { title, right, detail }, Content = row, IsEnabled = false };
            b.Click += (_, _) => _ = Do(_link.PresetAsync(s, save: PresetIn(s) == null));
            var save = new MenuItem { Header = $"Save current settings as C{s}" };
            save.Click += (_, _) => _ = Do(_link.PresetAsync(s, save: true));
            b.ContextMenu = new ContextMenu { Items = { save } };
            PresetButtons.Children.Add(b);
        }
    }

    private RemotePreset? PresetIn(int slot) => (_state?.Presets ?? []).FirstOrDefault(p => p.Slot == slot);

    private void ShowPresets(RemoteState s)
    {
        string page = s.Photo ? "photo" : "video";
        for (int i = 0; i < PresetButtons.Children.Count; i++)
        {
            var b = (Button)PresetButtons.Children[i];
            var preset = PresetIn(i + 1);
            var lines = (TextBlock[])b.Tag!;
            List<string> said = preset == null ? ["Empty", "", "Click to save here"] : preset.Lines ?? [];
            string Line(int n) => n < said.Count ? said[n] : "";
            lines[0].Text = Line(0).Length > 0 ? Line(0) : "Saved";
            lines[1].Text = Line(1);
            lines[2].Text = Line(2);
            lines[1].Visibility = lines[1].Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            lines[2].Visibility = lines[2].Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Green, with a green outline, when the camera is set that way now.
            bool now = preset != null && s.ActiveSlot == i + 1;
            lines[0].Foreground = preset == null ? (Brush)FindResource("Dim") : now ? (Brush)FindResource("Ok") : Brushes.White;
            b.BorderBrush = now ? (Brush)FindResource("Ok") : Brushes.Transparent;
            b.IsEnabled = s.Connected;
            b.ToolTip = preset == null ? $"Click to save the camera's {page} settings here."
                : $"Click to put these {page} settings back on the camera. Right-click to save over them.";
        }
    }

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
