using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CamRemote;

/// <summary>One camera as the phone page sees it. Each camera is its own tab, and methods may be called on any thread.</summary>
public interface IRemoteCamera
{
    string Id { get; }
    string Name { get; }
    object State();
    (byte[] Jpeg, long No)? LatestFrame();
    /// <summary>These return null when done, or a message to show on the phone.</summary>
    string? Set(string id, string value);
    string? Record(bool on);
    string? Focus(double x, double y);
    string? FocusStep(int step);
    string? Preset(int slot, bool save);
    string? Mode(bool photo);
    string? SlowQuick(bool on);
    string? Shutter(bool down);
}

/// <summary>
/// Serves the phone control page, and the camera to other CamRemotes. Only home-network and Tailscale
/// addresses are answered. Everything but the page itself needs the access code shown in the app (with
/// phone control on), or the token of a CamRemote the person let join (see <see cref="Peers"/>).
/// </summary>
public sealed class PhoneServer : IAsyncDisposable
{
    public const string Cookie = "camremote";
    private readonly Settings _settings;
    private readonly Dictionary<string, IRemoteCamera> _cameras;
    private readonly Dictionary<string, long> _lastFrameAsk = new();
    private readonly Dictionary<string, long> _refusedUntil = new(); // by address, so a "no" isn't asked again straight away
    private int _asking; // 1 while the person is being asked about a device
    private WebApplication? _app;
    private UdpClient? _udp;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>The other CamRemotes the person let in.</summary>
    public PeerAllowList? Allowed { get; init; }

    /// <summary>True while this app holds a camera worth joining. Any thread.</summary>
    public Func<bool> HasCamera { get; init; } = () => false;

    /// <summary>
    /// Asks the person whether a device (id, name, address) may join, and lets it in if so: returns the
    /// token from <see cref="PeerAllowList.Allow"/>, or null for no.
    /// </summary>
    public Func<string, string, string, Task<string?>>? AskToJoin { get; init; }

    public PhoneServer(Settings settings, IEnumerable<IRemoteCamera> cameras)
    {
        _settings = settings;
        _cameras = cameras.ToDictionary(c => c.Id);
    }

