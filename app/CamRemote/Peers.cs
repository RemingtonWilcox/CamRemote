using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CamRemote;

/// <summary>
/// Another CamRemote found holding a camera. Token is set for the one joined by access code before.
/// Tailnet is its Tailscale address, where asking to join lets it see this is one of the same person's devices.
/// </summary>
public sealed record FoundPeer(string Id, string Name, string Address, string? Token = null, string? Tailnet = null);

/// <summary>
/// Lets one CamRemote use the camera another already holds, with nothing to type. A host answers a
/// "who has the camera?" broadcast on its phone port (UDP). A joiner it hasn't seen asks to join
/// (/api/pair), the host's person allows it or not, and the host hands that device its own token.
/// Both sides keep the token in Windows Credential Manager or the macOS Keychain. Broadcasts don't
/// cross Tailscale, so a joiner also tries the addresses of hosts it has joined before.
/// </summary>
public static class Peers
{
    public const string AllowedResource = "CamRemote allowed"; // tokens this CamRemote handed out, by device id
    public const string HostResource = "CamRemote host"; // tokens other CamRemotes handed this one, by their device id
    public static readonly byte[] Question = "camremote-who-has-the-camera-1"u8.ToArray();
    private const int KeepHosts = 8, KeepAddresses = 6;

    public static string ThisName => Clean(Environment.MachineName);

    /// <summary>A name sent by another device, made safe to show.</summary>
    public static string Clean(string? name)
    {
        string s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? "another computer" : s.Length > 40 ? s[..40] : s;
    }

    /// <summary>
    /// Finds another CamRemote holding a camera and gets this one in: with its token from last time, or by
    /// asking (once a run per host, in <paramref name="asked"/>). Null when there's none or it said no.
    /// </summary>
    public static async Task<RemoteLink?> JoinAsync(Settings settings, Action<string> say, HashSet<string> asked, bool patient = false)
    {
        foreach (var host in await FindAsync(settings, patient))
        {
            string token = host.Token ?? settings.LoadSecret(HostResource, host.Id);
            if (token.Length > 0)
            {
                var (link, problem) = await RemoteLink.ResumeAsync(host.Address, token);
                if (link != null) return await JoinedAsync(settings, link, host);
                if (problem != RemoteLink.SignedOut || host.Token != null) continue;
                settings.SaveSecret(HostResource, host.Id, ""); // that host no longer lets this one in
            }
            if (host.Token != null || !asked.Add(host.Id)) continue;
            say($"The RemoteCam on {host.Name} has the camera. Asking it to let this computer join: answer on {host.Name}.");
            var (given, why, unanswered) = await AskToJoinAsync(settings, host);
            if (unanswered) asked.Remove(host.Id); // the question is still up there, so the next try waits on it again
            if (given == null)
            {
                say(why!);
                continue;
            }
            settings.SaveSecret(HostResource, host.Id, given);
            var (joined, notJoined) = await RemoteLink.ResumeAsync(host.Address, given);
            if (joined != null) return await JoinedAsync(settings, joined, host);
            say(notJoined!);
        }
        return null;
    }

    private static async Task<RemoteLink> JoinedAsync(Settings settings, RemoteLink link, FoundPeer host)
    {
        link.HostName = host.Name;
        link.HostId = host.Id;
        if (host.Token != null) return link; // joined by access code; ConnectWindow keeps that one
        using var http = Client(TimeSpan.FromSeconds(3));
        var hello = await HelloAsync(http, host.Address, link.Token);
        Remember(settings, host.Id, host.Name, new[] { host.Address }.Concat(hello?.Addresses ?? []));
        return link;
    }

    /// <summary>Keeps a host's addresses, newest first, so it's found again where broadcasts don't reach.</summary>
    public static void Remember(Settings settings, string id, string name, IEnumerable<string> addresses)
    {
        var known = settings.KnownHosts.FirstOrDefault(h => h.Id == id) ?? new PeerDevice { Id = id };
        settings.KnownHosts.Remove(known);
        known.Name = name;
        known.Addresses = addresses.Concat(known.Addresses).Distinct().Take(KeepAddresses).ToList();
        settings.KnownHosts.Insert(0, known);
        if (settings.KnownHosts.Count > KeepHosts) settings.KnownHosts.RemoveRange(KeepHosts, settings.KnownHosts.Count - KeepHosts);
        settings.Save();
    }

