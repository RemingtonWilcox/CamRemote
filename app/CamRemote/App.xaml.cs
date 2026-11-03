using System.IO;
using System.Windows;

namespace CamRemote;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The installer moves aside files that a stuck older copy was still holding. Remove them once they're free.
        foreach (var old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old", SearchOption.AllDirectories))
            try { File.Delete(old); } catch { }
    }
}
