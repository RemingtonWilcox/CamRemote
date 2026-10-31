using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CamRemote;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Environment.CurrentDirectory = AppContext.BaseDirectory; // Finder starts apps in /
        // The phone server never changes its config, and watching the folder makes macOS ask about Documents and Downloads.
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class AvaloniaUi : IUi
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public T Invoke<T>(Func<T> action) => Dispatcher.UIThread.Invoke(action);

    public IDisposable Every(TimeSpan interval, Action action) => DispatcherTimer.Run(() =>
    {
        action();
        return true;
    }, interval);
}
