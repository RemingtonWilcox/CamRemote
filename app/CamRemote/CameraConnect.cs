namespace CamRemote;

/// <summary>The steps of connecting to a camera, shared by the dialog and auto-connect.</summary>
public static class CameraConnect
{
    public const string FingerprintWarning =
        "The camera's security fingerprint has changed since last time.\n\n" +
        "That's expected after a camera reset or a new Access Authen. setting. " +
        "Otherwise another device may be pretending to be your camera.\n\nConnect anyway?";

    /// <summary>
    /// Connects to the camera saved in settings (at startup and when reconnecting). If the router has
    /// given the camera a new IP, it's found again by its MAC address. Returns false if no connect
    /// was started (camera off or unreachable).
    /// </summary>
    public static async Task<bool> SavedAsync(CameraService cam, Settings settings, Action<string> say, Func<Task<bool>> askFingerprint, bool quiet = false)
    {
        if (!quiet) say($"Looking for the camera at {settings.CameraIp}…");
        // A camera that's off answers nothing at all. Finding that out takes a second, where Sony's SDK takes 20 s to
        // give up and a search takes several more. A search still runs now and then, in case the router moved the camera.
        bool there = await AnswersAsync(settings.CameraIp);
        if (there) _misses = 0;
        else if (++_misses == 1) Log.Write($"Nothing answers at {settings.CameraIp}, so the camera is off or away.");
        if (!there && quiet && _misses % SearchEvery != 0) return false;
        // A camera paired over SSH before left its fingerprint: go straight to its address, since a search waits
        // seconds for answers. Cameras paired another way (the A9 II's PC Remote pairing) are always searched for.
        if (there && settings.Fingerprint.Length > 0 && await cam.AddByIpAsync(settings.CameraIp, settings.CameraModel) == 0)
        {
            var took = System.Diagnostics.Stopwatch.StartNew();
            if (await cam.FingerprintAsync(-1) is { Length: > 0 } fingerprint)
            {
                Log.Write($"Reached the camera at its saved address in {took.Elapsed.TotalSeconds:0.0} s.");
                return await ConnectAsync(cam, settings, -1, fingerprint, settings.LoadPassword(), say, askFingerprint);
            }
            Log.Write($"The camera didn't answer at its saved address ({took.Elapsed.TotalSeconds:0.0} s), so searching.");
        }
        var searched = System.Diagnostics.Stopwatch.StartNew();
        var found = await cam.DiscoverAsync();
        if (found.Count > 0 || _misses <= SearchEvery) Log.Write($"Searched for cameras in {searched.Elapsed.TotalSeconds:0.0} s: found {found.Count}.");
        var picked = found.FirstOrDefault(c => c.Ip == settings.CameraIp)
                     ?? found.FirstOrDefault(c => settings.CameraMac.Length > 0 && c.Mac == settings.CameraMac && c.Ip.Length > 0);
        if (picked == null && !there) return false;
        // Not found by search but something answers there: still try the saved IP directly.
        if (picked != null && picked.Ip != settings.CameraIp)
        {
            say($"The camera moved to {picked.Ip}; updating the saved address.");
            settings.CameraIp = picked.Ip;
            settings.Save();
        }
        return await PickedAsync(cam, settings, picked, settings.LoadPassword(), say, askFingerprint);
    }

    private const int SearchEvery = 8; // tries while the camera doesn't answer
    private static int _misses;

    /// <summary>
    /// Whether anything answers at the camera's address within a second and a half: SSH for cameras paired that
    /// way, PTP/IP for the A9 II's PC Remote pairing. A refusal counts, since only a camera that's on can refuse.
    /// </summary>
    private static async Task<bool> AnswersAsync(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var address)) return true; // the usual steps say what's wrong with it
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        async Task<bool> Port(int port)
        {
            using var tcp = new System.Net.Sockets.TcpClient(address.AddressFamily);
            try
            {
                await tcp.ConnectAsync(address, port, cancel.Token);
                return true;
            }
            catch (System.Net.Sockets.SocketException e) { return e.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused; }
            catch (OperationCanceledException) { return false; }
        }
        var ssh = Port(22);
        var ptp = Port(15740);
        try { return await await Task.WhenAny(ssh, ptp) || await ssh || await ptp; }
        finally { cancel.Cancel(); } // the other port's try, if it's still going
    }

    /// <summary>
    /// Connects to the saved camera just after another RemoteCam let it go: tries again each second for a few
    /// seconds while the camera frees up. True once connected.
    /// </summary>
    public static async Task<bool> TakeOverAsync(CameraService cam, Settings settings, Action<string> say, Func<Task<bool>> askFingerprint)
    {
        for (int i = 0; ; i++)
        {
            if (await SavedAsync(cam, settings, say, askFingerprint, quiet: i > 0))
            {
                for (int wait = 0; wait < 150 && cam.State == LinkState.Connecting; wait++) await Task.Delay(100);
                if (cam.State == LinkState.Connected) return true;
            }
            if (i == 5 || cam.HoldReconnect) return false;
            await Task.Delay(1000);
        }
    }

    /// <summary>Connects to a found camera, or to the saved IP when <paramref name="picked"/> is null.</summary>
    public static async Task<bool> PickedAsync(CameraService cam, Settings settings, FoundCamera? picked,
        string password, Action<string> say, Func<Task<bool>> askFingerprint)
    {
        int index;
        bool ssh;
        if (picked != null)
        {
            index = picked.Index;
            ssh = picked.Ssh;
            if (picked.Mac.Length > 0) settings.CameraMac = picked.Mac;
            if (picked.Model.Length > 0) settings.CameraModel = picked.Model;
            settings.Save();
        }
        else
        {
            int err = await cam.AddByIpAsync(settings.CameraIp, settings.CameraModel);
            if (err != 0)
            {
                say($"\"{settings.CameraIp}\" isn't a usable IP address.");
                return false;
            }
            index = -1;
            ssh = true;
        }

        string fingerprint = "";
        if (ssh)
        {
            fingerprint = await cam.FingerprintAsync(index) ?? "";
            if (fingerprint.Length == 0)
            {
                say("Couldn't reach the camera to read its security fingerprint. Is it on, on Wi-Fi, with Remote Shooting on?");
                return false;
            }
        }
        return await ConnectAsync(cam, settings, index, fingerprint, password, say, askFingerprint);
    }

    /// <summary>Checks a fingerprint read from the camera (empty when it doesn't use SSH) and starts connecting.</summary>
    private static async Task<bool> ConnectAsync(CameraService cam, Settings settings, int index, string fingerprint,
        string password, Action<string> say, Func<Task<bool>> askFingerprint)
    {
        if (fingerprint.Length > 0)
        {
            if (settings.Fingerprint.Length > 0 && settings.Fingerprint != fingerprint)
            {
                if (!await askFingerprint())
                {
                    cam.HoldReconnect = true; // don't keep asking
                    return false;
                }
            }
            settings.Fingerprint = fingerprint;
            settings.Save();
        }

        say("Connecting…");
        cam.Model = settings.CameraModel;
        cam.Connect(index, settings.CameraUser, password, fingerprint);
        return true;
    }
}
