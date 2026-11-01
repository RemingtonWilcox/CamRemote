using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Xunit;

namespace CamRemote.Tests;

public sealed class PhoneServerTests : IAsyncLifetime
{
    private sealed class StubCamera : IRemoteCamera
    {
        public readonly List<string> Calls = new();
        public string Id => "sony";
        public string Name => "A7S III";
        public object State() => new { link = "connected" };
        public (byte[] Jpeg, long No)? LatestFrame() => ([0xFF, 0xD8, 0xFF, 0xD9], 7);
        public string? Set(string id, string value) { Calls.Add($"set {id} {value}"); return id == "bad" ? "nope" : null; }
        public string? Record(bool on) { Calls.Add($"record {on}"); return null; }
        public string? Focus(double x, double y) { Calls.Add($"focus {x} {y}"); return null; }
        public string? FocusStep(int step) { Calls.Add($"step {step}"); return null; }
    }

    private readonly StubCamera _camera = new();
    private readonly Settings _settings = new();
    private PhoneServer _server = null!;
    private HttpClient _http = null!;
    private CookieContainer _cookies = new();

    public async Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _settings.PhonePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _settings.EnsurePhoneSecrets();
        _server = new PhoneServer(_settings, [_camera]);
        await _server.StartAsync();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = new Uri($"http://127.0.0.1:{_settings.PhonePort}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private Task<HttpResponseMessage> Login(string code) => _http.PostAsJsonAsync("api/login", new { code });

    private HttpRequestMessage Post(string path, object body, bool header = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (header) req.Headers.Add("X-CamRemote", "1");
        return req;
    }

    [Fact]
    public async Task The_page_itself_needs_no_code()
    {
        var r = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("text/html", r.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Everything_else_needs_the_code_first()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("api/cameras")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Post("api/sony/record", new { on = true }))).StatusCode);
        Assert.Empty(_camera.Calls);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_a_right_one_in_any_case_lets_you_in()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login("WRONGONE")).StatusCode);
        var ok = await Login(" " + _settings.PhoneCode.ToLowerInvariant() + " ");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("api/cameras")).StatusCode);
    }

    [Fact]
    public async Task Changes_need_the_custom_header_so_other_sites_cannot_send_them()
    {
        await Login(_settings.PhoneCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Post("api/sony/record", new { on = true }, header: false))).StatusCode);
        Assert.Empty(_camera.Calls);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Post("api/sony/record", new { on = true }))).StatusCode);
        Assert.Equal(["record True"], _camera.Calls);
    }

    [Fact]
    public async Task A_problem_comes_back_as_a_message()
    {
        await Login(_settings.PhoneCode);
        var r = await _http.SendAsync(Post("api/sony/set", new { id = "bad", value = "1" }));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("nope", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Post("api/canon/record", new { on = true }))).StatusCode);
    }

    [Fact]
    public async Task Frames_carry_their_number_and_wait_for_a_newer_one()
    {
        await Login(_settings.PhoneCode);
        var r = await _http.GetAsync("api/sony/frame");
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("7", Assert.Single(r.Headers.GetValues("X-Frame")));
        Assert.True(_server.Watching("sony"));
        var same = await _http.GetAsync("api/sony/frame?after=7");
        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("100.64.0.1", true)] // Tailscale
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Only_home_network_and_tailscale_addresses_are_answered(string ip, bool allowed) =>
        Assert.Equal(allowed, PhoneServer.IsLocalOrTailscale(IPAddress.Parse(ip)));
}
