using System.IO;
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

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamRemote", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new(); }
        catch { return new(); }
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
