namespace CamRemote;

/// <summary>The steps of connecting to a camera, shared by the dialog and auto-connect.</summary>
public static class CameraConnect
{
    public const string FingerprintWarning =
        "The camera's security fingerprint has changed since last time.\n\n" +
        "That's expected after a camera reset or a new Access Authen. setting. " +
        "Otherwise another device may be pretending to be your camera.\n\nConnect anyway?";

    /// <summary>
    /// Connects to the camera saved in settings (at startup and when reconnecting). The camera is known by
    /// its MAC address, so if the router has given it a new IP it's found again there, and a different camera
    /// that has taken over its old IP isn't mistaken for it. (The SSH fingerprint is checked as well, below.)
    /// Returns false if no connect was started (camera off or unreachable).
    /// </summary>
    public static async Task<bool> SavedAsync(CameraService cam, Settings settings, Action<string> say, Func<Task<bool>> askFingerprint, bool quiet = false)
    {
        if (!quiet) say($"Looking for the camera at {settings.CameraIp}…");
        var found = await cam.DiscoverAsync();
        bool knowMac = settings.CameraMac.Length > 0;
        var mine = knowMac ? found.FirstOrDefault(c => c.Ip.Length > 0 && SameMac(c.Mac, settings.CameraMac)) : null;
        var atSavedIp = found.FirstOrDefault(c => c.Ip == settings.CameraIp);
        if (mine == null && atSavedIp != null && knowMac && atSavedIp.Mac.Length > 0)
        {
            // Our camera isn't on the network, and someone else's has its old address.
            if (!quiet) say($"Another camera is using {settings.CameraIp} now. Still looking for yours…");
            Log.Write($"Skipped {atSavedIp.Model} at {atSavedIp.Ip}: MAC {atSavedIp.Mac} isn't the saved {settings.CameraMac}.");
            return false;
        }
        var picked = mine ?? atSavedIp;
        // Not found by search: still try the saved IP directly; reading the fingerprint fails fast if it's absent.
        if (picked != null && picked.Ip != settings.CameraIp)
        {
            say($"The camera moved to {picked.Ip}; updating the saved address.");
            settings.CameraIp = picked.Ip;
            settings.Save();
        }
        return await PickedAsync(cam, settings, picked, settings.LoadPassword(), say, askFingerprint);
    }

    /// <summary>Compares MAC addresses written either way: AA:BB:CC… or aa-bb-cc….</summary>
    public static bool SameMac(string a, string b)
    {
        static string Norm(string m) => new(m.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        string na = Norm(a);
        return na.Length > 0 && na == Norm(b);
    }

    /// <summary>
    /// True for four numbers 0-255 with dots between, as the camera shows under Display Wi-Fi Info.
    /// Sony's bridge reads the address loosely: "192.168.1.300" would quietly become 192.168.1.44.
    /// </summary>
    public static bool IsIPv4(string text)
    {
        string[] parts = text.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p) <= 255);
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
            if (!IsIPv4(settings.CameraIp))
            {
                say($"\"{settings.CameraIp}\" isn't a usable IP address. It looks like 192.168.1.23.");
                return false;
            }
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
        cam.Connect(index, settings.CameraUser, password, fingerprint);
        return true;
    }
}
