using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CamRemote;

public partial class MainWindow : Window
{
    private readonly CameraService _cam;
    private readonly VideoFeed _video;
    private readonly IUi _ui;
    private readonly LiveViewFeed<BitmapSource> _live;
    private readonly PictureAssist _assist;
    private WriteableBitmap? _assistBitmap;
    private bool _loadingAssist;
    private DateTime _assistDetailClosed;
    private readonly AudioMeter _meter = new();
    private readonly DispatcherTimer _meterTick;
    private readonly double[] _hold = [-100, -100];
    private DateTime _holdSince = DateTime.Now;
    private bool _useWifi; // picture from the camera's live view instead of the capture card
    private string _activeSource = ""; // Id of the picture source that's running
    private readonly Settings _settings = Settings.Load();
    private readonly List<PropControl> _rows = [];
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _hideFocusMark;
    private DateTime? _recStart;
    private bool _sawNotRecording; // this run saw the take begin, so its start time is exact
    private DateTime? _idleSince; // when the camera first said it wasn't recording
    private long? _lastMode;
    private readonly AutoReconnect _auto;
    private bool _connectingByHand; // the Connect dialog is open or its connect is running
    private bool _wasConnected; // connected at some point this run, so a drop is worth a banner
    private PhoneServer? _phone;
    private int _phoneCall; // the latest SetPhoneAsync, so an older one that finishes late steps aside
    private bool _phoneWatching; // a phone wants the live view, even if this window shows a capture card

