using System.Windows;
using System.Windows.Controls;

namespace CamRemote;

public partial class ConnectWindow : Window
{
    private readonly CameraService _cam;
    private readonly Settings _settings;

    public ConnectWindow(CameraService cam, Settings settings)
    {
        InitializeComponent();
        _cam = cam;
        _settings = settings;
        IpBox.Text = settings.CameraIp;
        UserBox.Text = settings.CameraUser;
        PasswordBox.Password = settings.LoadPassword();
        AutoBox.IsChecked = settings.AutoConnect;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        SearchButton.IsEnabled = false;
        SearchStatus.Text = "Searching…";
        var found = await _cam.DiscoverAsync();
        Found.ItemsSource = found;
        SearchStatus.Text = found.Count == 0
            ? "None found. Check the camera is on the same Wi-Fi with PC Remote on, or type its IP below."
            : "";
        var saved = found.FirstOrDefault(c => c.Ip == _settings.CameraIp) ?? found.FirstOrDefault();
        if (saved != null) Found.SelectedItem = saved;
        SearchButton.IsEnabled = true;
    }

    private void OnFoundSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Found.SelectedItem is FoundCamera { Ip.Length: > 0 } c) IpBox.Text = c.Ip;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (Found.SelectedItem == null && IpBox.Text.Trim().Length == 0)
        {
            SearchStatus.Text = "Pick a camera or type its IP address.";
            return;
        }
        _settings.CameraIp = IpBox.Text.Trim();
        _settings.CameraUser = UserBox.Text.Trim();
        _settings.AutoConnect = AutoBox.IsChecked == true;
        _settings.SavePassword(PasswordBox.Password);
        _settings.Save();
        DialogResult = true;
    }

    /// <summary>Connects to the camera picked in this dialog.</summary>
    public Task<bool> ConnectAsync(Action<string> say)
    {
        var picked = Found.SelectedItem as FoundCamera;
        // A found camera whose IP was typed over is treated as "connect by IP".
        if (picked != null && picked.Conn == "IP" && picked.Ip != _settings.CameraIp) picked = null;
        return ConnectAsync(_cam, _settings, picked, PasswordBox.Password, say);
    }

    /// <summary>
    /// Connects to the camera saved in settings (at startup and when reconnecting). If the router has
    /// given the camera a new IP, it's found again by its MAC address. Returns false if no connect
    /// was started (camera off or unreachable).
    /// </summary>
    public static async Task<bool> ConnectSavedAsync(CameraService cam, Settings settings, Action<string> say, bool quiet = false)
    {
        if (!quiet) say($"Looking for the camera at {settings.CameraIp}…");
        var found = await cam.DiscoverAsync();
        var picked = found.FirstOrDefault(c => c.Ip == settings.CameraIp)
                     ?? found.FirstOrDefault(c => settings.CameraMac.Length > 0 && c.Mac == settings.CameraMac && c.Ip.Length > 0);
        // Not found by search: still try the saved IP directly; reading the fingerprint fails fast if it's absent.
        if (picked != null && picked.Ip != settings.CameraIp)
        {
            say($"The camera moved to {picked.Ip}; updating the saved address.");
            settings.CameraIp = picked.Ip;
            settings.Save();
        }
        return await ConnectAsync(cam, settings, picked, settings.LoadPassword(), say);
    }

    private static async Task<bool> ConnectAsync(CameraService cam, Settings settings, FoundCamera? picked,
        string password, Action<string> say)
    {
        int index;
        bool ssh;
        if (picked != null)
        {
            index = picked.Index;
            ssh = picked.Ssh;
            if (picked.Mac.Length > 0 && picked.Mac != settings.CameraMac)
            {
                settings.CameraMac = picked.Mac;
                settings.Save();
            }
        }
        else
        {
            int err = await cam.AddByIpAsync(settings.CameraIp);
            if (err != 0)
            {
                say($"\"{settings.CameraIp}\" isn't a usable IP address.");
                return false;
            }
            index = -1;
            ssh = true;
        }

        string fingerprint = "";
        if (ssh)
        {
            fingerprint = await cam.FingerprintAsync(index) ?? "";
            if (fingerprint.Length == 0)
            {
                say("Couldn't reach the camera to read its security fingerprint. Is it on, on Wi-Fi, with PC Remote on?");
                return false;
            }
            if (settings.Fingerprint.Length > 0 && settings.Fingerprint != fingerprint)
            {
                var answer = MessageBox.Show(
                    "The camera's security fingerprint has changed since last time.\n\n" +
                    "That's expected after a camera reset or a new Access Authen. setting. " +
                    "Otherwise another device may be pretending to be your camera.\n\nConnect anyway?",
                    "CamRemote", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    cam.HoldReconnect = true; // don't keep asking
                    return false;
                }
            }
            settings.Fingerprint = fingerprint;
            settings.Save();
        }

        say("Connecting…");
        cam.Connect(index, settings.CameraUser, password, fingerprint);
        return true;
    }
}
