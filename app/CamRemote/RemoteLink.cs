using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamRemote;

public sealed class RemoteChoice
{
    public string Value { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>One setting as the host sends it. The PC and Mac use Sony SDK names for ids and the iPhone uses PTP codes, so treat them as opaque.</summary>
public sealed class RemoteRow
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Section { get; set; } = "more"; // exposure, focus, audio or more
    public string Kind { get; set; } = "Picker"; // Buttons, Stepper or Picker
    public string Value { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Enabled { get; set; }
    public List<RemoteChoice> Choices { get; set; } = [];
}

/// <summary>One of the host's saved C1 to C4, in the lines its own button shows.</summary>
public sealed class RemotePreset
{
    public int Slot { get; set; }
    public List<string> Lines { get; set; } = [];
}

public sealed class RemoteState
{
    public string Link { get; set; } = "disconnected";
    public bool Recording { get; set; }
    public int Rotation { get; set; }
    public string Battery { get; set; } = "";
    public string Card { get; set; } = "";
    public string Alert { get; set; } = "";
    public long[] BaseIso { get; set; } = [];
    public bool ManualFocus { get; set; }
    public double? Meter { get; set; }
    public long Magnify { get; set; }
    public long[] MagnifyChoices { get; set; } = [];
    public bool Tracking { get; set; }
    public int ZebraLevel { get; set; } = 90;
    public int PeakSensitivity { get; set; } = 1;
    public uint PeakColor { get; set; } = 0xFF3030;
    public List<RemoteRow> Rows { get; set; } = [];
    public bool Photo { get; set; }
    public bool SlowQuick { get; set; }
    public bool OffersSlowQuick { get; set; }
    public List<RemotePreset> Presets { get; set; } = []; // only the saved ones, for the side the host is on
    public int? ActiveSlot { get; set; }
    public bool TimeLapse { get; set; } // the iPhone app's own time-lapse is running

    public bool Connected => Link == "connected";
}

/// <summary>
/// Uses a camera that another CamRemote holds (the iPhone app, or the PC or Mac app with phone control on),
/// through the same control page a browser uses. The access code is typed once; after that the host's
/// token is kept in Windows Credential Manager or the macOS Keychain.
/// </summary>
public sealed class RemoteLink : IDisposable
{
    private const string Cookie = PhoneServer.Cookie;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private string _token;
    public string Address { get; }
    public string HostName { get; set; } = ""; // the computer's name, when it was found by itself
    public const string SignedOut = "Signed out: type the access code again.";
    public string CameraId { get; private set; } = "";
    public string CameraName { get; private set; } = "";

    private RemoteLink(string address, string token)
    {
        Address = address;
        _token = token;
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(6) })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>"100.1.2.3", "phone.tailnet.ts.net:8787" or a full http:// address, as the host's Setup shows it.</summary>
    public static string? Normalize(string typed)
    {
        string s = typed.Trim().TrimEnd('/');
        int label = s.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (label > 0) s = s[label..]; // pasted with the host's "Tailscale: " in front
        if (s.Length == 0) return null;
        if (!s.Contains("://")) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host.Length == 0) return null;
        int port = uri.IsDefaultPort && !s[(s.IndexOf("://") + 3)..].Contains(':') ? 8787 : uri.Port;
        return $"http://{uri.Host}:{port}";
    }

    /// <summary>Signs in with the access code. Returns the link, or why it didn't work.</summary>
    public static async Task<(RemoteLink? Link, string? Problem)> SignInAsync(string address, string code)
    {
        var link = new RemoteLink(address, "");
        try
        {
            using var r = await link._http.PostAsync("/api/login", Body(new { code = code.Trim().ToUpperInvariant() }));
            if (r.StatusCode == HttpStatusCode.Unauthorized) { link.Dispose(); return (null, "That access code isn't right."); }
            if (r.StatusCode == HttpStatusCode.Forbidden) { link.Dispose(); return (null, "Phone control is off on the other Egret, so its access code doesn't work. It can let this computer in by itself: click Connect camera… and answer there."); }
            if (!r.IsSuccessStatusCode) { link.Dispose(); return (null, $"The other Egret said no ({(int)r.StatusCode})."); }
            string? token = r.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? cookies.Select(c => c.Split(';')[0]).Where(c => c.StartsWith(Cookie + "=")).Select(c => c[(Cookie.Length + 1)..]).FirstOrDefault()
                : null;
            if (string.IsNullOrEmpty(token)) { link.Dispose(); return (null, "The other Egret didn't sign this computer in."); }
            link._token = token;
            return await link.FindCameraAsync();
        }
        catch (Exception ex)
        {
            link.Dispose();
            return (null, Unreachable(ex));
        }
    }

