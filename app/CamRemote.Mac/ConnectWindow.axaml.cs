using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace CamRemote;

public partial class ConnectWindow : Window
{
    private readonly CameraService _cam = null!;
    private readonly Settings _settings = null!;

    public ConnectWindow() => InitializeComponent(); // for the XAML loader

    public ConnectWindow(CameraService cam, Settings settings) : this()
    {
        _cam = cam;
        _settings = settings;
        IpBox.Text = settings.CameraIp;
        ModelBox.ItemsSource = CameraModels.All;
        ModelBox.SelectedItem = CameraModels.Find(settings.CameraModel);
        UserBox.Text = settings.CameraUser;
        PasswordBox.Text = settings.LoadPassword();
        AutoBox.IsChecked = settings.AutoConnect;
        Opened += async (_, _) => await SearchAsync();
    }

    private async void OnSearch(object? sender, RoutedEventArgs e) => await SearchAsync();

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

    private void OnFoundSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Found.SelectedItem is not FoundCamera c) return;
        if (c.Ip.Length > 0) IpBox.Text = c.Ip;
        if (CameraModels.Find(c.Model) is { } model) ModelBox.SelectedItem = model;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        string ip = (IpBox.Text ?? "").Trim();
        if (Found.SelectedItem == null && ip.Length == 0)
        {
            SearchStatus.Text = "Pick a camera or type its IP address.";
            return;
        }
        _settings.CameraIp = ip;
        if (ModelBox.SelectedItem is CameraModel typed) _settings.CameraModel = typed.Sdk;
        _settings.CameraUser = (UserBox.Text ?? "").Trim();
        _settings.AutoConnect = AutoBox.IsChecked == true;
        _settings.SavePassword(PasswordBox.Text ?? "");
        _settings.Save();
        Close(true);
    }

    /// <summary>Connects to the camera picked in this dialog.</summary>
    public Task<bool> ConnectAsync(Action<string> say, Func<Task<bool>> askFingerprint)
    {
        var picked = Found.SelectedItem as FoundCamera;
        // A found camera whose IP was typed over is treated as "connect by IP".
        if (picked != null && picked.Conn == "IP" && picked.Ip != _settings.CameraIp) picked = null;
        return CameraConnect.PickedAsync(_cam, _settings, picked, PasswordBox.Text ?? "", say, askFingerprint);
    }

    /// <summary>A yes/no question over the main window.</summary>
    public static async Task<bool> AskAsync(Window owner, string title, string question, string yesText, string noText)
    {
        var yes = new Button { Content = yesText, Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = noText, IsDefault = true };
        var dialog = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
                },
            },
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }
}