    private static int _lastFound = -1;

    /// <summary>Every CamRemote within reach that holds a camera: by broadcast at home, and at remembered addresses.</summary>
    public static async Task<List<FoundPeer>> FindAsync(Settings settings, bool patient = false)
    {
        var found = new ConcurrentDictionary<string, FoundPeer>();
        var known = settings.KnownHosts.SelectMany(h => h.Addresses.Select(a => (h.Id, a))).ToList();
        string last = settings.JoinAddress;
        string lastToken = last.Length > 0 ? LastJoinToken(settings) : "";
        var took = System.Diagnostics.Stopwatch.StartNew();
        // Remembered addresses get 1.5 s, or 3 s when one is surely there (the camera said it's busy), since Tailscale can be slow to answer first.
        var wait = TimeSpan.FromSeconds(patient ? 3 : 1.5);
        var others = Task.WhenAll(TryKnownAsync(known, found, wait), lastToken.Length > 0 ? TryLastAsync(last, lastToken, found, wait) : Task.CompletedTask);
        await AskAroundAsync(settings.DeviceId, settings.PhonePort, found);
        // One found at home is enough; remembered addresses can take seconds to time out.
        if (found.IsEmpty) await others;
        // Only when the count changes, since this runs every few seconds while the camera is off.
        if ((known.Count > 0 || !found.IsEmpty) && found.Count != _lastFound)
            Log.Write($"Looked for other RemoteCams in {took.Elapsed.TotalSeconds:0.0} s: found {found.Count}.");
        _lastFound = found.Count;
        // A host found by its id beats the same one joined by code before.
        return found.Values.OrderBy(p => p.Token != null).ToList();
    }

    // Read once a run: a Keychain read can ask the person, and this runs every few seconds while waiting.
    private static (string Address, string Token)? _lastJoin;

    private static string LastJoinToken(Settings settings)
    {
        if (_lastJoin is not { } j || j.Address != settings.JoinAddress) _lastJoin = j = (settings.JoinAddress, settings.LoadJoinToken());
        return j.Token;
    }