    /// <summary>Signs in with a token saved from last time. Returns null link with a problem if it's no longer accepted.</summary>
    public static async Task<(RemoteLink? Link, string? Problem)> ResumeAsync(string address, string token)
    {
        var link = new RemoteLink(address, token);
        try { return await link.FindCameraAsync(); }
        catch (Exception ex)
        {
            link.Dispose();
            return (null, Unreachable(ex));
        }
    }

    public string Token => _token;

    private async Task<(RemoteLink?, string?)> FindCameraAsync()
    {
        using var r = await Send(HttpMethod.Get, "/api/cameras");
        if (r.StatusCode == HttpStatusCode.Unauthorized) { Dispose(); return (null, SignedOut); }
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) { Dispose(); return (null, "The other Egret has no camera to share."); }
        CameraId = first.GetProperty("id").GetString() ?? "";
        CameraName = first.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        return (this, null);
    }

    private static string Unreachable(Exception ex) => ex switch
    {
        TaskCanceledException or HttpRequestException =>
            "Can't reach the other Egret. Check it's open, its phone or computer control is on, and both are on Tailscale or the same Wi-Fi. " +
            "If Tailscale says Connected but nothing gets through, switch Tailscale off and on there.",
        _ => ex.Message,
    };

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("Cookie", $"{Cookie}={_token}");
        if (method != HttpMethod.Get) req.Headers.Add("X-CamRemote", "1");
        if (body != null) req.Content = Body(body);
        return _http.SendAsync(req, ct);
    }

    // With its length up front: the iPhone app's small server reads Content-Length and doesn't take chunked bodies.
    private static StringContent Body(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    /// <summary>The host's camera, or null when the host can't be reached.</summary>
    public async Task<RemoteState?> StateAsync()
    {
        try
        {
            using var r = await Send(HttpMethod.Get, $"/api/{CameraId}/state");
            if (!r.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<RemoteState>(await r.Content.ReadAsStringAsync(), Json);
        }
        catch { return null; }
    }

    /// <summary>Hands each new picture to <paramref name="frame"/> until cancelled. The host waits for a new frame before answering, so this paces itself.</summary>
    public async Task RunFramesAsync(Action<byte[]> frame, CancellationToken ct)
    {
        string after = "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Without "after" until there's a frame number: an empty one is refused by Egret 1.0.12 and older.
                using var r = await Send(HttpMethod.Get, after.Length > 0 ? $"/api/{CameraId}/frame?after={after}" : $"/api/{CameraId}/frame", ct: ct);
                if (r.StatusCode == HttpStatusCode.OK)
                {
                    if (r.Headers.TryGetValues("X-Frame", out var no)) after = no.First();
                    frame(await r.Content.ReadAsByteArrayAsync(ct));
                }
                else await Task.Delay(r.StatusCode == HttpStatusCode.NoContent ? 50 : 1000, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { try { await Task.Delay(1000, ct); } catch { return; } }
        }
    }

    public Task<string?> SetAsync(string id, string value) => Act("set", new { id, value });
    public Task<string?> RecordAsync(bool on) => Act("record", new { on });
    public Task<string?> FocusAsync(double x, double y) => Act("focus", new { x, y });
    public Task<string?> FocusStepAsync(int step) => Act("focusstep", new { step });
    public Task<string?> PresetAsync(int slot, bool save) => Act("preset", new { slot, save });
    public Task<string?> ModeAsync(bool photo) => Act("mode", new { photo });
    public Task<string?> ShutterAsync(bool down) => Act("shutter", new { down });
    public Task<string?> SlowQuickAsync(bool on) => Act("slowquick", new { on });

    /// <summary>Null when it worked, otherwise what to tell the person.</summary>
    private async Task<string?> Act(string what, object body)
    {
        try
        {
            using var r = await Send(HttpMethod.Post, $"/api/{CameraId}/{what}", body);
            if (r.IsSuccessStatusCode) return null;
            if (r.StatusCode == HttpStatusCode.Conflict)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "The camera didn't take that.";
            }
            return r.StatusCode == HttpStatusCode.Unauthorized ? SignedOut : $"The other Egret refused that ({(int)r.StatusCode}).";
        }
        catch (Exception ex) { return Unreachable(ex); }
    }

    public void Dispose() => _http.Dispose();
}
