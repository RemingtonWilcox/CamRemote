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
        ModelBox.ItemsSource = CameraModels.All;
        ModelBox.SelectedItem = CameraModels.Find(settings.CameraModel);
        UserBox.Text = settings.CameraUser;
        PasswordBox.Password = settings.LoadPassword();
        AutoBox.IsChecked = settings.AutoConnect;
        JoinAddressBox.Text = settings.JoinAddress.Replace("http://", "");
    }

    /// <summary>Set when the person chose to use the camera through another CamRemote.</summary>
    public RemoteLink? Joined { get; private set; }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (RemoteLink.Normalize(JoinAddressBox.Text) is not { } address)
        {
            JoinStatus.Text = "Type the address the other RemoteCam shows, like 100.101.102.103:8787.";
            return;
        }
        JoinButton.IsEnabled = false;
        JoinStatus.Text = "Joining…";
        string code = JoinCodeBox.Text.Trim();
        string saved = address == _settings.JoinAddress ? _settings.LoadJoinToken() : "";
        var (link, problem) = code.Length == 0 && saved.Length > 0
            ? await RemoteLink.ResumeAsync(address, saved)
            : code.Length > 0 ? await RemoteLink.SignInAsync(address, code) : (null, "Type the access code the other RemoteCam shows.");
        JoinButton.IsEnabled = true;
        if (link == null)
        {
            JoinStatus.Text = problem;
            return;
        }
        _settings.JoinAddress = address;
        _settings.Save();
        _settings.SaveJoinToken(link.Token);
        Joined = link;
        DialogResult = true;
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
        if (Found.SelectedItem is not FoundCamera c) return;
        if (c.Ip.Length > 0) IpBox.Text = c.Ip;
        if (CameraModels.Find(c.Model) is { } model) ModelBox.SelectedItem = model;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (Found.SelectedItem == null && IpBox.Text.Trim().Length == 0)
        {
            SearchStatus.Text = "Pick a camera or type its IP address.";
            return;
        }
        _settings.CameraIp = IpBox.Text.Trim();
        if (ModelBox.SelectedItem is CameraModel typed) _settings.CameraModel = typed.Sdk;
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
        "RemoteCam", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
}
