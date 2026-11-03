using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
#if WINDOWS
using Windows.Security.Credentials;
#endif

namespace CamRemote;

/// <summary>A C1 to C4 slot: the camera's settings when it was saved. They live in the app, so the camera's own C1 to C3 stay as they are.</summary>
public sealed class Preset
{
    public string Summary { get; set; } = "";
    public Dictionary<string, long> Values { get; set; } = new(); // PropCode name to value
}

/// <summary>App settings in settings.json in <see cref="Folder"/>. The camera password lives in Windows Credential Manager or the macOS Keychain.</summary>
public sealed class Settings
{
    private const string VaultResource = "CamRemote camera";
    private const string JoinResource = "CamRemote join";

    public string VideoDeviceId { get; set; } = "";
    public string CameraIp { get; set; } = "";
    public string CameraMac { get; set; } = ""; // finds the camera again if the router gives it a new IP
    public string CameraModel { get; set; } = "ILCE-7SM3"; // as the camera reports it
    public string CameraUser { get; set; } = "admin";
    public string Fingerprint { get; set; } = "";
    public bool AutoConnect { get; set; } = true;
    public int AgreedVersion { get; set; } // the Agreement.Version last agreed to
    public int Rotation { get; set; } // degrees clockwise: 0, 90, 180 or 270, for a camera mounted on its side
    public bool ShowHistogram { get; set; } = true;
    public bool ShowZebras { get; set; }
    public bool ShowPeaking { get; set; }
    public bool ShowGrid { get; set; }
    public int ZebraLevel { get; set; } = 90; // percent; S-Log3 runs out of range a little above this
    public int PeakSensitivity { get; set; } = 1; // 0 low, 1 medium, 2 high
    public uint PeakColor { get; set; } = 0xFF3030;
    public DateTime? RecordingSince { get; set; } // UTC start of the take in progress
    public bool RecordingJoined { get; set; } // that take was already rolling, so the real start is earlier
    public bool PhoneEnabled { get; set; } // serve the phone control page
    public int PhonePort { get; set; } = 8787;
    public string PhoneCode { get; set; } = ""; // typed once on a phone
    public string PhoneToken { get; set; } = ""; // what the phone keeps afterwards
    public string JoinAddress { get; set; } = ""; // another CamRemote this one last used a camera through
    public string PhotoFolder { get; set; } = ""; // blank means Pictures/CamRemote
    public string RecordSubject { get; set; } = "Animal"; // what automatic recording looks for
    public int RecordStopAfter { get; set; } = 15; // seconds with nothing seen before automatic recording stops
    public bool RecordFocus { get; set; } = true; // automatic recording focuses on what it sees
    public double HudWidth { get; set; } = 280; // the histogram and meter over the picture
    public double HudX { get; set; } // how far it was dragged left of the picture's bottom-right corner (0 or less)
    public double HudY { get; set; } // and up from it
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } // 0 until the window has been closed once
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public int LongExposureSeconds { get; set; } = 30;
    public int NdStops { get; set; }
    public Dictionary<string, Preset> Presets { get; set; } = new(); // "video1" to "video4" and "photo1" to "photo4"

    /// <summary>%APPDATA%\CamRemote on Windows, ~/Library/Application Support/CamRemote on a Mac.</summary>
    public static string Folder => System.IO.Path.Combine(OperatingSystem.IsMacOS()
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamRemote");

    private static string Path => System.IO.Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Makes the phone code and token the first time phone control is turned on.</summary>
    public void EnsurePhoneSecrets()
    {
        if (PhoneCode.Length == 0) PhoneCode = RandomNumberGenerator.GetString("ABCDEFGHJKMNPQRSTUVWXYZ23456789", 8);
        if (PhoneToken.Length == 0) PhoneToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Replaces the code and token. Phones already signed in have to enter the new code.</summary>
    public void NewPhoneSecrets()
    {
        PhoneCode = "";
        PhoneToken = "";
        EnsurePhoneSecrets();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

#if WINDOWS
    public string LoadPassword()
    {
        try { var c = new PasswordVault().Retrieve(VaultResource, CameraUser); c.RetrievePassword(); return c.Password; }
        catch { return ""; }
    }

    public void SavePassword(string password)
    {
        var vault = new PasswordVault();
        try { foreach (var c in vault.FindAllByResource(VaultResource)) vault.Remove(c); } catch { /* none saved */ }
        if (password.Length > 0) vault.Add(new PasswordCredential(VaultResource, CameraUser, password));
    }

    /// <summary>What another CamRemote handed this one after its access code was typed.</summary>
    public string LoadJoinToken()
    {
        try { var c = new PasswordVault().Retrieve(JoinResource, JoinAddress); c.RetrievePassword(); return c.Password; }
        catch { return ""; }
    }

    public void SaveJoinToken(string token)
    {
        var vault = new PasswordVault();
        try { foreach (var c in vault.FindAllByResource(JoinResource)) vault.Remove(c); } catch { /* none saved */ }
        if (token.Length > 0) vault.Add(new PasswordCredential(JoinResource, JoinAddress, token));
    }
#else
    public string LoadPassword() => MacNative.LoadSecret(VaultResource, CameraUser);

    public void SavePassword(string password) => MacNative.SaveSecret(VaultResource, CameraUser, password);

    public string LoadJoinToken() => MacNative.LoadSecret(JoinResource, JoinAddress);

    public void SaveJoinToken(string token) => MacNative.SaveSecret(JoinResource, JoinAddress, token);
#endif
}