    private static async Task AskAroundAsync(string ownId, int port, ConcurrentDictionary<string, FoundPeer> found)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            foreach (var to in BroadcastAddresses())
                try { await udp.SendAsync(Question, new IPEndPoint(to, port)); }
                catch (SocketException) { /* that network can't broadcast */ }
            // Answers at home come back within a few hundredths of a second. One missed here isn't lost: the
            // camera then says it's busy, and that looks again.
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(0.8));
            while (true)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(wait.Token); }
                catch (SocketException) { continue; } // Windows reports an earlier send that went nowhere
                var from = r.RemoteEndPoint.Address;
                if (!PhoneServer.IsLocalOrTailscale(from)) continue;
                try
                {
                    var root = JsonDocument.Parse(r.Buffer).RootElement;
                    string id = root.GetProperty("id").GetString() ?? "";
                    if (id.Length == 0 || id == ownId || !root.GetProperty("camera").GetBoolean()) continue;
                    int hostPort = root.GetProperty("port").GetInt32();
                    if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
                    string? tail = root.TryGetProperty("tailnet", out var t) && IPAddress.TryParse(t.GetString(), out var ip) && Tailnet.Contains(ip)
                        ? $"http://{ip}:{hostPort}" : null;
                    found.TryAdd(id, new FoundPeer(id, Clean(root.GetProperty("name").GetString()), $"http://{from}:{hostPort}", Tailnet: tail));
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { /* not one of ours */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex) { Log.Write("Couldn't look for other RemoteCams on the network: " + ex.SocketErrorCode); }
    }

    /// <summary>The whole-network address, plus each home network's own, since Windows sends the first on one network only.</summary>
    private static HashSet<IPAddress> BroadcastAddresses()
    {
        var all = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var u in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    byte[] a = u.Address.GetAddressBytes(), m = u.IPv4Mask.GetAddressBytes();
                    if (IPAddress.IsLoopback(u.Address) || !PhoneServer.IsLocalOrTailscale(u.Address) || m.All(b => b == 255)) continue; // Tailscale is one address wide
                    all.Add(new IPAddress(a.Zip(m, (x, y) => (byte)(x | ~y)).ToArray()));
                }
        }
        catch (NetworkInformationException) { /* the whole-network one still goes */ }
        return all;
    }

    private static async Task TryKnownAsync(List<(string Id, string Address)> known, ConcurrentDictionary<string, FoundPeer> found, TimeSpan wait)
    {
        if (known.Count == 0) return;
        using var http = Client(wait);
        await Task.WhenAll(known.Select(async k =>
        {
            // The same address may belong to another device by now, so the id has to match.
            if (await HelloAsync(http, k.Address, null) is { Camera: true } hello && hello.Id == k.Id)
                found.TryAdd(k.Id, new FoundPeer(k.Id, hello.Name, k.Address));
        }));
    }

    /// <summary>The CamRemote last joined with its access code, like the iPhone app, which doesn't answer broadcasts.</summary>
    private static async Task TryLastAsync(string address, string token, ConcurrentDictionary<string, FoundPeer> found, TimeSpan wait)
    {
        using var http = Client(wait);
        try
        {
            using var cameras = await http.SendAsync(Get(address + "/api/cameras", token));
            if (!cameras.IsSuccessStatusCode) return;
            var first = JsonDocument.Parse(await cameras.Content.ReadAsStringAsync()).RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object || first.GetProperty("id").GetString() is not { Length: > 0 } cam) return;
            using var state = await http.SendAsync(Get($"{address}/api/{cam}/state", token));
            if (!state.IsSuccessStatusCode) return;
            if (JsonDocument.Parse(await state.Content.ReadAsStringAsync()).RootElement.GetProperty("link").GetString() != "connected") return;
            found.TryAdd("last:" + address, new FoundPeer("", new Uri(address).Host, address, token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    private sealed record Hello(string Id, string Name, bool Camera, List<string> Addresses);

    /// <summary>Who answers at an address. With this computer's token, it also lists its other addresses.</summary>
    private static async Task<Hello?> HelloAsync(HttpClient http, string address, string? token)
    {
        try
        {
            using var r = await http.SendAsync(Get(address + "/api/hello", token));
            if (!r.IsSuccessStatusCode) return null;
            var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            if (root.GetProperty("app").GetString() != "egret") return null; // the tag every copy answers with (see PhoneServer)
            var addresses = root.TryGetProperty("addresses", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => RemoteLink.Normalize(x.GetString() ?? "")).OfType<string>().ToList()
                : [];
            return new Hello(root.GetProperty("id").GetString() ?? "", Clean(root.GetProperty("name").GetString()),
                root.GetProperty("camera").GetBoolean(), addresses);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the host to let this computer in. Its person answers there, so this waits up to two minutes.
    /// Returns the token, or why not, and whether the question is still waiting for an answer. Over Tailscale
    /// when both have it, so the host can see this is one of the same person's devices and let it straight in.
    /// </summary>
    private static async Task<(string? Token, string? Problem, bool Unanswered)> AskToJoinAsync(Settings settings, FoundPeer host)
    {
        using var http = Client(TimeSpan.FromMinutes(2));
        try
        {
            HttpResponseMessage r;
            if (host.Tailnet != null && Tailnet.OwnAddress() != null)
                try { r = await http.SendAsync(PairRequest(settings, host.Tailnet)); }
                catch (HttpRequestException) { r = await http.SendAsync(PairRequest(settings, host.Address)); } // Tailscale is off on one side
            else r = await http.SendAsync(PairRequest(settings, host.Address));
            using var _ = r;
            if (r.IsSuccessStatusCode)
            {
                string token = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString() ?? "";
                if (token.Length > 0) return (token, null, false);
            }
            int status = (int)r.StatusCode;
            return (null, status switch
            {
                403 => $"{host.Name} didn't let this computer join.",
                408 => Waiting(host),
                409 => $"{host.Name} is asking about another device. To try again, click Connect camera… here.",
                429 => $"{host.Name} said no a moment ago. Try again in a few minutes.",
                404 => $"The RemoteCam on {host.Name} is older and can't let this computer in by itself. Update it, or join with its access code.",
                _ => $"{host.Name} couldn't take this computer ({status}).",
            }, status == 408);
        }
        catch (TaskCanceledException) { return (null, Waiting(host), true); }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, $"Lost touch with {host.Name} while asking to join.", false);
        }
    }

    private static HttpRequestMessage PairRequest(Settings settings, string address)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, address + "/api/pair")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = settings.DeviceId, name = ThisName }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-CamRemote", "1");
        return req;
    }

    private static string Waiting(FoundPeer host) => $"Still waiting for an answer on {host.Name}. This computer joins as soon as you allow it there.";

    private static HttpClient Client(TimeSpan timeout) =>
        new(new SocketsHttpHandler { UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(Math.Min(6, timeout.TotalSeconds)) }) { Timeout = timeout };

    private static HttpRequestMessage Get(string url, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token)) req.Headers.Add("Cookie", $"{PhoneServer.Cookie}={token}");
        return req;
    }
}

