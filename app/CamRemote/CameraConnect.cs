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
        // A camera paired over SSH before left its fingerprint: go straight to its address, since a search waits
        // seconds for answers. Cameras paired another way (the A9 II's PC Remote pairing) are always searched for.
        if (settings.Fingerprint.Length > 0 && await cam.AddByIpAsync(settings.CameraIp, settings.CameraModel) == 0)
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
        Log.Write($"Searched for cameras in {searched.Elapsed.TotalSeconds:0.0} s: found {found.Count}.");
        var picked = found.FirstOrDefault(c => c.Ip == settings.CameraIp)
                     ?? found.FirstOrDefault(c => settings.CameraMac.Length > 0 && c.Mac == settings.CameraMac && c.Ip.Length > 0);
        // Not found by search: still try the saved IP directly; reading the fingerprint fails fast if it's absent.
        if (picked != null && picked.Ip != settings.CameraIp)
        {
            say($"The camera moved to {picked.Ip}; updating the saved address.");
            settings.CameraIp = picked.Ip;
            settings.Save();
        }
        return await PickedAsync(cam, settings, picked, settings.LoadPassword(), say, askFingerprint);
    }

    /// <summary>
    /// Connects to the saved camera just after another Egret let it go: tries again each second for a few
    /// seconds while the camera frees up, rather than every 15 s. True once connected.
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
