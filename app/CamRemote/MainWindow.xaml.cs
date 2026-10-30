using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CamRemote;

public partial class MainWindow : Window
{
    private readonly CameraService _cam;
    private readonly VideoFeed _video;
    private readonly LiveViewFeed _live;
    private readonly AudioMeter _meter = new();
    private readonly DispatcherTimer _meterTick;
    private readonly double[] _hold = [-100, -100];
    private DateTime _holdSince = DateTime.Now;
    private bool _useWifi; // picture from the camera's live view instead of the capture card
    private readonly Settings _settings = Settings.Load();
    private readonly List<PropControl> _rows = [];
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _hideFocusMark;
    private DateTime? _recStart;
    private DateTime _linkSince = DateTime.Now; // when the link last changed state
    private bool _reconnecting;
    private bool _wasConnected; // connected at some point this run, so a drop is worth a banner

    public MainWindow()
    {
        InitializeComponent();
        _cam = new CameraService(Dispatcher);
        _video = new VideoFeed(Dispatcher);
        _video.BitmapChanged += () =>
        {
            if (_useWifi) return;
            Picture.Source = _video.Bitmap;
            NoSignal.Visibility = Visibility.Collapsed;
        };
        _live = new LiveViewFeed(Dispatcher);
        _live.Frame += bmp =>
        {
            if (!_useWifi) return;
            Picture.Source = bmp;
            NoSignal.Visibility = Visibility.Collapsed;
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
        Add(MagnifierPanel, "Focus magnifier", PropCode.Focus_Magnifier_Setting, Format.Magnifier, pick);
        Add(AudioPanel, "Record level", PropCode.AudioInputMasterLevel, v => v.ToString(), big, v => v);
        Add(AudioPanel, "Audio recording", PropCode.AudioRecording, Format.Named("AudioRecording"), PropControl.Kind.Buttons);
        Add(AudioPanel, "Wind noise reduction", PropCode.WindNoiseReduct, Format.Named("WindNoise"), PropControl.Kind.Buttons);
        Add(MorePanel, "Exposure mode", PropCode.ExposureProgramMode, Format.Named("ExposureProgram"), pick);
        Add(MorePanel, "White balance", PropCode.WhiteBalance, Format.Named("WhiteBalance"), pick);
        Add(MorePanel, "Color temp", PropCode.Colortemp, Format.ColorTemp, pick, v => v);
        Add(MorePanel, "Focus area", PropCode.FocusArea, Format.Named("FocusArea"), pick);
        Add(MorePanel, "Touch operation", PropCode.TouchOperation, Format.Named("TouchOperation"), PropControl.Kind.Buttons);
        Add(MorePanel, "Touch does", PropCode.FunctionOfTouchOperation, Format.Named("TouchFunction"), pick);
        Add(MorePanel, "Shooting mode", PropCode.MovieShootingMode, Format.Named("MovieShootingMode"), pick);
        Add(MorePanel, "Monitor LUT", PropCode.MonitorLUTSetting, Format.Named("MonitorLut"), PropControl.Kind.Buttons);
        Add(MorePanel, "File format", PropCode.Movie_File_Format, Format.Named("MovieFileFormat"), pick);
        Add(MorePanel, "Rec setting", PropCode.Movie_Recording_Setting, Format.Named("MovieRecordingSetting"), pick);
        Add(MorePanel, "Frame rate", PropCode.Movie_Recording_FrameRateSetting, Format.Named("MovieFrameRate"), pick);
        Add(MorePanel, "HDMI out", PropCode.Movie_HDMIOutputResolution, Format.Named("HdmiResolution"), pick);
        OnPropsChanged();
        ApplyRotation();

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        _meterTick = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => DrawMeters(), Dispatcher);
        UpdateMeter(null);
        _hideFocusMark = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hideFocusMark.Tick += (_, _) => { FocusMark.Visibility = Visibility.Collapsed; _hideFocusMark.Stop(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
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

        await TryReconnectAsync(first: true);
    }

    /// <summary>
    /// With auto-connect on, keeps trying the saved camera while it's off or out of range, and after a
    /// drop. Waits a little after a drop first, since the SDK tries to resume the session itself.
    /// </summary>
    private async Task TryReconnectAsync(bool first = false)
    {
        if (_reconnecting || _cam.HoldReconnect || !_settings.AutoConnect || _settings.CameraIp.Length == 0) return;
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
            if (!await ConnectWindow.ConnectSavedAsync(_cam, _settings, ShowMessage, quiet: !first))
                ShowMessage("Waiting for the camera (on, on Wi-Fi, Remote Shooting on). Trying again every 15 s…");
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _tick.Stop();
        _live.Stop();
        _meter.Dispose();
        await _video.DisposeAsync();
        _cam.Dispose();
    }

    // ---- Video ----

    private async void OnVideoDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoDevices.SelectedItem is not VideoDevice dev) return;
        _settings.VideoDeviceId = dev.Id;
        _settings.Save();
        Picture.Source = null;
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
            ShowMessage($"Couldn't open {dev.Name}: {ex.Message} (is another app using it, like OBS or Elgato Studio?)");
        }
    }

    private static readonly VideoDevice WifiSource = new("wifi", "Camera over Wi-Fi (live view)");

    /// <summary>Runs the live-view reader only while it's the chosen picture and the camera is connected.</summary>
    private void UpdateLiveView()
    {
        if (_useWifi && _cam.State == LinkState.Connected) _live.Start();
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
        _cam.CancelTouch();
        FocusMark.Visibility = Visibility.Collapsed;
    }

    // ---- Camera ----

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_cam.State != LinkState.Disconnected)
        {
            _cam.Disconnect();
            return;
        }
        var dlg = new ConnectWindow(_cam, _settings) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _cam.HoldReconnect = false;
        _linkSince = DateTime.Now;
        await dlg.ConnectAsync(ShowMessage);
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
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamRemote", "props.txt");
            var lines = _cam.Props.Values.OrderBy(p => p.Code).Select(p =>
                $"{(Enum.IsDefined(typeof(PropCode), p.Code) ? ((PropCode)p.Code).ToString() : $"0x{p.Code:X}"),-45} " +
                $"value=0x{p.Value:X} type=0x{p.Type:X} writable={p.Writable} enable={p.Enable} " +
                $"{(p.Str != null ? $"str=\"{p.Str}\" " : "")}choices=[{string.Join(",", p.Candidates.Select(c => $"0x{c:X}"))}]");
            System.IO.File.WriteAllLines(path, lines);
            ShowMessage("Saved camera properties to " + path);
            e.Handled = true;
        }
    }

    private void OnLinkChanged()
    {
        _linkSince = DateTime.Now;
        if (_cam.State == LinkState.Connected) _wasConnected = true;
        KeepAwake(_cam.State == LinkState.Connected);
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
        Iso640Button.FontWeight = isoNow == 640 ? FontWeights.Bold : FontWeights.Normal;
        Iso12800Button.FontWeight = isoNow == 12800 ? FontWeights.Bold : FontWeights.Normal;

        // The focus ring only does anything in MF or DMF.
        long focusMode = _cam.Get(PropCode.FocusMode)?.Value ?? 0;
        bool manual = _cam.State == LinkState.Connected && focusMode is 1 or 6;
        ManualFocus.IsEnabled = manual;
        ManualFocus.Opacity = manual ? 1 : 0.45;
        ManualFocus.ToolTip = manual ? null : "Switch Focus mode to MF (or DMF) to use the focus ring.";

        bool recording = _cam.IsRecording;
        if (recording && _recStart == null) _recStart = DateTime.Now;
        if (!recording) _recStart = null;
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
            alert = "Camera connection lost — reconnecting…   (the camera keeps recording if it was)";
            alertColor = (Brush)FindResource("Warn");
        }
        AlertBanner.Visibility = alert == null ? Visibility.Collapsed : Visibility.Visible;
        AlertBanner.Background = alertColor;
        AlertText.Foreground = alertColor == FindResource("Warn") ? Brushes.Black : Brushes.White;
        AlertText.Text = alert ?? "";

        CamSummary.Text = _cam.State != LinkState.Connected ? "" : string.Join("   ", new[]
        {
            Show(PropCode.Movie_Recording_Setting, Format.Named("MovieRecordingSetting")),
            Show(PropCode.ShutterSpeed, Format.Shutter),
            Show(PropCode.FNumber, Format.FNumber),
            Show(PropCode.IsoSensitivity, Format.Iso),
        }.Where(s => s.Length > 0));

        bool touchReady = _cam.Get(PropCode.RemoteTouchOperationEnableStatus)?.Value == 1
                          || _cam.Get(PropCode.AF_Area_Position) is { Writable: true };
        TouchHint.Text = _cam.State != LinkState.Connected || touchReady
            ? "Click the picture to focus there. Right-click cancels."
            : "Touch focus is off on the camera right now (needs movie mode and Touch Operation on, or a Spot focus area).";
    }

    private string Show(PropCode code, Func<long, string> fmt)
    {
        var p = _cam.Get(code);
        return p == null ? "" : fmt(p.Value);
    }

    private void Tick()
    {
        _ = TryReconnectAsync();
        if (_recStart is DateTime start)
            RecTime.Text = "● REC " + (DateTime.Now - start).ToString(@"hh\:mm\:ss");
        int frames = _useWifi ? _live.TakeFrameCount() : _video.TakeFrameCount();
        string format = _useWifi ? _live.Format : _video.Format;
        VideoInfo.Text = format.Length == 0 ? "" : $"{format}   {frames} fps shown";
    }

    private void ShowMessage(string msg) => MessageText.Text = $"{DateTime.Now:HH:mm:ss}  {msg}";

    // ---- Settings ----

    private void Add(Panel panel, string label, PropCode code, Func<long, string> fmt, PropControl.Kind kind,
        Func<long, double>? order = null)
    {
        var c = new PropControl(_cam, label, code, fmt, kind, order);
        panel.Children.Add(c.Root);
        _rows.Add(c);
    }

    /// <summary>Jumps to one of the A7S III's two native ISOs (640 / 12800 in S-Log3).</summary>
    private void OnBaseIsoClick(object sender, RoutedEventArgs e)
    {
        long want = long.Parse((string)((Button)sender).Tag);
        var p = _cam.Get(PropCode.IsoSensitivity);
        if (p == null) return;
        var choices = p.Choices().Where(v => (v & 0xFFFFFF) == want).ToList();
        if (choices.Count == 0)
        {
            ShowMessage($"The camera isn't offering ISO {want} right now (check the shooting mode).");
            return;
        }
        // Prefer the plain value over an "expanded" variant with flag bits set.
        _cam.Set(PropCode.IsoSensitivity, choices.OrderBy(v => v >> 24).First());
    }

    private void OnFocusStep(object sender, RoutedEventArgs e) =>
        _cam.FocusStep(int.Parse((string)((FrameworkElement)sender).Tag));

    // ---- Keep the PC awake ----

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Stops Windows sleeping or blanking the screen while the camera is connected.</summary>
    private static void KeepAwake(bool on)
    {
        const uint Continuous = 0x80000000, System = 0x1, Display = 0x2;
        SetThreadExecutionState(on ? Continuous | System | Display : Continuous);
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
