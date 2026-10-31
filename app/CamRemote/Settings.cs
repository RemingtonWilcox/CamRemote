using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Security.Credentials;

namespace CamRemote;

/// <summary>App settings in %APPDATA%\CamRemote\settings.json. The camera password lives in Windows Credential Manager.</summary>
public sealed class Settings
{
    private const string VaultResource = "CamRemote camera";

    public string VideoDeviceId { get; set; } = "";
    public string CameraIp { get; set; } = "";
    public string CameraMac { get; set; } = ""; // finds the camera again if the router gives it a new IP
    public string CameraUser { get; set; } = "admin";
    public string Fingerprint { get; set; } = "";
    public bool AutoConnect { get; set; } = true;
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

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamRemote", "settings.json");

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

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

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
}
