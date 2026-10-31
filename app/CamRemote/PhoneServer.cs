using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
}

/// <summary>
/// Serves the phone control page. Only home-network and Tailscale addresses are answered, and everything
/// but the page itself needs the access code shown in the app.
/// </summary>
public sealed class PhoneServer : IAsyncDisposable
{
    private const string Cookie = "camremote";
    private readonly Settings _settings;
    private readonly Dictionary<string, IRemoteCamera> _cameras;
    private readonly Dictionary<string, long> _lastFrameAsk = new();
    private WebApplication? _app;

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
            if (path.StartsWith("/api/") && path != "/api/login")
            {
                if (!SameSecret(ctx.Request.Cookies[Cookie], _settings.PhoneToken)) { ctx.Response.StatusCode = 401; return; }
                // Browsers won't add this header on another site's behalf.
                if (ctx.Request.Method != "GET" && !ctx.Request.Headers.ContainsKey("X-CamRemote")) { ctx.Response.StatusCode = 403; return; }
            }
            await next();
        });

        app.MapGet("/", () => Results.Bytes(Embedded("phone.html"), "text/html; charset=utf-8"));
        app.MapGet("/icon.png", () => Results.Bytes(Embedded("phone-icon.png"), "image/png"));
        app.MapGet("/manifest.webmanifest", () => Results.Json(new
        {
            name = "CamRemote", short_name = "CamRemote", start_url = "/", display = "standalone",
            background_color = "#101214", theme_color = "#101214",
            icons = new[] { new { src = "/icon.png", sizes = "180x180", type = "image/png" } },
        }));

        app.MapPost("/api/login", async (LoginBody body, HttpContext ctx) =>
        {
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

        app.MapGet("/api/cameras", () => Results.Json(_cameras.Values.Select(c => new { id = c.Id, name = c.Name })));
        app.MapGet("/api/{cam}/state", (string cam) => Find(cam) is { } c ? Results.Json(c.State()) : Results.NotFound());
        app.MapGet("/api/{cam}/frame", FrameAsync);
        app.MapPost("/api/{cam}/set", (string cam, SetBody b) => Do(cam, c => c.Set(b.Id, b.Value)));
        app.MapPost("/api/{cam}/record", (string cam, RecordBody b) => Do(cam, c => c.Record(b.On)));
        app.MapPost("/api/{cam}/focus", (string cam, FocusBody b) => Do(cam, c => c.Focus(b.X, b.Y)));
        app.MapPost("/api/{cam}/focusstep", (string cam, StepBody b) => Do(cam, c => c.FocusStep(b.Step)));

        await app.StartAsync();
        _app = app;
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

    private static bool SameSecret(string? given, string expected) =>
        given != null && expected.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    private static byte[] Embedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool IsLocalOrTailscale(IPAddress ip)
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
    public static List<string> Addresses(int port)
    {
        var found = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var props = nic.GetIPProperties();
            bool tailscale = nic.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) || nic.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            if (!tailscale && !props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
            foreach (var a in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address)))
                found.Add($"{(tailscale ? "Tailscale" : "Home")}: http://{a.Address}:{port}");
        }
        return found.OrderBy(s => s.StartsWith("Tailscale")).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app == null) return;
        using var soon = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await _app.StopAsync(soon.Token);
        await _app.DisposeAsync();
        _app = null;
    }

    private sealed record LoginBody(string? Code);
    private sealed record SetBody(string Id, string Value);
    private sealed record RecordBody(bool On);
    private sealed record FocusBody(double X, double Y);
    private sealed record StepBody(int Step);
}
