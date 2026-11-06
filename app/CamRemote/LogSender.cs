using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CamRemote;

/// <summary>
/// Sends the log to CamRemote's developer when someone presses Send log, and only then. It goes to a small
/// Cloudflare service (tools/log-receiver) that keeps it for 90 days.
/// </summary>
public static class LogSender
{
    private const string Address = "https://camremote-logs.wilcoxremington.workers.dev/log";
    private const int MaxChars = 600_000; // the newest part; the service takes up to 1 MB

    /// <summary>A reference to quote when talking about it, or else what went wrong.</summary>
    public static async Task<(string? Reference, string? Problem)> SendAsync(string note)
    {
        string log = ReadLog();
        if (log.Length == 0) return (null, "There's no log yet.");
        string app = $"RemoteCam {UpdateCheck.Current}, {RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture}";
        var body = JsonSerializer.Serialize(new { app, note = note.Trim(), log });
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CamRemote/" + UpdateCheck.Current);
            using var response = await http.PostAsync(Address, new StringContent(body, Encoding.UTF8, "application/json"));
            var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            if (response.IsSuccessStatusCode && answer.TryGetProperty("id", out var id))
            {
                Log.Write($"Sent the log to the developer, reference {id.GetString()}.");
                return (id.GetString(), null);
            }
            return (null, answer.TryGetProperty("error", out var error) ? error.GetString() : "It didn't go through. Try again later.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Log.Write("Couldn't send the log: " + ex.Message);
            return (null, "It couldn't be sent. Check that this computer is online, then try again.");
        }
    }

    /// <summary>The newest part of the log, with this computer's user folder written as ~ so the account name stays here.</summary>
    private static string ReadLog()
    {
        var text = new StringBuilder();
        foreach (var file in new[] { Log.FilePath + ".old", Log.FilePath })
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text.Append(reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string all = home.Length > 0 ? text.ToString().Replace(home, "~") : text.ToString();
        return all.Length > MaxChars ? all[^MaxChars..] : all;
    }
}