    public MainWindow()
    {
        InitializeComponent();
        var ui = new WpfUi(Dispatcher);
        _ui = ui;
        _cam = new CameraService(ui);
        _auto = new AutoReconnect(_cam, _settings, ShowMessage, ConnectWindow.AskFingerprint);
        _auto.StatusChanged += OnReconnectStatus;
        _video = new VideoFeed(Dispatcher);
        _video.BitmapChanged += () =>
        {
            if (_useWifi) return;
            Picture.Source = _video.Bitmap;
            NoSignal.Visibility = Visibility.Collapsed;
        };
        _assist = new PictureAssist(ui);
        _assist.Done += ShowAssist;
        _video.Drew += () =>
        {
            if (!_useWifi && _video.Bitmap != null) OfferToAssist(_video.Bitmap);
        };
        _live = new LiveViewFeed<BitmapSource>(ui, DecodeJpeg);
        _live.Frame += bmp =>
        {
            if (!_useWifi) return;
            Picture.Source = bmp;
            NoSignal.Visibility = Visibility.Collapsed;
            OfferToAssist(bmp);
        };
        _cam.Message += ShowMessage;
        _cam.StateChanged += OnLinkChanged;
        _cam.PropsChanged += OnPropsChanged;
        _cam.FramesChanged += ShowFrames;
        _cam.FocusMoved += ShowFocusBar;

        var big = PropControl.Kind.Stepper;
        var pick = PropControl.Kind.Picker;
        Add(ExposurePanel, "ISO", PropCode.IsoSensitivity, Format.Iso, big, v => (v & 0xFFFFFF) == 0xFFFFFF ? -1 : v & 0xFFFFFF);
        Add(ExposurePanel, "Shutter", PropCode.ShutterSpeed, Format.Shutter, big, v => -Format.ShutterSeconds(v));
        Add(ExposurePanel, "Iris", PropCode.FNumber, Format.FNumber, big, v => v);
        Add(FocusPanel, "Focus mode", PropCode.FocusMode, Format.Named("FocusMode"), PropControl.Kind.Buttons);
        Add(FocusPanel, "Tap does", PropCode.FunctionOfTouchOperation, Format.Named("TouchFunction"), PropControl.Kind.Buttons);
        Add(FocusPanel, "Detect", PropCode.RecognitionTarget, Format.Named("RecognitionTarget"), PropControl.Kind.Buttons);
        Add(FocusPanel, "Face/eye priority", PropCode.SubjectRecognitionInAF, Format.Named("SubjectInAf"), PropControl.Kind.Buttons);
        Add(FocusPanel, "Which eye", PropCode.RightLeftEyeSelect, Format.Named("EyeSelect"), PropControl.Kind.Buttons);
        Add(AudioPanel, "Record level", PropCode.AudioInputMasterLevel, v => v.ToString(), big, v => v);
        Add(AudioPanel, "Audio recording", PropCode.AudioRecording, Format.Named("AudioRecording"), PropControl.Kind.Buttons);
        Add(AudioPanel, "Wind noise reduction", PropCode.WindNoiseReduct, Format.Named("WindNoise"), PropControl.Kind.Buttons);
        Add(MorePanel, "Exposure mode", PropCode.ExposureProgramMode, Format.Named("ExposureProgram"), pick);
        Add(MorePanel, "White balance", PropCode.WhiteBalance, Format.Named("WhiteBalance"), pick);
        Add(MorePanel, "Color temp", PropCode.Colortemp, Format.ColorTemp, pick, v => v);
        Add(MorePanel, "Focus area", PropCode.FocusArea, Format.Named("FocusArea"), pick);
        Add(MorePanel, "Touch operation", PropCode.TouchOperation, Format.Named("TouchOperation"), PropControl.Kind.Buttons);
        Add(MorePanel, "Shooting mode", PropCode.MovieShootingMode, Format.Named("MovieShootingMode"), pick);
        Add(MorePanel, "Monitor LUT", PropCode.MonitorLUTSetting, Format.Named("MonitorLut"), PropControl.Kind.Buttons);
        Add(MorePanel, "File format", PropCode.Movie_File_Format, Format.Named("MovieFileFormat"), pick);
        Add(MorePanel, "Rec setting", PropCode.Movie_Recording_Setting, Format.Named("MovieRecordingSetting"), pick);
        Add(MorePanel, "Frame rate", PropCode.Movie_Recording_FrameRateSetting, Format.Named("MovieFrameRate"), pick);
        Add(MorePanel, "HDMI out", PropCode.Movie_HDMIOutputResolution, Format.Named("HdmiResolution"), pick);
        OnPropsChanged();
        ApplyRotation();
        SetUpAssist();
        DrawWheel();

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        _meterTick = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => DrawMeters(), Dispatcher);
        UpdateMeter(null);
        _hideFocusMark = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
        _hideFocusMark.Tick += (_, _) => { FocusMark.Visibility = Visibility.Collapsed; _hideFocusMark.Stop(); };
    }

    private static (BitmapSource, int, int) DecodeJpeg(byte[] buffer, int length)
    {
        var bmp = new BitmapImage();
        using (var ms = new System.IO.MemoryStream(buffer, 0, length, false))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        return (bmp, bmp.PixelWidth, bmp.PixelHeight);
    }

    private void OfferToAssist(BitmapSource frame)
    {
        BitmapSource source = frame.Format == PixelFormats.Bgr32 || frame.Format == PixelFormats.Bgra32
            ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        _assist.Offer(source.PixelWidth, source.PixelHeight, pixels => source.CopyPixels(pixels, source.PixelWidth * 4, 0));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.AgreedVersion < Agreement.Version)
        {
            if (MessageBox.Show(this, Agreement.Text, "Before you start", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            {
                Close();
                return;
            }
            _settings.AgreedVersion = Agreement.Version;
            _settings.Save();
        }
        await LoadSourcesAsync();
        await SetPhoneAsync(_settings.PhoneEnabled);
        _ = ShowUpdateAsync();
        await TryReconnectAsync();
    }

    /// <summary>Lists Wi-Fi plus every capture card Windows sees, and selects the saved one (Wi-Fi if it's gone).</summary>
    private async Task LoadSourcesAsync()
    {
        try
        {
            var devices = await VideoFeed.ListAsync();
            devices.Insert(0, WifiSource);
            VideoDevices.ItemsSource = devices;
            VideoDevices.SelectedItem = devices.FirstOrDefault(d => d.Id == _settings.VideoDeviceId) ?? WifiSource;
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't list video devices: " + ex.Message);
        }
    }

    private async void OnRefreshSourcesClick(object sender, RoutedEventArgs e) => await LoadSourcesAsync();

    private async Task ShowUpdateAsync()
    {
        if (await UpdateCheck.NewerVersionAsync() is not { } version) return;
        UpdateButton.Content = $"CamRemote {version} is out. Click to download";
        UpdateButton.Visibility = Visibility.Visible;
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) => UpdateCheck.OpenDownloadPage();

    /// <summary>
    /// With auto-connect on, keeps trying the saved camera while it's off or out of range, and after a drop,
    /// including at a new IP. <see cref="AutoReconnect"/> decides when; this says whether it may.
    /// </summary>
    private Task TryReconnectAsync() =>
        _auto.TickAsync(_settings.AgreedVersion >= Agreement.Version && !_connectingByHand && _settings.AutoConnect);

    /// <summary>Before the camera has ever connected, the reconnect status goes in the message line; after, in the banner.</summary>
    private void OnReconnectStatus()
    {
        if (!_wasConnected && _auto.Status is { } status) ShowMessage(status);
        OnPropsChanged();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _tick.Stop();
        _live.Stop();
        _meter.Dispose();
        _cam.Dispose(); // before any await: the window may be gone by the time an await resumes, and this thread would keep the app alive
        if (_phone != null) await _phone.DisposeAsync();
        await _video.DisposeAsync();
    }

    // ---- Video ----

    private async void OnVideoDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoDevices.SelectedItem is not VideoDevice dev) return;
        if (dev.Id == _activeSource) return; // the list was refreshed; keep the running picture
        _activeSource = dev.Id;
        _settings.VideoDeviceId = dev.Id;
        _settings.Save();
        Picture.Source = null;
        ClearAssist();
        _useWifi = dev == WifiSource;
        UpdateLiveView();
        UpdateMeter(_useWifi ? null : dev);
        NoSignal.Visibility = Visibility.Visible;
        try
        {
            if (_useWifi) await _video.StopAsync();
            else await _video.StartAsync(dev.Id);
        }
        catch (Exception ex)
        {
            ShowMessage($"Couldn't open {dev.Name}: {ex.Message} (is another app using it, like OBS or the capture card's own app?)");
        }
    }

    private static readonly VideoDevice WifiSource = new("wifi", "Camera over Wi-Fi (live view)");

    /// <summary>Runs the live-view reader only while it's the chosen picture and the camera is connected.</summary>
    private void UpdateLiveView()
    {
        if ((_useWifi || _phoneWatching) && _cam.State == LinkState.Connected) _live.Start();
        else _live.Stop();
        NoSignal.Text = _useWifi
            ? _cam.State == LinkState.Connected ? "Waiting for the camera's picture…" : "Connect the camera to see its picture"
            : "No video — pick a picture source on the right";
    }

    private void OnPictureClick(object sender, MouseButtonEventArgs e)
    {
        if (_cam.State != LinkState.Connected || Picture.ActualWidth <= 0) return;
        var p = e.GetPosition(Picture);
        double fx = p.X / Picture.ActualWidth, fy = p.Y / Picture.ActualHeight;
        if (_zoom.Ratio > 0)
        {
            _zoom.MoveTo(fx, fy);
            ApplyZoom();
            return;
        }
        var problem = _cam.TouchFocus(fx, fy);
        if (problem != null)
        {
            ShowMessage(problem);
            return;
        }
        Canvas.SetLeft(FocusMark, p.X - FocusMark.Width / 2);
        Canvas.SetTop(FocusMark, p.Y - FocusMark.Height / 2);
        FocusMark.Visibility = Visibility.Visible;
        _hideFocusMark.Stop();
        _hideFocusMark.Start();
    }

    private void OnRotateClick(object sender, RoutedEventArgs e)
    {
        _settings.Rotation = ((_settings.Rotation + int.Parse((string)((Button)sender).Tag)) % 360 + 360) % 360;
        _settings.Save();
        ApplyRotation();
    }

    /// <summary>
    /// Turns the picture for a camera on its side. Click-to-focus still lands in the right spot,
    /// because clicks are read in the picture's own (unrotated) coordinates.
    /// </summary>
    private void ApplyRotation()
    {
        PictureRotation.Angle = _settings.Rotation;
        RotationText.Text = $"Rotation {_settings.Rotation}°";
    }

    private void OnPictureRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_cam.State != LinkState.Connected) return;
        if (_cam.CancelTouch() is { } problem) ShowMessage(problem);
    }

    private static readonly SolidColorBrush FrameFocusedBrush = new(Color.FromRgb(0x4C, 0xD9, 0x64)), FrameOtherBrush = new(Colors.White);

    /// <summary>Draws the boxes the camera has on its own screen. While magnified they don't line up with the picture.</summary>
    private void ShowFrames()
    {
        FrameLayer.Children.Clear();
        double w = Picture.ActualWidth, h = Picture.ActualHeight;
        if (_cam.MagnifyRatio == 0 && _zoom.Ratio == 0 && w > 0)
        {
            foreach (var f in _cam.FramesToShow)
            {
                var box = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(f.W * w, 6),
                    Height = Math.Max(f.H * h, 6),
                    Stroke = f.Focused ? FrameFocusedBrush : FrameOtherBrush,
                    StrokeThickness = f.Kind == FrameKind.Focus ? 1.5 : 2.5,
                };
                Canvas.SetLeft(box, f.X * w - box.Width / 2);
                Canvas.SetTop(box, f.Y * h - box.Height / 2);
                FrameLayer.Children.Add(box);
            }
        }
        if (_cam.IsTracking != _showedTracking) ShowFocusTools();
    }

    private static readonly SolidColorBrush ZoomBrush = new(Color.FromRgb(0xF2, 0xC9, 0x4C));
    private bool _showedTracking;
    private readonly PictureZoom _zoom = new();

    private void ApplyZoom()
    {
        var (x, y) = _zoom.Origin;
        foreach (var layer in new FrameworkElement[] { Picture, AssistLayer })
        {
            layer.RenderTransformOrigin = new Point(x, y);
            layer.RenderTransform = new ScaleTransform(_zoom.Scale, _zoom.Scale);
        }
        ShowFrames();
        ShowFocusTools();
    }

    private void OnZoomBadgeClick(object sender, RoutedEventArgs e) => OnMagnifyClick(0);

    private void OnMagnifyClick(long ratio)
    {
        if (_cam.MagnifyChoices().Count > 0) _cam.Magnify(ratio);
        else
        {
            _zoom.Set(ratio);
            ApplyZoom();
        }
    }

    // ---- Camera ----

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_cam.State != LinkState.Disconnected)
        {
            _cam.Disconnect();
            return;
        }
        _connectingByHand = true; // auto-reconnect waits, so it can't race the dialog or pop up over it
        try
        {
            var dlg = new ConnectWindow(_cam, _settings) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _cam.HoldReconnect = false;
            await dlg.ConnectAsync(ShowMessage);
        }
        finally
        {
            _connectingByHand = false;
        }
    }

    private void OnRecClick(object sender, RoutedEventArgs e) => _cam.ToggleRecord();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9 && _cam.State == LinkState.Connected)
        {
            _cam.ToggleRecord();
            e.Handled = true;
        }
        else if (e.Key == Key.F12 && _cam.State == LinkState.Connected)
        {
            // Diagnostics: every property the camera reports, with names.
            var path = System.IO.Path.Combine(Settings.Folder, "props.txt");
            var lines = _cam.Props.Values.OrderBy(p => p.Code).Select(p =>
                $"{(Enum.IsDefined(typeof(PropCode), p.Code) ? ((PropCode)p.Code).ToString() : $"0x{p.Code:X}"),-45} " +
                $"value=0x{p.Value:X} type=0x{p.Type:X} writable={p.Writable} enable={p.Enable} " +
                $"{(p.Str != null ? $"str=\"{p.Str}\" " : "")}choices=[{string.Join(",", p.Candidates.Select(c => $"0x{c:X}"))}]");
            System.IO.File.WriteAllLines(path, lines);
            ShowMessage("Saved camera properties to " + path);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.H or Key.Z or Key.P or Key.G)
        {
            var chip = e.Key switch { Key.H => HistogramChip, Key.Z => ZebraChip, Key.P => PeakingChip, _ => GridChip };
            chip.IsChecked = chip.IsChecked != true;
            ApplyAssist();
            e.Handled = true;
        }
    }

    /// <summary>Names the connected camera in the title, and offers its native ISOs if it has two.</summary>
    private void ShowCameraModel()
    {
        bool connected = _cam.State == LinkState.Connected;
        Title = connected ? $"CamRemote — {CameraModels.Label(_settings.CameraModel)}" : "CamRemote";
        var model = connected ? CameraModels.Find(_settings.CameraModel) : null;
        BaseIsoButtons.Visibility = model is { HasBaseIso: true } ? Visibility.Visible : Visibility.Collapsed;
        if (model is not { HasBaseIso: true }) return;
        IsoLowButton.Content = $"Base ISO {model.LowIso}";
        IsoLowButton.Tag = model.LowIso.ToString();
        IsoHighButton.Content = $"Base ISO {model.HighIso}";
        IsoHighButton.Tag = model.HighIso.ToString();
    }

    private void OnLinkChanged()
    {
        if (_cam.State == LinkState.Connected) _wasConnected = true;
        KeepAwake();
        ShowCameraModel();
        var (color, text, button) = _cam.State switch
        {
            LinkState.Connected => ((Brush)FindResource("Ok"), "Camera connected", "Disconnect"),
            LinkState.Connecting => ((Brush)FindResource("Warn"), "Connecting…", "Cancel"),
            _ => (Brushes.Gray, "Camera not connected", "Connect camera…"),
        };
        LinkDot.Fill = color;
        LinkText.Text = text;
        ConnectButton.Content = button;
        RecButton.IsEnabled = _cam.State == LinkState.Connected;
        UpdateLiveView();
        if (_useWifi && _cam.State != LinkState.Connected)
        {
            Picture.Source = null;
            ClearAssist();
            NoSignal.Visibility = Visibility.Visible;
        }
        OnPropsChanged();
    }

    private void OnPropsChanged()
    {
        foreach (var row in _rows) row.Update(_cam.Get(row.Code));

        bool iso = _cam.Get(PropCode.IsoSensitivity) is { Writable: true };
        BaseIsoButtons.IsEnabled = iso;
        long isoNow = (_cam.Get(PropCode.IsoSensitivity)?.Value ?? 0) & 0xFFFFFF;
        IsoLowButton.FontWeight = isoNow == BaseIsoOf(IsoLowButton) ? FontWeights.Bold : FontWeights.Normal;
        IsoHighButton.FontWeight = isoNow == BaseIsoOf(IsoHighButton) ? FontWeights.Bold : FontWeights.Normal;

        // The focus ring only does anything in MF or DMF.
        long focusMode = _cam.Get(PropCode.FocusMode)?.Value ?? 0;
        bool manual = _cam.State == LinkState.Connected && focusMode is 1 or 6;
        ManualFocus.IsEnabled = manual;
        ManualFocus.Opacity = manual ? 1 : 0.45;
        ManualFocus.ToolTip = manual ? null : "Switch Focus mode to MF (or DMF) to use the focus ring.";

        bool recording = _cam.IsRecording;
        TrackRecordingTime(recording);
        RecBadge.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecFrame.BorderBrush = recording ? (Brush)FindResource("Rec") : Brushes.Transparent;
        RecLabel.Text = recording ? "Stop" : "Record";
        RecDot.Fill = recording ? Brushes.White : (Brush)FindResource("Rec");
        if (recording) RecButton.Background = (Brush)FindResource("Rec");
        else RecButton.ClearValue(BackgroundProperty);

        var battery = _cam.Get(PropCode.BatteryRemain);
        var level = _cam.Get(PropCode.BatteryLevel);
        BatteryText.Text = battery == null && level == null ? "" : "Battery " + Format.Battery(battery?.Value ?? -1, level?.Value ?? -1);
        BatteryText.Foreground = Format.BatteryLow(battery?.Value ?? -1, level?.Value ?? -1) ? (Brush)FindResource("Rec") : Foreground;
        var slot1 = _cam.Get(PropCode.MediaSLOT1_RemainingTime);
        var slot2 = _cam.Get(PropCode.MediaSLOT2_RemainingTime);
        var parts = new List<string>();
        var minutes = new List<long>();
        if (slot1 != null && _cam.Get(PropCode.MediaSLOT1_Status)?.Value == 0) { parts.Add("Card 1: " + Format.Duration(slot1.Value)); minutes.Add(slot1.Value / 60); }
        if (slot2 != null && _cam.Get(PropCode.MediaSLOT2_Status)?.Value == 0) { parts.Add("Card 2: " + Format.Duration(slot2.Value)); minutes.Add(slot2.Value / 60); }
        CardText.Text = _cam.State == LinkState.Connected && parts.Count == 0 && _cam.Props.Count > 0 ? "No card ready" : string.Join("    ", parts);
        bool cardLow = minutes.Count > 0 && minutes.Max() < 10;
        CardText.Foreground = cardLow || CardText.Text == "No card ready" ? (Brush)FindResource("Rec") : Foreground;

        // One banner over the picture for whatever needs attention most.
        string? alert = null;
        Brush alertColor = (Brush)FindResource("Rec");
        if (_cam.State == LinkState.Connected && _cam.Props.Count > 0)
        {
            long pct = battery?.Value ?? -1, lvl = level?.Value ?? -1;
            if (Format.BatteryCritical(pct, lvl)) alert = "⚠  BATTERY ALMOST EMPTY — swap it now";
            else if (Format.BatteryLow(pct, lvl)) alert = "⚠  Low battery";
            else if (CardText.Text == "No card ready") alert = "⚠  No memory card ready";
            else if (cardLow) alert = "⚠  Card almost full — under 10 minutes left";
        }
        else if (_wasConnected && !_cam.HoldReconnect)
        {
            alert = _auto.Status ?? "Camera connection lost — reconnecting…   (the camera keeps recording if it was)";
            alertColor = (Brush)FindResource("Warn");
        }
        AlertBanner.Visibility = alert == null ? Visibility.Collapsed : Visibility.Visible;
        AlertBanner.Background = alertColor;
        AlertText.Foreground = alertColor == FindResource("Warn") ? Brushes.Black : Brushes.White;
        AlertText.Text = alert ?? "";

        // The camera is the truth: a dial or Memory Recall can change everything at once, so say so when it does.
        long? mode = _cam.State == LinkState.Connected ? _cam.Get(PropCode.ExposureProgramMode)?.Value : null;
        if (mode != null && _lastMode != null && mode != _lastMode)
            ShowMessage($"The camera changed to {Format.Named("ExposureProgram")(mode.Value)}. Settings here now show what it reports.");
        if (mode != null) _lastMode = mode;

        CamSummary.Text = _cam.State != LinkState.Connected ? "" : string.Join("   ", new[]
        {
            Show(PropCode.ExposureProgramMode, Format.Named("ExposureProgram")),
            Show(PropCode.Movie_Recording_Setting, Format.Named("MovieRecordingSetting")),
            Show(PropCode.ShutterSpeed, Format.Shutter),
            Show(PropCode.FNumber, Format.FNumber),
            Show(PropCode.IsoSensitivity, Format.Iso),
        }.Where(s => s.Length > 0));

        ShowMeter();

        ShowFocusTools();
    }

    /// <summary>
    /// Keeps the take's start time in settings, so the timer carries on after a dropped link or a restart.
    /// The camera doesn't report how long it has been recording, so a take this app never saw begin
    /// counts from when it was found, shown with a "+".
    /// </summary>
    private void TrackRecordingTime(bool recording)
    {
        if (_cam.State != LinkState.Connected || _cam.Props.Count == 0) return;
        if (!recording)
        {
            // Just after connecting the camera can say "not recording" mid-take, so only believe it once it holds.
            _idleSince ??= DateTime.Now;
            if (DateTime.Now - _idleSince < TimeSpan.FromSeconds(5)) return;
            _recStart = null;
            _sawNotRecording = true;
            if (_settings.RecordingSince != null)
            {
                _settings.RecordingSince = null;
                _settings.Save();
            }
        }
        else
        {
            _idleSince = null;
            if (_recStart != null) return;
            if (_settings.RecordingSince == null || _sawNotRecording)
            {
                _settings.RecordingSince = DateTime.UtcNow;
                _settings.RecordingJoined = !_sawNotRecording;
                _settings.Save();
            }
            _recStart = _settings.RecordingSince.Value.ToLocalTime();
        }
    }

    private string Show(PropCode code, Func<long, string> fmt)
    {
        var p = _cam.Get(code);
        return p == null ? "" : fmt(p.Value);
    }

    private void Tick()
    {
        _ = TryReconnectAsync();
        bool watching = _phone?.Watching("sony") == true;
        if (watching != _phoneWatching)
        {
            _phoneWatching = watching;
            UpdateLiveView();
        }
        if (_recStart is DateTime start)
            RecTime.Text = "● REC " + (DateTime.Now - start).ToString(@"hh\:mm\:ss") + (_settings.RecordingJoined ? "+" : "");
        int frames = _useWifi ? _live.TakeFrameCount() : _video.TakeFrameCount();
        string format = _useWifi ? _live.Format : _video.Format;
        VideoInfo.Text = format.Length == 0 ? "" : $"{format}   {frames} fps shown";
    }

    private void ShowMessage(string msg) => MessageText.Text = $"{DateTime.Now:HH:mm:ss}  {msg}";

    // ---- Settings ----

    private void Add(Panel panel, string label, PropCode code, Func<long, string> fmt, PropControl.Kind kind,
        Func<long, double>? order = null)
    {
        var c = new PropControl(_cam, label, code, fmt, kind, order)
        {
            Section = panel == ExposurePanel ? "exposure" : panel == AudioPanel ? "audio" : panel == MorePanel ? "more" : "focus",
        };
        panel.Children.Add(c.Root);
        _rows.Add(c);
    }

    private static long BaseIsoOf(Button b) => long.TryParse(b.Tag as string, out long v) ? v : 0;

    /// <summary>Jumps to one of the connected camera's two native ISOs.</summary>
    private void OnBaseIsoClick(object sender, RoutedEventArgs e)
    {
        if (_cam.SetBaseIso(long.Parse((string)((Button)sender).Tag)) is { } problem) ShowMessage(problem);
    }

    private void ShowFocusTools()
    {
        bool connected = _cam.State == LinkState.Connected;
        var choices = connected ? _cam.MagnifyChoices() : [];
        bool inApp = choices.Count == 0;
        if (inApp) choices = [20, 40];
        long ratio = inApp ? _zoom.Ratio : _cam.MagnifyRatio;
        var wanted = new List<long> { 0 };
        wanted.AddRange(choices);
        if (!wanted.SequenceEqual(MagnifierButtons.Children.OfType<Button>().Select(b => (long)b.Tag)))
        {
            MagnifierButtons.Children.Clear();
            foreach (long r in wanted)
            {
                var b = new Button { Tag = r, Content = r == 0 ? "Off" : $"×{r / 10.0:0.#}", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 4, 0) };
                b.Click += (_, _) => OnMagnifyClick((long)b.Tag);
                MagnifierButtons.Children.Add(b);
            }
        }
        foreach (Button b in MagnifierButtons.Children)
        {
            b.IsEnabled = connected || inApp;
            bool on = (long)b.Tag == ratio && ratio > 0;
            b.FontWeight = (long)b.Tag == ratio ? FontWeights.Bold : FontWeights.Normal;
            if (on)
            {
                b.Background = ZoomBrush;
                b.Foreground = Brushes.Black;
            }
            else
            {
                b.ClearValue(BackgroundProperty);
                b.ClearValue(ForegroundProperty);
            }
        }
        ZoomBadge.Content = $"🔍 ×{ratio / 10.0:0.#}  ✕";
        ZoomBadge.Visibility = ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowFocusBar();
        bool tracking = connected && _cam.IsTracking;
        _showedTracking = tracking;
        StopTracking.Visibility = tracking ? Visibility.Visible : Visibility.Collapsed;
        TouchHint.Text = !connected ? ""
            : ratio > 0 ? "Zoomed in. Click the picture to move around. Off goes back to clicking to focus."
            : _cam.Get(PropCode.FunctionOfTouchOperation)?.Value == 1 ? "Tap does is Off, so clicks do nothing."
            : _cam.IsManualFocus ? "Click the picture to autofocus once. It stays in manual."
            : tracking ? "The camera is tracking. Right-click the picture to stop."
            : _cam.CanCancelTouch ? "Click to focus somewhere else. Right-click cancels."
            : _cam.TapTracks ? "Click a subject to ask the camera to track it."
            : "Click the picture to focus there.";
    }

    private void OnStopTrackingClick(object sender, RoutedEventArgs e)
    {
        if (_cam.CancelTouch() is { } problem) ShowMessage(problem);
    }

    // ---- Focus wheel ----

    private const double WheelTickGap = 14;
    private double _wheelLastX, _wheelTurned;

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

    private void TurnWheel(double pixels)
    {
        _wheelTurned += pixels;
        DrawWheel();
        _cam.TurnRing(pixels);
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
        _cam.FocusStep(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

    /// <summary>Draws the range from close up to infinity, with a marker where focus is now.</summary>
    private void ShowFocusBar()
    {
        double w = FocusBar.ActualWidth - 2, h = FocusBar.ActualHeight - 2;
        FocusBarTicks.Children.Clear();
        if (w <= 0) return;
        for (int i = 0; i <= 20; i++)
        {
            bool major = i % 5 == 0;
            var tick = new System.Windows.Shapes.Rectangle { Width = 1, Height = major ? 12 : 6, Fill = major ? Brushes.Gray : Brushes.DimGray };
            Canvas.SetLeft(tick, i * (w - 1) / 20);
            Canvas.SetTop(tick, h - tick.Height);
            FocusBarTicks.Children.Add(tick);
        }
        if (_cam.FocusPosition is not { } spot) return;
        var marker = new System.Windows.Shapes.Rectangle { Width = 3, Height = h, Fill = Brushes.Gold };
        Canvas.SetLeft(marker, spot * (w - 3));
        FocusBarTicks.Children.Add(marker);
    }

    private void OnFocusBarSized(object sender, SizeChangedEventArgs e) => ShowFocusBar();

    private void OnFocusBarClick(object sender, MouseButtonEventArgs e)
    {
        if (_cam.FocusTo(e.GetPosition(FocusBar).X / FocusBar.ActualWidth) is { } problem) ShowMessage(problem);
    }

    private void OnFocusStep(object sender, RoutedEventArgs e) =>
        _cam.FocusStep(int.Parse((string)((FrameworkElement)sender).Tag));

    // ---- Shooting guides ----

    private void SetUpAssist()
    {
        for (int stop = -3; stop <= 3; stop++)
        {
            double h = stop == 0 ? 24 : 10;
            var tick = new System.Windows.Shapes.Rectangle { Width = 1, Height = h, Fill = (Brush)FindResource("Dim") };
            Canvas.SetLeft(tick, 75 + stop * 24);
            Canvas.SetTop(tick, (24 - h) / 2);
            ExposureScale.Children.Insert(0, tick);
        }

        _loadingAssist = true;
        HistogramChip.IsChecked = _settings.ShowHistogram;
        ZebraChip.IsChecked = _settings.ShowZebras;
        PeakingChip.IsChecked = _settings.ShowPeaking;
        GridChip.IsChecked = _settings.ShowGrid;
        ZebraLevelBox.ItemsSource = new[] { 70, 80, 90, 95, 100 }.Select(v => new KeyValuePair<string, int>($"{v}%", v)).ToList();
        PeakSensitivityBox.ItemsSource = new[] { "Low", "Medium", "High" }.Select((n, i) => new KeyValuePair<string, int>(n, i)).ToList();
        PeakColorBox.ItemsSource = new[] { ("Red", 0xFF3030u), ("Yellow", 0xFFE030u), ("Cyan", 0x30E0FFu), ("White", 0xFFFFFFu) }
            .Select(c => new KeyValuePair<string, uint>(c.Item1, c.Item2)).ToList();
        foreach (var box in new[] { ZebraLevelBox, PeakSensitivityBox, PeakColorBox })
        {
            box.DisplayMemberPath = "Key";
            box.SelectedValuePath = "Value";
        }
        ZebraLevelBox.SelectedValue = _settings.ZebraLevel;
        PeakSensitivityBox.SelectedValue = _settings.PeakSensitivity;
        PeakColorBox.SelectedValue = _settings.PeakColor;
        _loadingAssist = false;
        ApplyAssist();
    }

    private void OnAssistToggle(object sender, RoutedEventArgs e) => ApplyAssist();

    private void OnAssistSettingChanged(object sender, SelectionChangedEventArgs e) => ApplyAssist();

    /// <summary>Saves the guide settings, and only reads the picture while something needs it.</summary>
    private void ApplyAssist()
    {
        if (_loadingAssist) return;
        _settings.ShowHistogram = HistogramChip.IsChecked == true;
        _settings.ShowZebras = ZebraChip.IsChecked == true;
        _settings.ShowPeaking = PeakingChip.IsChecked == true;
        _settings.ShowGrid = GridChip.IsChecked == true;
        if (ZebraLevelBox.SelectedValue is int level) _settings.ZebraLevel = level;
        if (PeakSensitivityBox.SelectedValue is int sensitivity) _settings.PeakSensitivity = sensitivity;
        if (PeakColorBox.SelectedValue is uint color) _settings.PeakColor = color;
        _settings.Save();

        bool layer = _settings.ShowZebras || _settings.ShowPeaking;
        _assist.Options = new AssistOptions(_settings.ShowHistogram || layer || AssistDetail.IsOpen, _settings.ShowZebras,
            _settings.ZebraLevel, _settings.ShowPeaking, _settings.PeakSensitivity, _settings.PeakColor);
        var show = _settings.ShowHistogram ? Visibility.Visible : Visibility.Collapsed;
        HistogramBox.Visibility = ClipBox.Visibility = show;
        AssistLayer.Visibility = layer ? Visibility.Visible : Visibility.Collapsed;
        GuideGrid.Visibility = _settings.ShowGrid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAssistDetailClick(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _assistDetailClosed).TotalMilliseconds < 250) return; // that click just closed it
        AssistDetail.PlacementTarget = HistogramBox.IsVisible ? HistogramBox : (UIElement)sender;
        AssistDetail.IsOpen = true;
        ApplyAssist();
    }

    private void OnAssistDetailClosed(object? sender, EventArgs e)
    {
        _assistDetailClosed = DateTime.Now;
        ApplyAssist();
    }

    private void ShowAssist(AssistResult r)
    {
        const double Clipping = 0.5; // percent of the picture worth flagging
        if (_settings.ShowHistogram)
        {
            HistogramSmall.Source = BitmapSource.Create(256, 40, 96, 96, PixelFormats.Pbgra32, null,
                PictureAssist.DrawHistogram(r, 40, false, _settings.ZebraLevel), 256 * 4);
            OverText.Text = $"Over {_settings.ZebraLevel}%: {r.OverPercent:0.0}%";
            OverText.Foreground = r.OverPercent >= Clipping ? (Brush)FindResource("Warn") : (Brush)FindResource("Dim");
            UnderText.Text = $"Under 3%: {r.UnderPercent:0.0}%";
        }
        if (AssistDetail.IsOpen)
        {
            HistogramLarge.Source = BitmapSource.Create(256, 130, 96, 96, PixelFormats.Pbgra32, null,
                PictureAssist.DrawHistogram(r, 130, true, _settings.ZebraLevel), 256 * 4);
            DetailText.Text = $"{r.OverPercent:0.0}% over {_settings.ZebraLevel}%     {r.UnderPercent:0.0}% under 3%";
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
        else if (_assistBitmap != null)
        {
            _assistBitmap = null;
            AssistLayer.Source = null;
        }
    }

    private void ClearAssist()
    {
        _assistBitmap = null;
        AssistLayer.Source = null;
        HistogramSmall.Source = HistogramLarge.Source = null;
        OverText.Text = UnderText.Text = DetailText.Text = "";
    }

    private void ShowMeter()
    {
        var stops = _cam.State == LinkState.Connected ? _cam.MeterStops : null;
        MeterBox.Visibility = stops == null ? Visibility.Collapsed : Visibility.Visible;
        if (stops is not double s) return;
        Canvas.SetLeft(ExposureNeedle, 74 + Math.Clamp(s, -3.1, 3.1) * 24);
        ExposureText.Text = Math.Abs(s) > 3 ? (s > 0 ? "+3 ▶" : "◀ -3") : $"{s:+0.0;-0.0;0.0}";
        ExposureNeedle.Fill = Math.Abs(s) <= 0.35 ? (Brush)FindResource("Ok") : Math.Abs(s) >= 2 ? (Brush)FindResource("Warn") : Brushes.White;
    }

    // ---- Keep the PC awake ----

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>
    /// Stops Windows sleeping or blanking the screen while the camera is connected. With phone control on,
    /// the PC also stays awake with the camera off, so a phone can still reach it.
    /// </summary>
    private void KeepAwake()
    {
        const uint Continuous = 0x80000000, System = 0x1, Display = 0x2;
        SetThreadExecutionState(_cam.State == LinkState.Connected ? Continuous | System | Display
            : _phone != null ? Continuous | System : Continuous);
    }

    // ---- Phone control ----

    private async void OnPhoneToggle(object sender, RoutedEventArgs e)
    {
        _settings.PhoneEnabled = PhoneEnabled.IsChecked == true;
        _settings.Save();
        await SetPhoneAsync(_settings.PhoneEnabled);
    }

    private async Task SetPhoneAsync(bool on)
    {
        int call = ++_phoneCall;
        PhoneEnabled.IsChecked = on;
        if (_phone != null)
        {
            await _phone.DisposeAsync();
            _phone = null;
        }
        if (on)
        {
            _settings.EnsurePhoneSecrets();
            _settings.Save();
            var sony = new SonyRemote(_ui, _cam, () => _live.Latest, _settings, _rows, () =>
                (BatteryText.Text, CardText.Text, AlertBanner.Visibility == Visibility.Visible ? AlertText.Text : ""));
            var server = new PhoneServer(_settings, [sony]);
            try
            {
                await server.StartAsync();
                if (call != _phoneCall)
                {
                    await server.DisposeAsync(); // switched off (or on again) while starting: this one isn't wanted
                    return;
                }
                _phone = server;
            }
            catch (Exception ex)
            {
                ShowMessage($"Phone control couldn't start: {ex.Message}");
            }
        }
        PhoneInfo.Visibility = _phone != null ? Visibility.Visible : Visibility.Collapsed;
        PhoneInfo.Text = _phone == null ? "" : string.Join("\n", PhoneServer.Addresses(_settings.PhonePort)) + $"\nAccess code: {_settings.PhoneCode}";
        KeepAwake();
    }

    // ---- Audio meters ----

    /// <summary>Meters the capture card's audio input when the picture comes over HDMI.</summary>
    private void UpdateMeter(VideoDevice? hdmi)
    {
        _meterTick.Stop();
        _meter.Stop();
        string hint = "Live meters need the camera's HDMI into a capture card: over Wi-Fi the camera sends no sound. Record level still works.";
        if (hdmi != null)
        {
            try
            {
                hint = _meter.Start(hdmi.Name)
                    ? "Meters show the camera's audio over HDMI (set the camera's HDMI audio output on)."
                    : $"Couldn't find an audio input for {hdmi.Name}.";
            }
            catch (Exception ex)
            {
                hint = "Couldn't open the capture card's audio: " + ex.Message;
            }
        }
        MeterHint.Text = hint;
        Meters.Opacity = _meter.Running ? 1 : 0.35;
        if (_meter.Running) _meterTick.Start();
        else DrawBar(MeterL, HoldL, MeterLText, MeterLTrack, -100, -100);
        if (!_meter.Running) DrawBar(MeterR, HoldR, MeterRText, MeterRTrack, -100, -100);
    }

    private void DrawMeters()
    {
        var (l, r) = _meter.TakePeaks();
        // Peak hold for 1.5 s so short bird calls register.
        if (DateTime.Now - _holdSince > TimeSpan.FromSeconds(1.5)) { _hold[0] = _hold[1] = -100; _holdSince = DateTime.Now; }
        _hold[0] = Math.Max(_hold[0], l);
        _hold[1] = Math.Max(_hold[1], r);
        DrawBar(MeterL, HoldL, MeterLText, MeterLTrack, l, _hold[0]);
        DrawBar(MeterR, HoldR, MeterRText, MeterRTrack, r, _hold[1]);
    }

    private const double MeterFloor = -60; // dBFS at the left end of the bar

    private void DrawBar(System.Windows.Shapes.Rectangle bar, System.Windows.Shapes.Rectangle hold, TextBlock text, FrameworkElement track,
        double db, double holdDb)
    {
        double w = track.ActualWidth;
        double Frac(double d) => Math.Clamp((d - MeterFloor) / -MeterFloor, 0, 1);
        bar.Width = w * Frac(db);
        bar.Fill = db > -6 ? (Brush)FindResource("Rec") : db > -18 ? (Brush)FindResource("Warn") : (Brush)FindResource("Ok");
        hold.Visibility = holdDb > MeterFloor ? Visibility.Visible : Visibility.Collapsed;
        hold.Margin = new Thickness(Math.Max(0, w * Frac(holdDb) - 2), 0, 0, 0);
        text.Text = holdDb > MeterFloor ? $"{holdDb:0} dB" : "";
        text.Foreground = holdDb > -1 ? (Brush)FindResource("Rec") : (Brush)FindResource("Dim");
    }
}