    /// <summary>True while a phone has asked for this camera's picture in the last few seconds.</summary>
    public bool Watching(string camera)
    {
        lock (_lastFrameAsk)
            return _lastFrameAsk.TryGetValue(camera, out long at) && Environment.TickCount64 - at < 5000;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(_settings.PhonePort));
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip == null || !IsLocalOrTailscale(ip)) { ctx.Response.StatusCode = 403; return; }
            string path = ctx.Request.Path.Value ?? "";
            string? cookie = ctx.Request.Cookies[Cookie];
            bool signedIn = (_settings.PhoneEnabled && SameSecret(cookie, _settings.PhoneToken))
                            || (_settings.ShareWithEgrets && Allowed?.DeviceFor(cookie) != null);
            ctx.Items["signedIn"] = signedIn;
            if (path.StartsWith("/api/") && path is not ("/api/login" or "/api/hello" or "/api/pair"))
            {
                if (!signedIn) { ctx.Response.StatusCode = 401; return; }
                // Browsers won't add this header on another site's behalf.
                if (ctx.Request.Method != "GET" && !ctx.Request.Headers.ContainsKey("X-CamRemote")) { ctx.Response.StatusCode = 403; return; }
            }
            if (path == "/api/pair" && !ctx.Request.Headers.ContainsKey("X-CamRemote")) { ctx.Response.StatusCode = 403; return; }
            await next();
        });

        app.MapGet("/", () => Results.Bytes(Embedded("phone.html"), "text/html; charset=utf-8"));
        app.MapGet("/icon.png", () => Results.Bytes(Embedded("phone-icon.png"), "image/png"));
        app.MapGet("/manifest.webmanifest", () => Results.Json(new
        {
            name = "Egret", short_name = "Egret", start_url = "/", display = "standalone",
            background_color = "#101214", theme_color = "#101214",
            icons = new[] { new { src = "/icon.png", sizes = "180x180", type = "image/png" } },
        }));

        app.MapPost("/api/login", async (LoginBody body, HttpContext ctx) =>
        {
            if (!_settings.PhoneEnabled) return Results.StatusCode(403);
            if (!SameSecret(body.Code?.Trim().ToUpperInvariant(), _settings.PhoneCode))
            {
                await Task.Delay(500); // keeps guessing slow
                return Results.Unauthorized();
            }
            ctx.Response.Cookies.Append(Cookie, _settings.PhoneToken, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365),
            });
            return Results.Ok();
        });

        app.MapGet("/api/hello", (HttpContext ctx) => Results.Json(new
        {
            app = "egret",
            id = _settings.DeviceId,
            name = Peers.ThisName,
            camera = _settings.ShareWithEgrets && HasCamera(),
            addresses = ctx.Items["signedIn"] is true ? OwnAddresses().Select(a => $"http://{a.Ip}:{_settings.PhonePort}").ToList() : null,
        }));
        app.MapPost("/api/pair", PairAsync);
        app.MapGet("/api/cameras", () => Results.Json(_cameras.Values.Select(c => new { id = c.Id, name = c.Name })));
        app.MapGet("/api/{cam}/state", (string cam) => Find(cam) is { } c ? Results.Json(c.State()) : Results.NotFound());
        app.MapGet("/api/{cam}/frame", FrameAsync);
        app.MapPost("/api/{cam}/set", (string cam, SetBody b) => Do(cam, c => c.Set(b.Id, b.Value)));
        app.MapPost("/api/{cam}/record", (string cam, RecordBody b) => Do(cam, c => c.Record(b.On)));
        app.MapPost("/api/{cam}/focus", (string cam, FocusBody b) => Do(cam, c => c.Focus(b.X, b.Y)));
        app.MapPost("/api/{cam}/focusstep", (string cam, StepBody b) => Do(cam, c => c.FocusStep(b.Step)));
        app.MapPost("/api/{cam}/preset", (string cam, PresetBody b) => Do(cam, c => c.Preset(b.Slot, b.Save)));
        app.MapPost("/api/{cam}/mode", (string cam, ModeBody b) => Do(cam, c => c.Mode(b.Photo)));
        app.MapPost("/api/{cam}/slowquick", (string cam, RecordBody b) => Do(cam, c => c.SlowQuick(b.On)));
        app.MapPost("/api/{cam}/shutter", (string cam, ShutterBody b) => Do(cam, c => c.Shutter(b.Down)));

        await app.StartAsync();
        _app = app;
        StartAnswering();
    }

    /// <summary>
    /// A device asking to join for the first time. The person answers here, so this waits for them.
    /// Only one is asked about at a time, and an address told no isn't asked about again for five minutes.
    /// </summary>
    private async Task<IResult> PairAsync(PairBody body, HttpContext ctx)
    {
        if (!_settings.ShareWithEgrets || AskToJoin == null) return Results.StatusCode(403);
        string id = body.Id ?? "";
        if (id.Length is < 16 or > 64 || !id.All(char.IsAsciiHexDigit)) return Results.BadRequest();
        string address = Shown(ctx.Connection.RemoteIpAddress!);
        lock (_refusedUntil)
            if (_refusedUntil.TryGetValue(address, out long until) && Environment.TickCount64 < until) return Results.StatusCode(429);
        if (Interlocked.CompareExchange(ref _asking, 1, 0) != 0) return Results.StatusCode(409);
        try
        {
            string? token = await AskToJoin(id, Peers.Clean(body.Name), address);
            if (token == null)
            {
                lock (_refusedUntil) _refusedUntil[address] = Environment.TickCount64 + 5 * 60_000;
                return Results.StatusCode(403);
            }
            return Results.Json(new { token });
        }
        finally
        {
            _asking = 0;
        }
    }

    private static string Shown(IPAddress ip) => (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    /// <summary>Answers other CamRemotes asking who has the camera, on the same port number over UDP.</summary>
    private void StartAnswering()
    {
        try { _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _settings.PhonePort)); }
        catch (SocketException ex)
        {
            Log.Write($"Other Egrets can't find this one by themselves: {ex.SocketErrorCode}");
            return;
        }
        var udp = _udp;
        var stop = _stop.Token;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(stop);
                    if (!_settings.ShareWithEgrets || !HasCamera() || !IsLocalOrTailscale(r.RemoteEndPoint.Address)
                        || !r.Buffer.AsSpan().SequenceEqual(Peers.Question)) continue;
                    byte[] answer = JsonSerializer.SerializeToUtf8Bytes(new { id = _settings.DeviceId, name = Peers.ThisName, port = _settings.PhonePort, camera = true });
                    await udp.SendAsync(answer, r.RemoteEndPoint, stop);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { await Task.Delay(200, CancellationToken.None); } // Windows reports an earlier reply that went nowhere
            }
        });
    }

    /// <summary>Waits briefly for a frame newer than the last one, so the picture paces itself to the connection.</summary>
    private async Task<IResult> FrameAsync(string cam, long? after, HttpContext ctx)
    {
        if (Find(cam) is not { } camera) return Results.NotFound();
        lock (_lastFrameAsk) _lastFrameAsk[cam] = Environment.TickCount64;
        for (int i = 0; i < 50 && !ctx.RequestAborted.IsCancellationRequested; i++)
        {
            if (camera.LatestFrame() is { } frame && frame.No != after)
            {
                ctx.Response.Headers["X-Frame"] = frame.No.ToString();
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Bytes(frame.Jpeg, "image/jpeg");
            }
            await Task.Delay(20);
        }
        return Results.NoContent();
    }

    private IRemoteCamera? Find(string id) => _cameras.GetValueOrDefault(id);

    private IResult Do(string cam, Func<IRemoteCamera, string?> action)
    {
        if (Find(cam) is not { } camera) return Results.NotFound();
        string? problem = action(camera);
        return problem == null ? Results.Ok() : Results.Json(new { message = problem }, statusCode: 409);
    }

    public static bool SameSecret(string? given, string expected) =>
        given != null && expected.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    private static byte[] Embedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static bool IsLocalOrTailscale(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 100 && b[1] is >= 64 and <= 127); // Tailscale
        return (b[0] & 0xFE) == 0xFC || ip.IsIPv6LinkLocal; // includes Tailscale's fd7a:… range
    }

    /// <summary>The addresses a phone can use, labelled for the app's panel.</summary>
    public static List<string> Addresses(int port) =>
        OwnAddresses().Select(a => $"{(a.Tailscale ? "Tailscale" : "Home")}: http://{a.Ip}:{port}").ToList();

    /// <summary>This computer's home-network addresses, then its Tailscale ones.</summary>
    private static List<(bool Tailscale, IPAddress Ip)> OwnAddresses()
    {
        var found = new List<(bool Tailscale, IPAddress Ip)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var props = nic.GetIPProperties();
            bool tailscale = nic.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) || nic.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            if (!tailscale && !props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
            foreach (var a in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address)))
                found.Add((tailscale, a.Address));
        }
        return found.OrderBy(a => a.Tailscale).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp?.Dispose();
        _udp = null;
        if (_app == null) return;
        using var soon = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await _app.StopAsync(soon.Token);
        await _app.DisposeAsync();
        _app = null;
    }

    private sealed record LoginBody(string? Code);
    private sealed record PairBody(string? Id, string? Name);
    private sealed record SetBody(string Id, string Value);
    private sealed record RecordBody(bool On);
    private sealed record FocusBody(double X, double Y);
    private sealed record StepBody(int Step);
    private sealed record PresetBody(int Slot, bool Save);
    private sealed record ModeBody(bool Photo);
    private sealed record ShutterBody(bool Down);
}
