using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace CamRemote;

public partial class MainWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(0xFF9A9A9A), Rec = new SolidColorBrush(0xFFE5322D),
        Ok = new SolidColorBrush(0xFF4CC26A), Warn = new SolidColorBrush(0xFFE8B339);

    private readonly CameraService _cam;
    private readonly VideoFeed _video;
    private readonly IUi _ui;
    private readonly LiveViewFeed<(byte[] Pixels, int Width, int Height)> _live;
    private readonly PictureAssist _assist;
    private WriteableBitmap? _pictureBitmap, _assistBitmap, _smallBitmap, _largeBitmap;
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
    private DateTime _linkSince = DateTime.Now; // when the link last changed state
    private bool _reconnecting;
    private bool _wasConnected; // connected at some point this run, so a drop is worth a banner
    private PhoneServer? _phone;
    private bool _phoneWatching; // a phone wants the live view, even if this window shows a capture card

    public MainWindow()
    {
        InitializeComponent();
        var ui = new AvaloniaUi();
        _ui = ui;
        _cam = new CameraService(ui);
        _video = new VideoFeed(ui);
        _video.Frame += (pixels, w, h) =>
        {
            if (_useWifi) return;
            ShowFrame(pixels, w, h);
        };
        _assist = new PictureAssist(ui);
        _assist.Done += ShowAssist;
        _live = new LiveViewFeed<(byte[], int, int)>(ui, DecodeJpeg);
        _live.Frame += frame =>
        {
            if (!_useWifi) return;
            ShowFrame(frame.Item1, frame.Item2, frame.Item3);
        };
        _cam.Message += ShowMessage;
        _cam.StateChanged += OnLinkChanged;
        _cam.PropsChanged += OnPropsChanged;

        var big = PropControl.Kind.Stepper;
        var pick = PropControl.Kind.Picker;
        Add(ExposurePanel, "ISO", PropCode.IsoSensitivity, Format.Iso, big, v => (v & 0xFFFFFF) == 0xFFFFFF ? -1 : v & 0xFFFFFF);
        Add(ExposurePanel, "Shutter", PropCode.ShutterSpeed, Format.Shutter, big, v => -Format.ShutterSeconds(v));
        Add(ExposurePanel, "Iris", PropCode.FNumber, Format.FNumber, big, v => v);
        Add(FocusPanel, "Focus mode", PropCode.FocusMode, Format.Named("FocusMode"), PropControl.Kind.Buttons);
        Add(FocusPanel, "Tap does", PropCode.FunctionOfTouchOperation, Format.Named("TouchFunction"), PropControl.Kind.Buttons);
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

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _meterTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTick.Tick += (_, _) => DrawMeters();
        UpdateMeter(null);
        _hideFocusMark = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hideFocusMark.Tick += (_, _) => { FocusMark.IsVisible = false; _hideFocusMark.Stop(); };

        Opened += OnLoaded;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
    }

    private static ((byte[], int, int), int, int) DecodeJpeg(byte[] buffer, int length)
    {
        var jpeg = buffer.AsSpan(0, length);
        var info = SKBitmap.DecodeBounds(jpeg);
        using var bmp = SKBitmap.Decode(jpeg, new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        return ((bmp.GetPixelSpan().ToArray(), info.Width, info.Height), info.Width, info.Height);
    }

    private void ShowFrame(byte[] pixels, int w, int h)
    {
        Blit(ref _pictureBitmap, Picture, pixels, w, h);
        NoSignal.IsVisible = false;
        _assist.Offer(w, h, target => pixels.AsSpan(0, target.Length).CopyTo(target));
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

    private async void OnLoaded(object? sender, EventArgs e)
    {
        if (_settings.AgreedVersion < Agreement.Version)
        {
            if (!await ConnectWindow.AskAsync(this, "Before you start", Agreement.Text, "I agree", "Quit"))
            {
                Close();
                return;
            }
            _settings.AgreedVersion = Agreement.Version;
            _settings.Save();
        }
        await LoadSourcesAsync();
        await SetPhoneAsync(_settings.PhoneEnabled);
        await TryReconnectAsync(first: true);
    }

    /// <summary>Lists Wi-Fi plus every capture card the Mac sees, and selects the saved one (Wi-Fi if it's gone).</summary>
    private Task LoadSourcesAsync()
    {
        try
        {
            var devices = VideoFeed.List();
            devices.Insert(0, WifiSource);
            VideoDevices.ItemsSource = devices;
            VideoDevices.SelectedItem = devices.FirstOrDefault(d => d.Id == _settings.VideoDeviceId) ?? WifiSource;
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't list video devices: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    private async void OnRefreshSourcesClick(object? sender, RoutedEventArgs e) => await LoadSourcesAsync();

    /// <summary>
    /// With auto-connect on, keeps trying the saved camera while it's off or out of range, and after a
    /// drop. Waits a little after a drop first, since the SDK tries to resume the session itself.
    /// </summary>
    private async Task TryReconnectAsync(bool first = false)
    {
        if (_settings.AgreedVersion < Agreement.Version || _reconnecting || _cam.HoldReconnect || !_settings.AutoConnect || _settings.CameraIp.Length == 0) return;
        var idle = DateTime.Now - _linkSince;
        bool due = _cam.State switch
        {
            LinkState.Disconnected => first || idle > TimeSpan.FromSeconds(15),
            LinkState.Connecting => idle > TimeSpan.FromSeconds(45), // stuck
            _ => false,
        };
        if (!due) return;
        _reconnecting = true;
        try
        {
            _linkSince = DateTime.Now;
            if (!await CameraConnect.SavedAsync(_cam, _settings, ShowMessage, AskFingerprint, quiet: !first))
                ShowMessage("Waiting for the camera (on, on Wi-Fi, Remote Shooting on). Trying again every 15 s…");
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private Task<bool> AskFingerprint() => ConnectWindow.AskAsync(this, "CamRemote", CameraConnect.FingerprintWarning, "Connect anyway", "Don't connect");

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _tick.Stop();
        _live.Stop();
        _meter.Dispose();
        _video.Stop();
        _cam.Dispose();
        if (OperatingSystem.IsMacOS()) MacNative.cb_awake(0);
        _ = _phone?.DisposeAsync();
    }

    // ---- Video ----

    private async void OnVideoDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (VideoDevices.SelectedItem is not VideoDevice dev) return;
        if (dev.Id == _activeSource) return; // the list was refreshed; keep the running picture
        _activeSource = dev.Id;
        _settings.VideoDeviceId = dev.Id;
        _settings.Save();
        ClearPicture();
        ClearAssist();
        _useWifi = dev == WifiSource;
        UpdateLiveView();
        UpdateMeter(_useWifi ? null : dev);
        NoSignal.IsVisible = true;
        _video.Stop();
        if (_useWifi) return;
        if (await _video.StartAsync(dev.Id) is { } problem) ShowMessage($"Couldn't open {dev.Name}: {problem}");
    }

    private void ClearPicture()
    {
        _pictureBitmap = null;
        Picture.Source = null;
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

    private void OnPicturePressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Picture);
        bool cancel = point.Properties.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Control); // Control-click is a Mac's right-click
        if (cancel)
        {
            if (_cam.State != LinkState.Connected) return;
            _cam.CancelTouch();
            FocusMark.IsVisible = false;
            return;
        }
        if (_cam.State != LinkState.Connected || Picture.Bounds.Width <= 0) return;
        var p = point.Position;
        double fx = p.X / Picture.Bounds.Width, fy = p.Y / Picture.Bounds.Height;
        var problem = _cam.TouchFocus(fx, fy);
        if (problem != null)
        {
            ShowMessage(problem);
            return;
        }
        Canvas.SetLeft(FocusMark, p.X - FocusMark.Width / 2);
        Canvas.SetTop(FocusMark, p.Y - FocusMark.Height / 2);
        FocusMark.IsVisible = true;
        _hideFocusMark.Stop();
        _hideFocusMark.Start();
    }

    private void OnRotateClick(object? sender, RoutedEventArgs e)
    {
        _settings.Rotation = ((_settings.Rotation + int.Parse((string)((Button)sender!).Tag!)) % 360 + 360) % 360;
        _settings.Save();
        ApplyRotation();
    }

    /// <summary>
    /// Turns the picture for a camera on its side. Click-to-focus still lands in the right spot,
    /// because clicks are read in the picture's own (unrotated) coordinates.
    /// </summary>
    private void ApplyRotation()
    {
        PictureTurn.LayoutTransform = new RotateTransform(_settings.Rotation);
        RotationText.Text = $"Rotation {_settings.Rotation}°";
    }

    // ---- Camera ----

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_cam.State != LinkState.Disconnected)
        {
            _cam.Disconnect();
            return;
        }
        var dlg = new ConnectWindow(_cam, _settings);
        if (!await dlg.ShowDialog<bool>(this)) return;
        _cam.HoldReconnect = false;
        _linkSince = DateTime.Now;
        await dlg.ConnectAsync(ShowMessage, AskFingerprint);
    }

    private void OnRecClick(object? sender, RoutedEventArgs e) => _cam.ToggleRecord();

    private void OnKeyDown(object? sender, KeyEventArgs e)
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
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.H or Key.Z or Key.P or Key.G)
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
        BaseIsoButtons.IsVisible = model is { HasBaseIso: true };
        if (model is not { HasBaseIso: true }) return;
        IsoLowButton.Content = $"Base ISO {model.LowIso}";
        IsoLowButton.Tag = model.LowIso.ToString();
        IsoHighButton.Content = $"Base ISO {model.HighIso}";
        IsoHighButton.Tag = model.HighIso.ToString();
    }

    private void OnLinkChanged()
    {
        _linkSince = DateTime.Now;
        if (_cam.State == LinkState.Connected) _wasConnected = true;
        KeepAwake();
        ShowCameraModel();
        var (color, text, button) = _cam.State switch
        {
            LinkState.Connected => (Ok, "Camera connected", "Disconnect"),
            LinkState.Connecting => (Warn, "Connecting…", "Cancel"),
            _ => ((IBrush)Brushes.Gray, "Camera not connected", "Connect camera…"),
        };
        LinkDot.Fill = color;
        LinkText.Text = text;
        ConnectButton.Content = button;
        RecButton.IsEnabled = _cam.State == LinkState.Connected;
        UpdateLiveView();
        if (_useWifi && _cam.State != LinkState.Connected)
        {
            ClearPicture();
            ClearAssist();
            NoSignal.IsVisible = true;
        }
        OnPropsChanged();
    }

    private void OnPropsChanged()
    {
        foreach (var row in _rows) row.Update(_cam.Get(row.Code));

        bool iso = _cam.Get(PropCode.IsoSensitivity) is { Writable: true };
        BaseIsoButtons.IsEnabled = iso;
        long isoNow = (_cam.Get(PropCode.IsoSensitivity)?.Value ?? 0) & 0xFFFFFF;
        IsoLowButton.FontWeight = isoNow == BaseIsoOf(IsoLowButton) ? FontWeight.Bold : FontWeight.Normal;
        IsoHighButton.FontWeight = isoNow == BaseIsoOf(IsoHighButton) ? FontWeight.Bold : FontWeight.Normal;

        // The focus ring only does anything in MF or DMF.
        long focusMode = _cam.Get(PropCode.FocusMode)?.Value ?? 0;
        bool manual = _cam.State == LinkState.Connected && focusMode is 1 or 6;
        ManualFocus.IsEnabled = manual;
        ManualFocus.Opacity = manual ? 1 : 0.45;
        ToolTip.SetTip(ManualFocus, manual ? null : "Switch Focus mode to MF (or DMF) to use the focus ring.");

        bool recording = _cam.IsRecording;
        TrackRecordingTime(recording);
        RecBadge.IsVisible = recording;
        RecFrame.BorderBrush = recording ? Rec : Brushes.Transparent;
        RecLabel.Text = recording ? "Stop" : "Record";
        RecDot.Fill = recording ? Brushes.White : Rec;
        if (recording) RecButton.Background = Rec;
        else RecButton.ClearValue(BackgroundProperty);

        var battery = _cam.Get(PropCode.BatteryRemain);
        var level = _cam.Get(PropCode.BatteryLevel);
        BatteryText.Text = battery == null && level == null ? "" : "Battery " + Format.Battery(battery?.Value ?? -1, level?.Value ?? -1);
        BatteryText.Foreground = Format.BatteryLow(battery?.Value ?? -1, level?.Value ?? -1) ? Rec : Brushes.White;
        var slot1 = _cam.Get(PropCode.MediaSLOT1_RemainingTime);
        var slot2 = _cam.Get(PropCode.MediaSLOT2_RemainingTime);
        var parts = new List<string>();
        var minutes = new List<long>();
        if (slot1 != null && _cam.Get(PropCode.MediaSLOT1_Status)?.Value == 0) { parts.Add("Card 1: " + Format.Duration(slot1.Value)); minutes.Add(slot1.Value / 60); }
        if (slot2 != null && _cam.Get(PropCode.MediaSLOT2_Status)?.Value == 0) { parts.Add("Card 2: " + Format.Duration(slot2.Value)); minutes.Add(slot2.Value / 60); }
        CardText.Text = _cam.State == LinkState.Connected && parts.Count == 0 && _cam.Props.Count > 0 ? "No card ready" : string.Join("    ", parts);
        bool cardLow = minutes.Count > 0 && minutes.Max() < 10;
        CardText.Foreground = cardLow || CardText.Text == "No card ready" ? Rec : Brushes.White;

        // One banner over the picture for whatever needs attention most.
        string? alert = null;
        IBrush alertColor = Rec;
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
            alert = "Camera connection lost — reconnecting…   (the camera keeps recording if it was)";
            alertColor = Warn;
        }
        AlertBanner.IsVisible = alert != null;
        AlertBanner.Background = alertColor;
        AlertText.Foreground = alertColor == Warn ? Brushes.Black : Brushes.White;
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
    private void OnBaseIsoClick(object? sender, RoutedEventArgs e)
    {
        if (_cam.SetBaseIso(long.Parse((string)((Button)sender!).Tag!)) is { } problem) ShowMessage(problem);
    }

    private void ShowFocusTools()
    {
        bool connected = _cam.State == LinkState.Connected;
        long ratio = connected ? _cam.MagnifyRatio : 0;
        var choices = connected ? _cam.MagnifyChoices() : [];
        var wanted = new List<long> { 0 };
        wanted.AddRange(choices);
        if (!wanted.SequenceEqual(MagnifierButtons.Children.OfType<Button>().Select(b => (long)b.Tag!)))
        {
            MagnifierButtons.Children.Clear();
            foreach (long r in wanted)
            {
                var b = new Button { Tag = r, Content = r == 0 ? "Off" : $"×{r / 10.0:0.#}", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 4, 0) };
                b.Click += (_, _) => _cam.Magnify((long)b.Tag!);
                MagnifierButtons.Children.Add(b);
            }
        }
        foreach (var b in MagnifierButtons.Children.OfType<Button>())
        {
            b.IsEnabled = connected;
            b.FontWeight = (long)b.Tag! == ratio ? FontWeight.Bold : FontWeight.Normal;
        }
        bool tracking = connected && _cam.IsTracking;
        StopTracking.IsVisible = tracking;
        TouchHint.Text = !connected ? ""
            : ratio > 0 ? "Magnified: click the picture to move the magnified area."
            : _cam.IsManualFocus ? "Click the picture to autofocus once. It stays in manual."
            : tracking ? "Tracking. Right-click or Control-click the picture to stop."
            : _cam.TapTracks ? "Click a subject to track it. Right-click or Control-click cancels."
            : "Click the picture to focus there. Right-click or Control-click cancels.";
    }

    private void OnStopTrackingClick(object? sender, RoutedEventArgs e) => _cam.CancelTouch();

    // ---- Focus wheel ----

    private const double WheelPixelsPerStep = 5, WheelTickGap = 14;
    private double _wheelLastX, _wheelOwed, _wheelTurned;

    private void DrawWheel()
    {
        WheelTicks.Children.Clear();
        for (int i = -1; i < 40; i++)
        {
            bool major = i % 5 == 0;
            var tick = new Rectangle { Width = 1, Height = major ? 24 : 12, Fill = major ? Brushes.Gray : Brushes.DimGray };
            Canvas.SetLeft(tick, i * WheelTickGap + _wheelTurned % (WheelTickGap * 5));
            Canvas.SetTop(tick, major ? 10 : 16);
            WheelTicks.Children.Add(tick);
        }
    }

    private void TurnWheel(double pixels)
    {
        _wheelTurned += pixels;
        _wheelOwed = Math.Clamp(_wheelOwed + pixels, -7 * WheelPixelsPerStep, 7 * WheelPixelsPerStep);
        DrawWheel();
        int step = (int)Math.Clamp(_wheelOwed / WheelPixelsPerStep, -7, 7);
        if (step != 0 && _cam.FocusStep(step)) _wheelOwed -= step * WheelPixelsPerStep;
    }

    private bool _wheelHeld;

    private void OnWheelDown(object? sender, PointerPressedEventArgs e)
    {
        _wheelLastX = e.GetPosition(FocusWheel).X;
        _wheelOwed = 0;
        _wheelHeld = true;
        e.Pointer.Capture(FocusWheel);
    }

    private void OnWheelMove(object? sender, PointerEventArgs e)
    {
        if (!_wheelHeld) return;
        double x = e.GetPosition(FocusWheel).X;
        TurnWheel(x - _wheelLastX);
        _wheelLastX = x;
    }

    private void OnWheelUp(object? sender, PointerReleasedEventArgs e)
    {
        _wheelHeld = false;
        e.Pointer.Capture(null);
    }

    private void OnWheelScroll(object? sender, PointerWheelEventArgs e)
    {
        TurnWheel(e.Delta.Y * WheelPixelsPerStep); // a trackpad sends fractions, which build up into steps
        e.Handled = true;
    }

    private void OnFocusStep(object? sender, RoutedEventArgs e) =>
        _cam.FocusStep(int.Parse((string)((Control)sender!).Tag!));

    // ---- Shooting guides ----

    private static readonly int[] ZebraLevels = [70, 80, 90, 95, 100];
    private static readonly (string Name, uint Color)[] PeakColors =
        [("Red", 0xFF3030u), ("Yellow", 0xFFE030u), ("Cyan", 0x30E0FFu), ("White", 0xFFFFFFu)];

    private void SetUpAssist()
    {
        for (int stop = -3; stop <= 3; stop++)
        {
            double h = stop == 0 ? 24 : 10;
            var tick = new Rectangle { Width = 1, Height = h, Fill = Dim };
            Canvas.SetLeft(tick, 75 + stop * 24);
            Canvas.SetTop(tick, (24 - h) / 2);
            ExposureScale.Children.Insert(0, tick);
        }

        _loadingAssist = true;
        HistogramChip.IsChecked = _settings.ShowHistogram;
        ZebraChip.IsChecked = _settings.ShowZebras;
        PeakingChip.IsChecked = _settings.ShowPeaking;
        GridChip.IsChecked = _settings.ShowGrid;
        ZebraLevelBox.ItemsSource = ZebraLevels.Select(v => $"{v}%").ToList();
        PeakSensitivityBox.ItemsSource = new[] { "Low", "Medium", "High" };
        PeakColorBox.ItemsSource = PeakColors.Select(c => c.Name).ToList();
        ZebraLevelBox.SelectedIndex = Array.IndexOf(ZebraLevels, _settings.ZebraLevel);
        PeakSensitivityBox.SelectedIndex = _settings.PeakSensitivity;
        PeakColorBox.SelectedIndex = Array.FindIndex(PeakColors, c => c.Color == _settings.PeakColor);
        _loadingAssist = false;
        ApplyAssist();
    }

    private void OnAssistToggle(object? sender, RoutedEventArgs e) => ApplyAssist();

    private void OnAssistSettingChanged(object? sender, SelectionChangedEventArgs e) => ApplyAssist();

    /// <summary>Saves the guide settings, and only reads the picture while something needs it.</summary>
    private void ApplyAssist()
    {
        if (_loadingAssist) return;
        _settings.ShowHistogram = HistogramChip.IsChecked == true;
        _settings.ShowZebras = ZebraChip.IsChecked == true;
        _settings.ShowPeaking = PeakingChip.IsChecked == true;
        _settings.ShowGrid = GridChip.IsChecked == true;
        if (ZebraLevelBox.SelectedIndex >= 0) _settings.ZebraLevel = ZebraLevels[ZebraLevelBox.SelectedIndex];
        if (PeakSensitivityBox.SelectedIndex >= 0) _settings.PeakSensitivity = PeakSensitivityBox.SelectedIndex;
        if (PeakColorBox.SelectedIndex >= 0) _settings.PeakColor = PeakColors[PeakColorBox.SelectedIndex].Color;
        _settings.Save();

        bool layer = _settings.ShowZebras || _settings.ShowPeaking;
        _assist.Options = new AssistOptions(_settings.ShowHistogram || layer || AssistDetail.IsOpen, _settings.ShowZebras,
            _settings.ZebraLevel, _settings.ShowPeaking, _settings.PeakSensitivity, _settings.PeakColor);
        HistogramBox.IsVisible = ClipBox.IsVisible = _settings.ShowHistogram;
        AssistLayer.IsVisible = layer;
        GuideGrid.IsVisible = _settings.ShowGrid;
    }

    private void OnHistogramPressed(object? sender, PointerPressedEventArgs e) => OpenAssistDetail(HistogramBox);

    private void OnAssistDetailClick(object? sender, RoutedEventArgs e) =>
        OpenAssistDetail(HistogramBox.IsVisible ? HistogramBox : DetailButton);

    private void OpenAssistDetail(Control target)
    {
        if ((DateTime.Now - _assistDetailClosed).TotalMilliseconds < 250) return; // that click just closed it
        AssistDetail.PlacementTarget = target;
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
            Blit(ref _smallBitmap, HistogramSmall, PictureAssist.DrawHistogram(r, 40, false, _settings.ZebraLevel), 256, 40);
            OverText.Text = $"Over {_settings.ZebraLevel}%: {r.OverPercent:0.0}%";
            OverText.Foreground = r.OverPercent >= Clipping ? Warn : Dim;
            UnderText.Text = $"Under 3%: {r.UnderPercent:0.0}%";
        }
        if (AssistDetail.IsOpen)
        {
            Blit(ref _largeBitmap, HistogramLarge, PictureAssist.DrawHistogram(r, 130, true, _settings.ZebraLevel), 256, 130);
            DetailText.Text = $"{r.OverPercent:0.0}% over {_settings.ZebraLevel}%     {r.UnderPercent:0.0}% under 3%";
        }
        if (r.Overlay.Length > 0)
        {
            Blit(ref _assistBitmap, AssistLayer, r.Overlay, r.Width, r.Height);
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
        _smallBitmap = _largeBitmap = null;
        HistogramSmall.Source = HistogramLarge.Source = null;
        OverText.Text = UnderText.Text = DetailText.Text = "";
    }

    private void ShowMeter()
    {
        var stops = _cam.State == LinkState.Connected ? _cam.MeterStops : null;
        MeterBox.IsVisible = stops != null;
        if (stops is not double s) return;
        Canvas.SetLeft(ExposureNeedle, 74 + Math.Clamp(s, -3.1, 3.1) * 24);
        ExposureText.Text = Math.Abs(s) > 3 ? (s > 0 ? "+3 ▶" : "◀ -3") : $"{s:+0.0;-0.0;0.0}";
        ExposureNeedle.Fill = Math.Abs(s) <= 0.35 ? Ok : Math.Abs(s) >= 2 ? Warn : Brushes.White;
    }

    // ---- Keep the Mac awake ----

    /// <summary>
    /// Stops the Mac sleeping or blanking the screen while the camera is connected. With phone control on,
    /// it also stays awake with the camera off, so a phone can still reach it.
    /// </summary>
    private void KeepAwake()
    {
        if (OperatingSystem.IsMacOS()) MacNative.cb_awake(_cam.State == LinkState.Connected ? 2 : _phone != null ? 1 : 0);
    }

    // ---- Phone control ----

    private async void OnPhoneToggle(object? sender, RoutedEventArgs e)
    {
        _settings.PhoneEnabled = PhoneEnabled.IsChecked == true;
        _settings.Save();
        await SetPhoneAsync(_settings.PhoneEnabled);
    }

    private async Task SetPhoneAsync(bool on)
    {
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
                (BatteryText.Text ?? "", CardText.Text ?? "", AlertBanner.IsVisible ? AlertText.Text ?? "" : ""));
            var server = new PhoneServer(_settings, [sony]);
            try
            {
                await server.StartAsync();
                _phone = server;
            }
            catch (Exception ex)
            {
                ShowMessage($"Phone control couldn't start: {ex.Message}");
            }
        }
        PhoneInfo.IsVisible = _phone != null;
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
            hint = _meter.Start(hdmi.Name) ?? "Meters show the camera's audio over HDMI (set the camera's HDMI audio output on).";
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

    private void DrawBar(Rectangle bar, Rectangle hold, TextBlock text, Control track,
        double db, double holdDb)
    {
        double w = track.Bounds.Width;
        double Frac(double d) => Math.Clamp((d - MeterFloor) / -MeterFloor, 0, 1);
        bar.Width = w * Frac(db);
        bar.Fill = db > -6 ? Rec : db > -18 ? Warn : Ok;
        hold.IsVisible = holdDb > MeterFloor;
        hold.Margin = new Thickness(Math.Max(0, w * Frac(holdDb) - 2), 0, 0, 0);
        text.Text = holdDb > MeterFloor ? $"{holdDb:0} dB" : "";
        text.Foreground = holdDb > -1 ? Rec : Dim;
    }
}
