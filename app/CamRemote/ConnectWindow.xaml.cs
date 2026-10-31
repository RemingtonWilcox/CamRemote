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
        return CameraConnect.PickedAsync(_cam, _settings, picked, PasswordBox.Password, say, AskFingerprint);
    }

    /// <summary>Asks before trusting a camera whose fingerprint has changed.</summary>
    public static Task<bool> AskFingerprint() => Task.FromResult(MessageBox.Show(CameraConnect.FingerprintWarning,
        "CamRemote", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
}
