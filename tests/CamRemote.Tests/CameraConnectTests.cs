using Xunit;

namespace CamRemote.Tests;

[Collection("settings file")] // these save settings.json, so they take turns
public class CameraConnectTests
{
    private static Settings Saved() => new() { CameraIp = "192.168.1.50", CameraMac = "AA:BB", CameraUser = "user1", Fingerprint = "SHA256:fake" };

    private const string Found = "[{\"index\":0,\"model\":\"ILCE-7SM3\",\"conn\":\"IP\",\"ip\":\"192.168.1.77\",\"mac\":\"AA:BB\",\"ssh\":true}]";

    [Fact]
    public async Task A_camera_with_a_new_address_is_found_by_its_mac()
    {
        using var rig = new Rig();
        rig.Camera.EnumerateJson = Found;
        var settings = Saved();
        var said = new List<string>();
        Assert.True(await CameraConnect.SavedAsync(rig.Cam, settings, said.Add, () => Task.FromResult(true)));
        Assert.Equal("192.168.1.77", settings.CameraIp);
        Assert.Contains(said, s => s.Contains("moved to 192.168.1.77"));
        rig.Settle();
        Assert.Contains("Connect 0 user1 SHA256:fake", rig.Camera.Calls);
    }

    [Fact]
    public async Task A_camera_search_misses_is_still_tried_at_its_saved_address()
    {
        using var rig = new Rig();
        var settings = Saved();
        Assert.True(await CameraConnect.SavedAsync(rig.Cam, settings, _ => { }, () => Task.FromResult(true)));
        rig.Settle();
        Assert.Contains("MakeIpCamera 192.168.1.50 ILCE-7SM3", rig.Camera.Calls);
        Assert.Contains("Connect -1 user1 SHA256:fake", rig.Camera.Calls);
    }

    [Fact]
    public async Task A_changed_fingerprint_that_is_declined_stops_and_holds_reconnecting()
    {
        using var rig = new Rig();
        rig.Camera.FingerprintText = "SHA256:someone-else";
        var settings = Saved();
        bool asked = false;
        Assert.False(await CameraConnect.SavedAsync(rig.Cam, settings, _ => { }, () => { asked = true; return Task.FromResult(false); }));
        Assert.True(asked);
        Assert.True(rig.Cam.HoldReconnect);
        Assert.Equal("SHA256:fake", settings.Fingerprint); // the trusted one is kept
        rig.Settle();
        Assert.DoesNotContain(rig.Camera.Calls, c => c.StartsWith("Connect"));
    }

    [Fact]
    public async Task A_changed_fingerprint_that_is_accepted_is_remembered()
    {
        using var rig = new Rig();
        rig.Camera.FingerprintText = "SHA256:new";
        var settings = Saved();
        Assert.True(await CameraConnect.SavedAsync(rig.Cam, settings, _ => { }, () => Task.FromResult(true)));
        Assert.Equal("SHA256:new", settings.Fingerprint);
    }

    [Fact]
    public async Task The_first_fingerprint_is_trusted_without_asking()
    {
        using var rig = new Rig();
        var settings = Saved();
        settings.Fingerprint = "";
        Assert.True(await CameraConnect.SavedAsync(rig.Cam, settings, _ => { }, () => throw new InvalidOperationException("shouldn't ask")));
        Assert.Equal("SHA256:fake", settings.Fingerprint);
    }

    [Fact]
    public async Task An_unreachable_camera_says_so_and_does_not_connect()
    {
        using var rig = new Rig();
        rig.Camera.FingerprintText = null;
        var said = new List<string>();
        Assert.False(await CameraConnect.SavedAsync(rig.Cam, Saved(), said.Add, () => Task.FromResult(true)));
        Assert.Contains(said, s => s.Contains("Couldn't reach the camera"));
    }
}

[Collection("settings file")]
public class SettingsTests
{
    [Fact]
    public void Settings_survive_a_save_and_load()
    {
        var s = new Settings { CameraIp = "10.0.0.9", Rotation = 90, Fingerprint = "fp", RecordingSince = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        s.Save();
        var back = Settings.Load();
        Assert.Equal("10.0.0.9", back.CameraIp);
        Assert.Equal(90, back.Rotation);
        Assert.Equal("fp", back.Fingerprint);
        Assert.Equal(s.RecordingSince, back.RecordingSince);
    }

    [Fact]
    public void Saving_replaces_the_whole_file_and_leaves_nothing_behind()
    {
        string file = Path.Combine(Settings.Folder, "settings.json");
        new Settings { CameraIp = "10.0.0.1", Fingerprint = "a-much-longer-fingerprint-than-the-next-one" }.Save();
        new Settings { CameraIp = "10.0.0.2", Fingerprint = "short" }.Save();
        Assert.Equal("short", Settings.Load().Fingerprint); // valid JSON, no tail left from the longer file
        Assert.False(File.Exists(file + ".new"));
    }

    [Fact]
    public void Phone_secrets_are_made_once_and_kept()
    {
        var s = new Settings();
        s.EnsurePhoneSecrets();
        Assert.Matches("^[A-HJKMNP-Z2-9]{8}$", s.PhoneCode); // no 0/O or 1/I/L to misread
        Assert.Matches("^[0-9A-F]{64}$", s.PhoneToken);
        var (code, token) = (s.PhoneCode, s.PhoneToken);
        s.EnsurePhoneSecrets();
        Assert.Equal((code, token), (s.PhoneCode, s.PhoneToken));
    }

    [Fact]
    public void The_password_is_kept_out_of_the_settings_file()
    {
        var s = new Settings { CameraUser = "user1" };
        s.SavePassword("hunter2");
        s.Save();
        Assert.Equal("hunter2", s.LoadPassword());
        Assert.DoesNotContain("hunter2", File.ReadAllText(Path.Combine(Settings.Folder, "settings.json")));
    }
}
