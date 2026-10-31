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
        var found = await cam.DiscoverAsync();
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
                say("Couldn't reach the camera to read its security fingerprint. Is it on, on Wi-Fi, with PC Remote on?");
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
