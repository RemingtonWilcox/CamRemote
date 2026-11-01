using System.IO;

namespace CamRemote;

/// <summary>A plain text log in <see cref="Settings.Folder"/>, for working out what happened. It stays on this computer.</summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(Settings.Folder, "camremote.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) file.MoveTo(FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a log must never get in the way
    }
}