/// <summary>The other CamRemotes allowed to use this one's camera. Their tokens are read from the vault once, at startup.</summary>
public sealed class PeerAllowList
{
    private readonly Settings _settings;
    private readonly Dictionary<string, string> _tokens = []; // device id to token

    public PeerAllowList(Settings settings)
    {
        _settings = settings;
        foreach (var d in settings.AllowedDevices)
            if (settings.LoadSecret(Peers.AllowedResource, d.Id) is { Length: > 0 } token) _tokens[d.Id] = token;
    }

    /// <summary>The device this token was handed to, if it's still allowed. Any thread.</summary>
    public string? DeviceFor(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_tokens)
            foreach (var (id, t) in _tokens)
                if (PhoneServer.SameSecret(token, t)) return id;
        return null;
    }

    /// <summary>Lets a device in for good, until it's removed in Setup. Returns its new token. On the UI thread.</summary>
    public string Allow(string id, string name)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _settings.SaveSecret(Peers.AllowedResource, id, token);
        _settings.AllowedDevices.RemoveAll(d => d.Id == id);
        _settings.AllowedDevices.Add(new PeerDevice { Id = id, Name = name });
        _settings.Save();
        lock (_tokens) _tokens[id] = token;
        Log.Write($"Let {name} join this camera.");
        return token;
    }

    /// <summary>Signs a device out at once. It has to ask again to come back. On the UI thread.</summary>
    public void Remove(string id)
    {
        lock (_tokens) _tokens.Remove(id);
        _settings.SaveSecret(Peers.AllowedResource, id, "");
        _settings.AllowedDevices.RemoveAll(d => d.Id == id);
        _settings.Save();
    }
}

/// <summary>
/// What this computer's Tailscale knows: its own address, and whether another address belongs to the same
/// person's account. Tailscale proves who each device is, so those join without being asked about.
/// </summary>
public static class Tailnet
{
    private static readonly string[] Cli = OperatingSystem.IsWindows()
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")]
        : ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/opt/homebrew/bin/tailscale", "/usr/local/bin/tailscale"];

    /// <summary>A Tailscale address: 100.64.0.0/10 or fd7a:115c:a1e0::/48.</summary>
    public static bool Contains(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 100 && b[1] is >= 64 and <= 127
            : b.Length == 16 && b[0] == 0xFD && b[1] == 0x7A && b[2] == 0x11 && b[3] == 0x5C && b[4] == 0xA1 && b[5] == 0xE0;
    }

    /// <summary>This computer's Tailscale address, or null when Tailscale is off.</summary>
    public static IPAddress? OwnAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && Contains(a));
        }
        catch (NetworkInformationException) { return null; }
    }

    /// <summary>
    /// True when <paramref name="from"/> is a device signed in to Tailscale as the same person as this one. Shared
    /// and tagged devices, like Funnel's, belong to someone else as far as Tailscale says, so they're still asked about.
    /// </summary>
    public static async Task<bool> SamePersonAsync(IPAddress from)
    {
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        if (!Contains(from) || Cli.FirstOrDefault(File.Exists) is not { } cli) return false;
        using var me = await RunAsync(cli, "status", "--json", "--peers=false");
        using var them = await RunAsync(cli, "whois", "--json", from.ToString());
        if (me == null || them == null) return false;
        try
        {
            long mine = me.RootElement.GetProperty("Self").GetProperty("UserID").GetInt64();
            long theirs = them.RootElement.GetProperty("UserProfile").GetProperty("ID").GetInt64();
            return mine != 0 && mine == theirs;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }

    private static async Task<JsonDocument?> RunAsync(string cli, params string[] args)
    {
        var start = new ProcessStartInfo(cli) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) start.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(start);
            if (p == null) return null;
            using var soon = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string json = await p.StandardOutput.ReadToEndAsync(soon.Token);
            await p.WaitForExitAsync(soon.Token);
            return p.ExitCode == 0 ? JsonDocument.Parse(json) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
