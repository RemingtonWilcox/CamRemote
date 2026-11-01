using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace CamRemote;

/// <summary>Looks once at launch for a newer release on GitHub. It only tells the user: nothing is downloaded or installed.</summary>
public static class UpdateCheck
{
    public const string Current = "1.0.4"; // also in tools/windows/CamRemote.iss and tools/mac/Info.plist
    private const string Latest = "https://api.github.com/repos/RemingtonWilcox/CamRemote/releases/latest";
    private const string Page = "https://github.com/RemingtonWilcox/CamRemote/releases/latest";

    /// <summary>The newer version's number, or null if this one is current or GitHub can't be reached.</summary>
    public static async Task<string?> NewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CamRemote/" + Current);
            var tag = JsonDocument.Parse(await http.GetStringAsync(Latest)).RootElement.GetProperty("tag_name").GetString() ?? "";
            Log.Write($"This is {Current}; the latest release is {tag}.");
            return Version.TryParse(tag.TrimStart('v'), out var latest) && latest > Version.Parse(Current) ? latest.ToString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write("Couldn't check for a new version: " + ex.Message);
            return null;
        }
    }

    public static void OpenDownloadPage() => Process.Start(new ProcessStartInfo(Page) { UseShellExecute = true });
}
