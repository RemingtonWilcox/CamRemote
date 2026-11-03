using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
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
    private readonly PropControl _recordLevel; // beside the meters with a capture card, in Setup otherwise
    private readonly PropControl _frameRate, _recSetting; // on the Video page, swapped for the S&Q ones in S&Q
    private readonly PropControl[] _slowQuickRows;
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

    // Sony gives these as ranges thousands of steps long, so offer the sensible stops.
    private static readonly long[] IntervalChoices = [5, 10, 20, 30, 50, 100, 150, 300, 600, 1200, 3000, 6000]; // tenths of a second
    private static readonly long[] ShotChoices = [30, 60, 120, 240, 300, 600, 1200, 2400, 4800, 9999];
    private static readonly long[] StartChoices = [1, 5, 10, 30, 60, 300]; // seconds
    private DateTime _linkSince = DateTime.Now; // when the link last changed state
    private bool _reconnecting;
    private bool _wasConnected; // connected at some point this run, so a drop is worth a banner
    private PhoneServer? _phone;
    private readonly BirdTrigger _bird;
    private bool _setup; // the Setup tab is showing
    private bool _phoneWatching; // a phone wants the live view, even if this window shows a capture card

    public MainWindow()
    {
        InitializeComponent();
        var ui = new AvaloniaUi();
        _ui = ui;
        _cam = new CameraService(ui);
        _cam.UsePhotoFolder(_settings.PhotoFolder);
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
        _bird = new BirdTrigger(ui, _cam, () => _live.Latest, ShrinkForBird, FocusOnAnimal, () => _settings.Rotation);
        _bird.Status += text => BirdStatus.Text = text;
        _bird.Spotted += ShowAnimal;
        BirdWatch.IsVisible = BirdTrigger.Available;
        _cam.Message += ShowMessage;
        _cam.StateChanged += OnLinkChanged;
        _cam.PropsChanged += OnPropsChanged;
        _cam.FramesChanged += ShowFrames;
        _cam.FocusMoved += ShowFocusBar;

        // A button hides the press from ordinary handlers, so catch it on the way in. Holding it is what makes a burst.
        ShutterButton.AddHandler(PointerPressedEvent, (_, _) => _cam.ShutterDown(), RoutingStrategies.Tunnel);
        ShutterButton.AddHandler(PointerReleasedEvent, (_, _) => _cam.ShutterUp(), RoutingStrategies.Tunnel);
        ShutterButton.AddHandler(PointerCaptureLostEvent, (_, _) => _cam.ShutterUp(), RoutingStrategies.Bubble);

        var big = PropControl.Kind.Stepper;
        var pick = PropControl.Kind.Picker;
        Add(ExposurePanel, "ISO", PropCode.IsoSensitivity, Format.Iso, big, v => (v & 0xFFFFFF) == 0xFFFFFF ? -1 : v & 0xFFFFFF);
        Add(ExposurePanel, "Shutter", PropCode.ShutterSpeed, Format.Shutter, big, v => -Format.ShutterSeconds(v));
        Add(ExposurePanel, "Iris", PropCode.FNumber, Format.FNumber, big, v => v);
        Add(FocusPanel, "Focus mode", PropCode.FocusMode, Format.Named("FocusMode"), PropControl.Kind.Buttons);
        Add(FocusPanel, "Tap does", PropCode.FunctionOfTouchOperation, Format.Named("TouchFunction"), PropControl.Kind.Buttons);
        // The everyday areas as buttons: Wide, Zone, Center, Spot M and Expand Spot. The rest are under More.
        Add(FocusPanel, "Focus area", PropCode.FocusArea, Format.FocusArea, PropControl.Kind.Shortcuts).Shortcuts = [1, 2, 3, 5, 7];
        Add(FocusPanel, "Detect", PropCode.RecognitionTarget, Format.Detect, PropControl.Kind.Buttons);
        _frameRate = Add(RecordingPanel, "Frame rate", PropCode.Movie_Recording_FrameRateSetting, Format.MovieFrameRate, pick, onPhone: false);
        var shootsAt = Add(RecordingPanel, "S&Q shoots at", PropCode.SQFrameRate, Format.SlowQuickRate, pick, v => v, onPhone: false);
        var playsAt = Add(RecordingPanel, "S&Q plays at", PropCode.SQRecordingFrameRateSetting, Format.MovieFrameRate, pick, onPhone: false);
        Add(RecordingPanel, "File format", PropCode.Movie_File_Format, Format.Named("MovieFileFormat"), pick, onPhone: false);
        _recSetting = Add(RecordingPanel, "Rec setting", PropCode.Movie_Recording_Setting, Format.Named("MovieRecordingSetting"), pick, onPhone: false);
        _slowQuickRows = [shootsAt, playsAt, Add(RecordingPanel, "S&Q rec setting", PropCode.SQRecordingSetting, Format.Named("MovieRecordingSetting"), pick, onPhone: false)];
        Add(FocusSetup, "Face/eye priority", PropCode.SubjectRecognitionInAF, Format.Named("SubjectInAf"), PropControl.Kind.Buttons);
        Add(FocusSetup, "Which eye", PropCode.RightLeftEyeSelect, Format.Named("EyeSelect"), PropControl.Kind.Buttons);
        _recordLevel = Add(AudioSetup, "Record level", PropCode.AudioInputMasterLevel, v => v.ToString(), big, v => v);
        Add(AudioSetup, "Audio recording", PropCode.AudioRecording, Format.Named("AudioRecording"), PropControl.Kind.Buttons);
        Add(AudioSetup, "Wind noise reduction", PropCode.WindNoiseReduct, Format.Named("WindNoise"), PropControl.Kind.Buttons);
        Add(MorePanel, "Exposure mode", PropCode.ExposureProgramMode, Format.Named("ExposureProgram"), pick);
        Add(MorePanel, "White balance", PropCode.WhiteBalance, Format.Named("WhiteBalance"), pick);
        Add(MorePanel, "Color temp", PropCode.Colortemp, Format.ColorTemp, pick, v => v);
        Add(MorePanel, "Touch operation", PropCode.TouchOperation, Format.Named("TouchOperation"), PropControl.Kind.Buttons);
        Add(MovieMore, "Shooting mode", PropCode.MovieShootingMode, Format.Named("MovieShootingMode"), pick);
        Add(MovieMore, "Monitor LUT", PropCode.MonitorLUTSetting, Format.Named("MonitorLut"), PropControl.Kind.Buttons);
        Add(MovieMore, "File format", PropCode.Movie_File_Format, Format.Named("MovieFileFormat"), pick);
        Add(MovieMore, "Rec setting", PropCode.Movie_Recording_Setting, Format.Named("MovieRecordingSetting"), pick);
        Add(MovieMore, "Frame rate", PropCode.Movie_Recording_FrameRateSetting, Format.MovieFrameRate, pick);
        Add(MovieMore, "HDMI out", PropCode.Movie_HDMIOutputResolution, Format.Named("HdmiResolution"), pick);
        Add(DrivePanel, "Drive", PropCode.DriveMode, Format.Drive, pick);
        Add(SavePanel, "Save photos to", PropCode.StillImageStoreDestination, Format.SaveTo, PropControl.Kind.Buttons);
        Add(SavePanel, "Computer's copy", PropCode.Still_Image_Trans_Size, Format.ComputerCopy, PropControl.Kind.Buttons);
        Add(SavePanel, "Send to computer", PropCode.RAW_J_PC_Save_Image, Format.Photo("RawJpegToPc"), PropControl.Kind.Buttons);
        Add(PhotoPanel, "File format", PropCode.FileType, Format.Photo("FileType"), PropControl.Kind.Buttons);
        Add(PhotoPanel, "RAW type", PropCode.RAW_FileCompressionType, Format.Photo("RawType"), PropControl.Kind.Buttons);
        Add(PhotoPanel, "JPEG quality", PropCode.StillImageQuality, Format.Photo("ImageQuality"), pick);
        Add(PhotoPanel, "Image size", PropCode.ImageSize, Format.Photo("ImageSize"), PropControl.Kind.Buttons);
        Add(PhotoPanel, "Aspect ratio", PropCode.AspectRatio, Format.Photo("AspectRatio"), PropControl.Kind.Buttons);
        Add(PhotoPanel, "Metering", PropCode.MeteringMode, Format.Photo("Metering"), pick);
        Add(PhotoPanel, "Shutter type", PropCode.ShutterType, Format.Photo("ShutterType"), PropControl.Kind.Buttons);
        Add(TimeLapsePanel, "Time-lapse", PropCode.Interval_Rec_Mode, Format.Photo("IntervalMode"), PropControl.Kind.Buttons);
        Add(TimeLapsePanel, "One photo every", PropCode.IntervalRec_ShootingInterval, Format.Interval, pick, v => v, IntervalChoices);
        Add(TimeLapsePanel, "Photos", PropCode.IntervalRec_NumberOfShots, v => v.ToString("N0"), pick, v => v, ShotChoices);
        Add(TimeLapsePanel, "Start after", PropCode.IntervalRec_ShootingStartTime, v => Format.Interval(v * 10), pick, v => v, StartChoices);
        MakePresetButtons();
        MakeRecordSubjectButtons();
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
        _hideFocusMark = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
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

    private static byte[] ShrinkForBird(byte[] jpeg, int size)
    {
        using var full = SKBitmap.Decode(jpeg);
        using var small = full.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var rgba = small.GetPixelSpan();
        var rgb = new byte[size * size * 3];
        for (int i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
        {
            rgb[o] = rgba[i];
            rgb[o + 1] = rgba[i + 1];
            rgb[o + 2] = rgba[i + 2];
        }
        return rgb;
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
        _ = ShowUpdateAsync();
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
    private async Task ShowUpdateAsync()
    {
        if (await UpdateCheck.NewerVersionAsync() is not { } version) return;
        UpdateButton.Content = $"CamRemote {version} is out. Click to download";
        UpdateButton.IsVisible = true;
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e) => UpdateCheck.OpenDownloadPage();

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
        _bird.Stop();
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
        PictureNote.IsVisible = _useWifi;
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
        if ((_useWifi || _phoneWatching || _bird.On) && _cam.State == LinkState.Connected) _live.Start();
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
            if (_cam.CancelTouch() is { } nothing) ShowMessage(nothing);
            return;
        }
        if (_cam.State != LinkState.Connected || Picture.Bounds.Width <= 0) return;
        var p = point.Position;
        double fx = p.X / Picture.Bounds.Width, fy = p.Y / Picture.Bounds.Height;
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
        ShowFocusMark(p.X, p.Y);
    }

    private void ShowFocusMark(double x, double y)
    {
        Canvas.SetLeft(FocusMark, x - FocusMark.Width / 2);
        Canvas.SetTop(FocusMark, y - FocusMark.Height / 2);
        FocusMark.IsVisible = true;
        _hideFocusMark.Stop();
        _hideFocusMark.Start();
    }

    private void FocusOnAnimal(double fx, double fy)
    {
        if (_cam.State != LinkState.Connected || _zoom.Ratio > 0) return;
        if (_cam.TouchFocus(fx, fy, evenIfTapIsOff: true) is { } problem) Log.Write("Animal focus: " + problem);
        else ShowFocusMark(fx * Picture.Bounds.Width, fy * Picture.Bounds.Height);
    }

    /// <summary>Draws the box around what the trigger sees. The label stays upright however the picture is turned.</summary>
    private void ShowAnimal((double X, double Y, double W, double H)? box)
    {
        double w = Picture.Bounds.Width, h = Picture.Bounds.Height;
        bool show = box != null && w > 0 && _zoom.Ratio == 0 && _cam.MagnifyRatio == 0;
        AnimalBox.IsVisible = show;
        AnimalLabel.IsVisible = show;
        if (!show) return;
        var b = box!.Value;
        AnimalBox.Width = Math.Max(b.W * w, 24);
        AnimalBox.Height = Math.Max(b.H * h, 24);
        double left = b.X * w - AnimalBox.Width / 2, top = b.Y * h - AnimalBox.Height / 2;
        Canvas.SetLeft(AnimalBox, left);
        Canvas.SetTop(AnimalBox, top);
        // The corner that ends up top left on screen
        Canvas.SetLeft(AnimalLabel, _settings.Rotation is 0 or 90 ? left : left + AnimalBox.Width);
        Canvas.SetTop(AnimalLabel, _settings.Rotation is 0 or 270 ? top : top + AnimalBox.Height);
        AnimalLabel.RenderTransform = new RotateTransform(-_settings.Rotation);
    }

    private static readonly SolidColorBrush FrameFocusedBrush = new(Color.FromRgb(0x4C, 0xD9, 0x64)), FrameOtherBrush = new(Colors.White);

    /// <summary>Draws the boxes the camera has on its own screen. While magnified they don't line up with the picture.</summary>
    private void ShowFrames()
    {
        FrameLayer.Children.Clear();
        double w = Picture.Bounds.Width, h = Picture.Bounds.Height;
        if (_cam.MagnifyRatio == 0 && _zoom.Ratio == 0 && w > 0)
        {
            foreach (var f in _cam.FramesToShow)
            {
                var box = new Rectangle
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
        foreach (var layer in new Control[] { Picture, AssistLayer })
        {
            layer.RenderTransformOrigin = new RelativePoint(x, y, RelativeUnit.Relative);
            layer.RenderTransform = new ScaleTransform(_zoom.Scale, _zoom.Scale);
        }
        ShowFrames();
        ShowFocusTools();
    }

    private void OnZoomBadgeClick(object? sender, RoutedEventArgs e) => OnMagnifyClick(0);

    private void OnMagnifyClick(long ratio)
    {
        if (_cam.MagnifyChoices().Count > 0) _cam.Magnify(ratio);
        else
        {
            _zoom.Set(ratio);
            ApplyZoom();
        }
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

    /// <summary>The page always shows what the camera is doing, so a click asks the camera and waits for it to change.</summary>
    private void OnPageClick(object? sender, RoutedEventArgs e)
    {
        bool wantPhoto = sender == PhotoPage;
        _setup = false;
        SideScroll.ScrollToHome();
        ShowPage(_cam.IsPhotoMode);
        if (_cam.State == LinkState.Connected && _cam.SwitchPage(wantPhoto) is { } problem) ShowMessage(problem);
    }

    /// <summary>Normal movie or S&Q, keeping the same exposure letter. The buttons follow what the camera reports.</summary>
    private void OnSlowQuickClick(object? sender, RoutedEventArgs e)
    {
        ShowPage(_cam.IsPhotoMode);
        if (_cam.SetSlowQuick(sender == SlowQuick) is { } problem) ShowMessage(problem);
    }

    private void OnSetupClick(object? sender, RoutedEventArgs e)
    {
        _setup = true;
        SideScroll.ScrollToHome();
        ShowPage(_cam.IsPhotoMode);
    }

    private void OnOpenPhotos(object? sender, RoutedEventArgs e)
    {
        if (System.IO.Directory.Exists(_cam.PhotoFolder)) System.Diagnostics.Process.Start("open", [_cam.PhotoFolder]);
        else ShowMessage("That folder isn't there yet. It's made when a camera connects.");
    }

    /// <summary>Shows the log in Finder, so it's easy to send along when something went wrong.</summary>
    private void OnShowLogPressed(object? sender, PointerPressedEventArgs e)
    {
        if (System.IO.File.Exists(Log.FilePath)) System.Diagnostics.Process.Start("open", ["-R", Log.FilePath]);
        else ShowMessage("There's no log yet.");
    }

    private async void OnChangePhotos(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Save photos to this folder" });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
        _settings.PhotoFolder = folder;
        _settings.Save();
        _cam.UsePhotoFolder(folder);
        PhotoFolderText.Text = _cam.PhotoFolder;
    }

    /// <summary>Swaps the side panel between the video and photo tools.</summary>
    private void ShowPage(bool photo)
    {
        VideoPage.IsChecked = !_setup && !photo;
        PhotoPage.IsChecked = !_setup && photo;
        SetupTab.IsChecked = _setup;
        ShootPage.IsVisible = !_setup;
        SetupPage.IsVisible = _setup;
        VideoTop.IsVisible = VideoShoot.IsVisible = VideoSetup.IsVisible = !photo;
        bool slowQuick = _cam.IsSlowQuick;
        _frameRate.Root.IsVisible = _recSetting.Root.IsVisible = !slowQuick;
        foreach (var row in _slowQuickRows) row.Root.IsVisible = slowQuick;
        SlowQuickSwitch.IsVisible = _cam.OffersSlowQuick;
        NormalSpeed.IsChecked = !slowQuick;
        SlowQuick.IsChecked = slowQuick;
        AudioSection.IsVisible = !photo && !_useWifi;
        ShutterButton.IsVisible = DrivePanel.IsVisible = TimeLapseSection.IsVisible = PhotoSetup.IsVisible = photo;
        foreach (var row in _rows.Where(r => r.Section == "photo"))
            row.Root.IsVisible = _cam.Applies(row.Code);
        FolderRow.IsVisible = _cam.SendsToComputer;
        PhotoFolderText.Text = _cam.PhotoFolder;
        ShutterLabel.Text = _cam.TimeLapseRunning ? "Stop time-lapse" : _cam.TimeLapseOn ? "Start time-lapse" : "Take photo";
        var interval = _cam.Get(PropCode.IntervalRec_ShootingInterval);
        var shots = _cam.Get(PropCode.IntervalRec_NumberOfShots);
        TimeLapsePlan.Text = interval == null || shots == null ? "" : Format.TimeLapse(interval.Value, shots.Value);
    }

    private void OnCardMenuClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (int slot in _cam.CardSlots)
        {
            var item = new MenuItem { Header = $"Format Card {slot}…", IsEnabled = _cam.CanFormat(slot) };
            item.Click += async (_, _) => await AskToFormatAsync(slot);
            menu.Items.Add(item);
        }
        menu.Open(CardMenu);
    }

    private async Task AskToFormatAsync(int slot)
    {
        _cam.HoldForFormat();
        if (await ConnectWindow.AskAsync(this, "Format card", _cam.FormatQuestion(slot), "Erase card", "Cancel")) _cam.FormatCard(slot);
        else _cam.EndFormat();
    }

    // ---- Automatic recording ----

    // What automatic recording can look for. Only animals for now: a new subject needs its own detector in BirdTrigger.
    private static readonly (string Name, string Tip)[] RecordSubjects =
    [
        ("Animal", "Birds and a few other animals: cats, dogs, horses, sheep, cows, elephants, bears, zebras and giraffes."),
    ];

    private void MakeRecordSubjectButtons()
    {
        foreach (var (subject, tip) in RecordSubjects)
        {
            var b = new ToggleButton
            {
                Content = subject, Margin = new Thickness(2), Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                IsChecked = subject == RecordSubjects[0].Name,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { foreach (ToggleButton other in RecordSubjectButtons.Children) other.IsChecked = other == b; };
            RecordSubjectButtons.Children.Add(b);
        }
    }

    private void OnBirdWatchClick(object? sender, RoutedEventArgs e)
    {
        bool on = BirdWatch.IsChecked == true;
        if (on) _bird.Start();
        else _bird.Stop();
        BirdStatus.IsVisible = on;
        RecordSubjectPanel.IsVisible = on;
        UpdateLiveView();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9 && _cam.State == LinkState.Connected)
        {
            if (_cam.IsPhotoMode)
            {
                _cam.ShutterDown();
                _cam.ShutterUp();
            }
            else _cam.ToggleRecord();
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
        RecButton.IsEnabled = ShutterButton.IsEnabled = _cam.State == LinkState.Connected;
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

        // The focus ring only does anything in MF or DMF, so it's hidden otherwise.
        long focusMode = _cam.Get(PropCode.FocusMode)?.Value ?? 0;
        bool manual = _cam.State == LinkState.Connected && focusMode is 1 or 6;
        ManualFocus.IsVisible = manual;

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
        bool photo = _cam.IsPhotoMode;
        // Every slot the camera has is shown, so an empty one is easy to spot.
        void Card(int slot, Prop? status, Prop? time, Prop? photos)
        {
            if (status == null) return;
            // The A7S III reports an empty slot as "recognising or locked" (3) rather than "no card" (1), with nothing left on it.
            bool empty = status.Value == 1 || status.Value == 3 && (time?.Value ?? 0) == 0 && (photos?.Value ?? 0) == 0;
            if (empty) parts.Add($"Card {slot}: empty");
            else if (status.Value != 0) parts.Add($"Card {slot}: not ready");
            else if (photo && photos != null) { parts.Add($"Card {slot}: {photos.Value:N0} photos"); minutes.Add(photos.Value / 10); }
            else if (time != null) { parts.Add($"Card {slot}: " + Format.Duration(time.Value)); minutes.Add(time.Value / 60); }
        }
        Card(1, _cam.Get(PropCode.MediaSLOT1_Status), slot1, _cam.Get(PropCode.MediaSLOT1_RemainingNumber));
        Card(2, _cam.Get(PropCode.MediaSLOT2_Status), slot2, _cam.Get(PropCode.MediaSLOT2_RemainingNumber));
        bool cardReady = _cam.Get(PropCode.MediaSLOT1_Status)?.Value == 0 || _cam.Get(PropCode.MediaSLOT2_Status)?.Value == 0;
        bool noCard = _cam.State == LinkState.Connected && !cardReady && _cam.Props.Count > 0;
        CardText.Text = noCard && parts.Count == 0 ? "No card ready" : string.Join("    ", parts);
        CardMenu.IsVisible = _cam.State == LinkState.Connected && _cam.CardSlots.Any();
        bool cardLow = minutes.Count > 0 && minutes.Max() < 10;
        CardText.Foreground = cardLow || noCard ? Rec : Brushes.White;

        // One banner over the picture for whatever needs attention most.
        string? alert = null;
        IBrush alertColor = Rec;
        if (_cam.State == LinkState.Connected && _cam.Props.Count > 0)
        {
            long pct = battery?.Value ?? -1, lvl = level?.Value ?? -1;
            if (Format.BatteryCritical(pct, lvl)) alert = "⚠  BATTERY ALMOST EMPTY — swap it now";
            else if (Format.BatteryLow(pct, lvl)) alert = "⚠  Low battery";
            else if (noCard) alert = "⚠  No memory card ready";
            else if (cardLow) alert = photo ? "⚠  Card almost full — under 100 photos left" : "⚠  Card almost full — under 10 minutes left";
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
        {
            if (!_cam.ModeChangedHere) ShowMessage($"The camera changed to {Format.Named("ExposureProgram")(mode.Value)}. Settings here now show what it reports.");
            _cam.LogOptions();
        }
        if (mode != null) _lastMode = mode;
        ShowPage(photo);
        ShowPresets();

        // Shutter, iris and ISO are already big on the right, so this says what they don't.
        CamSummary.Text = _cam.State != LinkState.Connected ? "" : string.Join("   ", (photo
            ? new[]
            {
                Show(PropCode.ExposureProgramMode, Format.Named("ExposureProgram")),
                Show(PropCode.FileType, Format.Photo("FileType")),
                Show(PropCode.DriveMode, Format.Drive),
                Show(PropCode.AspectRatio, Format.Photo("AspectRatio")),
            }
            : new[]
            {
                Show(PropCode.ExposureProgramMode, Format.Named("ExposureProgram")),
                _cam.IsSlowQuick ? "S&Q " + Show(PropCode.SQFrameRate, Format.SlowQuickRate) : Show(PropCode.Movie_Recording_FrameRateSetting, Format.MovieFrameRate),
                Show(PropCode.Movie_File_Format, Format.Named("MovieFileFormat")),
                Show(_cam.IsSlowQuick ? PropCode.SQRecordingSetting : PropCode.Movie_Recording_Setting, Format.Named("MovieRecordingSetting")),
            }).Where(s => s.Length > 0));

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
        ToolTip.SetTip(MessageBar, format.Length == 0 ? null : $"Picture: {format}, {frames} fps shown");
    }

    private void ShowMessage(string msg) => MessageText.Text = msg;

    // ---- Settings ----

    private PropControl Add(Panel panel, string label, PropCode code, Func<long, string> fmt, PropControl.Kind kind,
        Func<long, double>? order = null, long[]? presets = null, bool onPhone = true)
    {
        var c = new PropControl(_cam, label, code, fmt, kind, order)
        {
            Section = panel == ExposurePanel ? "exposure" : panel == AudioPanel || panel == AudioSetup ? "audio"
                : panel == MorePanel || panel == MovieMore || panel == RecordingPanel ? "more" : panel == FocusPanel || panel == FocusSetup ? "focus" : "photo",
            Presets = presets,
            OnPhone = onPhone,
        };
        panel.Children.Add(c.Root);
        _rows.Add(c);
        return c;
    }

    // ---- Presets ----

    private const int PresetSlots = 4;

    /// <summary>C1 to C4. A click on an empty one saves the camera's settings there; on a full one, puts them back.</summary>
    private void MakePresetButtons()
    {
        for (int slot = 1; slot <= PresetSlots; slot++)
        {
            int s = slot;
            // A row each: frame rate, with the format on the right, then the exposure below, so presets can be told apart at a glance.
            var name = new TextBlock { Text = $"C{s}", FontSize = 12, Foreground = Dim, Width = 28, Margin = new Thickness(0, 2, 0, 0) };
            var title = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var right = new TextBlock { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var detail = new TextBlock { FontSize = 12, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(name, Dock.Left);
            var row = new DockPanel { Children = { name, new StackPanel { Children = { new DockPanel { Children = { right, title } }, detail } } } };
            var b = new Button { Classes = { "Tile" }, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(2), Tag = new[] { title, right, detail }, Content = row };
            b.Click += (_, _) =>
            {
                if (PresetIn(s) is { } preset) { if (_cam.ApplyPreset(preset, $"C{s}") is { } problem) ShowMessage(problem); }
                else SavePreset(s);
            };
            var save = new MenuItem { Header = $"Save current settings as C{s}" };
            save.Click += (_, _) => SavePreset(s);
            var clear = new MenuItem { Header = $"Clear C{s}" };
            clear.Click += (_, _) =>
            {
                _settings.Presets.Remove(PresetKey(s));
                _settings.Save();
                ShowPresets();
            };
            b.ContextMenu = new ContextMenu { Items = { save, clear } };
            PresetButtons.Children.Add(b);
        }
    }

    /// <summary>Video and Photo each have their own C1 to C4.</summary>
    private string PresetKey(int slot) => (_cam.IsPhotoMode ? "photo" : "video") + slot;

    private Preset? PresetIn(int slot) => _settings.Presets.GetValueOrDefault(PresetKey(slot));

    private void SavePreset(int slot)
    {
        if (_cam.TakePreset() is not { } preset)
        {
            ShowMessage("Connect the camera first, then save its settings.");
            return;
        }
        _settings.Presets[PresetKey(slot)] = preset;
        _settings.Save();
        ShowMessage($"Saved C{slot}: {preset.Summary}. Click C{slot} to come back to these settings.");
        ShowPresets();
    }

    private void ShowPresets()
    {
        bool connected = _cam.State == LinkState.Connected;
        string page = _cam.IsPhotoMode ? "photo" : "video";
        for (int i = 0; i < PresetButtons.Children.Count; i++)
        {
            var b = (Button)PresetButtons.Children[i];
            var preset = PresetIn(i + 1);
            var lines = (TextBlock[])b.Tag!;
            var (top, middle, bottom) = preset == null ? ("Empty", "", "Click to save here") : CameraService.Describe(preset, _cam.IsPhotoMode);
            lines[0].Text = top.Length > 0 ? top : "Saved";
            lines[1].Text = middle;
            lines[2].Text = bottom;
            lines[1].IsVisible = middle.Length > 0;
            lines[2].IsVisible = bottom.Length > 0;
            // Green, with a green outline, when the camera is set that way now, as on the phone.
            bool now = preset != null && _cam.Matches(preset);
            lines[0].Foreground = preset == null ? Dim : now ? Ok : Brushes.White;
            b.BorderBrush = now ? Ok : Brushes.Transparent;
            b.IsEnabled = connected;
            ((MenuItem)b.ContextMenu!.Items[1]!).IsEnabled = preset != null;
            ToolTip.SetTip(b, preset == null ? $"Click to save the camera's {page} settings here."
                : $"Click to put these {page} settings back on the camera. Right-click to save over them or clear them.");
        }
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
        var choices = connected ? _cam.MagnifyChoices() : [];
        bool inApp = choices.Count == 0;
        if (inApp) choices = [20, 40];
        long ratio = inApp ? _zoom.Ratio : _cam.MagnifyRatio;
        var wanted = new List<long> { 0 };
        wanted.AddRange(choices);
        if (!wanted.SequenceEqual(MagnifierButtons.Children.OfType<Button>().Select(b => (long)b.Tag!)))
        {
            MagnifierButtons.Children.Clear();
            foreach (long r in wanted)
            {
                var b = new Button { Tag = r, Content = r == 0 ? "Off" : $"×{r / 10.0:0.#}", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 4, 0) };
                b.Click += (_, _) => OnMagnifyClick((long)b.Tag!);
                MagnifierButtons.Children.Add(b);
            }
        }
        foreach (var b in MagnifierButtons.Children.OfType<Button>())
        {
            b.IsEnabled = connected || inApp;
            bool on = (long)b.Tag! == ratio && ratio > 0;
            b.FontWeight = (long)b.Tag! == ratio ? FontWeight.Bold : FontWeight.Normal;
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
        ZoomBadge.IsVisible = ratio > 0;
        ShowFocusBar();
        bool tracking = connected && _cam.IsTracking;
        _showedTracking = tracking;
        StopTracking.IsVisible = tracking;
        // Only say something when a click won't simply focus; the everyday tip waits on the picture's tooltip.
        TouchHint.Text = !connected ? ""
            : ratio > 0 ? "Zoomed in. Click the picture to move around. Off goes back to clicking to focus."
            : _cam.Get(PropCode.FunctionOfTouchOperation)?.Value == 1 ? "Tap does is Off, so clicks do nothing."
            : _cam.IsManualFocus ? "Click the picture to autofocus once. It stays in manual."
            : "";
        TouchHint.IsVisible = TouchHint.Text.Length > 0;
        ToolTip.SetTip(Picture, !connected || ratio > 0 ? null
            : tracking ? "Right-click or Control-click to stop tracking."
            : _cam.CanCancelTouch ? "Click to focus somewhere else. Right-click or Control-click cancels."
            : _cam.TapTracks ? "Click a subject to track it."
            : "Click to focus there.");
    }

    private void OnStopTrackingClick(object? sender, RoutedEventArgs e)
    {
        if (_cam.CancelTouch() is { } problem) ShowMessage(problem);
    }

    // ---- Focus wheel ----

    private const double WheelPixelsPerStep = 12, WheelTickGap = 14;
    private double _wheelLastX, _wheelTurned;

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
        DrawWheel();
        _cam.TurnRing(pixels);
    }

    private bool _wheelHeld;

    private void OnWheelDown(object? sender, PointerPressedEventArgs e)
    {
        _wheelLastX = e.GetPosition(FocusWheel).X;
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

    /// <summary>Draws the range from close up to infinity, with a marker where focus is now.</summary>
    private void ShowFocusBar()
    {
        double w = FocusBar.Bounds.Width - 2, h = FocusBar.Bounds.Height - 2;
        FocusBarTicks.Children.Clear();
        if (w <= 0) return;
        for (int i = 0; i <= 20; i++)
        {
            bool major = i % 5 == 0;
            var tick = new Rectangle { Width = 1, Height = major ? 12 : 6, Fill = major ? Brushes.Gray : Brushes.DimGray };
            Canvas.SetLeft(tick, i * (w - 1) / 20);
            Canvas.SetTop(tick, h - tick.Height);
            FocusBarTicks.Children.Add(tick);
        }
        if (_cam.FocusPosition is not { } spot) return;
        var marker = new Rectangle { Width = 3, Height = h, Fill = Brushes.Gold };
        Canvas.SetLeft(marker, spot * (w - 3));
        FocusBarTicks.Children.Add(marker);
    }

    private void OnFocusBarSized(object? sender, SizeChangedEventArgs e) => ShowFocusBar();

    private void OnFocusBarClick(object? sender, PointerPressedEventArgs e)
    {
        if (_cam.FocusTo(e.GetPosition(FocusBar).X / FocusBar.Bounds.Width) is { } problem) ShowMessage(problem);
    }

    private void OnFocusStep(object? sender, RoutedEventArgs e) =>
        _cam.FocusStep(int.Parse((string)((Control)sender!).Tag!));

    // ---- Shooting guides ----

    private static readonly int[] ZebraLevels = [70, 80, 90, 95, 100];
    private static readonly (string Name, uint Color)[] PeakColors =
        [("Red", 0xFF3030u), ("Yellow", 0xFFE030u), ("Cyan", 0x30E0FFu), ("White", 0xFFFFFFu)];

    private void SetUpAssist()
    {
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
        Canvas.SetLeft(ExposureNeedle, MeterX(s) - 1);
        ExposureText.Text = Format.Meter(s);
        ExposureNeedle.Fill = Math.Abs(s) <= 0.35 ? Ok : Math.Abs(s) >= 2 ? Warn : Brushes.White;
        ExposureText.Foreground = ExposureNeedle.Fill;
    }

    // -3 to +3 stops across the meter, with room at the ends for the numbers.
    private double MeterX(double stops) => 10 + (Math.Clamp(stops, -3.1, 3.1) + 3) * (ExposureScale.Bounds.Width - 20) / 6;

    private void OnExposureScaleSized(object? sender, SizeChangedEventArgs e)
    {
        ExposureScale.Children.Clear();
        for (int stop = -3; stop <= 3; stop++)
        {
            double h = stop == 0 ? 18 : 8;
            var tick = new Rectangle { Width = 1, Height = h, Fill = Dim };
            Canvas.SetLeft(tick, MeterX(stop));
            Canvas.SetTop(tick, (18 - h) / 2);
            var number = new TextBlock { Text = stop == 0 ? "0" : $"{stop:+0;-0}", Width = 24, TextAlignment = TextAlignment.Center, FontSize = 10, Foreground = Dim };
            Canvas.SetLeft(number, MeterX(stop) - 12);
            Canvas.SetTop(number, 18);
            ExposureScale.Children.Add(tick);
            ExposureScale.Children.Add(number);
        }
        ExposureScale.Children.Add(ExposureNeedle);
        ShowMeter();
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

    // The code stays hidden unless asked for, so it doesn't end up in a screen recording.
    private bool _codeShown;
    private int _codeShowing; // counts each showing, so only the latest one hides it again

    private void ShowPhoneInfo()
    {
        PhoneInfo.Text = _phone == null ? "" : string.Join("\n", PhoneServer.Addresses(_settings.PhonePort))
            + $"\nAccess code: {(_codeShown ? _settings.PhoneCode : "••••••••")}";
        PhoneCodeShow.Content = _codeShown ? "Hide code" : "Show code";
    }

    private async Task ShowCodeAsync()
    {
        int showing = ++_codeShowing;
        _codeShown = true;
        ShowPhoneInfo();
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (showing != _codeShowing) return;
        _codeShown = false;
        ShowPhoneInfo();
    }

    private async void OnPhoneCodeShow(object? sender, RoutedEventArgs e)
    {
        if (!_codeShown)
        {
            await ShowCodeAsync();
            return;
        }
        _codeShowing++;
        _codeShown = false;
        ShowPhoneInfo();
    }

    private async void OnPhoneCodeNew(object? sender, RoutedEventArgs e)
    {
        if (!await ConnectWindow.AskAsync(this, "New code", "Make a new access code? Phones already signed in will need to enter it.", "Make new code", "Cancel")) return;
        _settings.NewPhoneSecrets();
        _settings.Save();
        await ShowCodeAsync();
    }

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
        PhoneCodeButtons.IsVisible = _phone != null;
        ShowPhoneInfo();
        KeepAwake();
    }

    // ---- Audio meters ----

    /// <summary>
    /// Meters the capture card's audio input when the picture comes over HDMI. Over Wi-Fi the camera sends
    /// no sound, so the meters are hidden and Record level lives in Setup instead.
    /// </summary>
    private void UpdateMeter(VideoDevice? hdmi)
    {
        _meterTick.Stop();
        _meter.Stop();
        string hint = "";
        if (hdmi != null)
            hint = _meter.Start(hdmi.Name) ?? "If the bars stay still, turn on the camera's HDMI audio output.";
        var home = hdmi != null ? AudioPanel : AudioSetup;
        if (_recordLevel.Root.Parent != home)
        {
            ((Panel)_recordLevel.Root.Parent!).Children.Remove(_recordLevel.Root);
            home.Children.Insert(0, _recordLevel.Root);
        }
        AudioSection.IsVisible = hdmi != null && VideoTop.IsVisible;
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
