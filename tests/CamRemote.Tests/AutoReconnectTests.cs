using Xunit;

namespace CamRemote.Tests;

/// <summary>
/// Finding the camera again after the Wi-Fi drops, including when the router gives it a new IP.
/// The clock is the test's, so the grace period and the backoff are checked without waiting.
/// </summary>
[Collection("settings file")] // these save settings.json, so they take turns
public class AutoReconnectTests
{
    private const string Mac = "AA:BB:CC:00:11:22";

    private sealed class Setup : IDisposable
    {
        public readonly Rig Rig = new();
        public readonly Settings Settings = new() { CameraIp = "192.168.1.50", CameraMac = Mac, CameraUser = "user1", Fingerprint = "SHA256:fake" };
        public readonly AutoReconnect Auto;
        public DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        public Setup()
        {
            Rig.Camera.OnNetwork.Add(("192.168.1.50", Mac, "ILCE-7SM3"));
            Auto = Rig.Ui.Invoke(() => new AutoReconnect(Rig.Cam, Settings, _ => { }, () => Task.FromResult(true), () => Now));
        }

        public FakeCamera Camera => Rig.Camera;
        public LinkState State => Rig.Ui.Invoke(() => Rig.Cam.State);

        public void Tick(bool allowed = true)
        {
            Rig.Ui.Invoke(() => Auto.TickAsync(allowed)).Wait(TimeSpan.FromSeconds(10));
            Rig.Settle();
        }

        public void Wait(TimeSpan t)
        {
            Now += t;
            Rig.Settle();
        }

        public void ConnectFirst()
        {
            Tick();
            Rig.WaitFor(() => State == LinkState.Connected);
            Rig.Settle();
        }

        public void Drop()
        {
            Camera.DropLink(SdkCodesTests.Code("CrError_Connect_TimeOut"));
            Rig.WaitFor(() => State == LinkState.Disconnected);
            Rig.Settle();
        }

        public int Searches => Camera.Calls.Count(c => c == "Enumerate");

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public void The_first_try_at_startup_is_immediate()
    {
        using var s = new Setup();
        s.ConnectFirst();
        Assert.Equal("192.168.1.50", s.Camera.ConnectedIp);
        Assert.Equal(0, s.Auto.Attempts);
        Assert.Null(s.Auto.Status);
    }

    [Fact]
    public void A_camera_that_comes_back_at_a_new_ip_is_found_by_its_mac_and_reconnected()
    {
        using var s = new Setup();
        s.ConnectFirst();
        s.Drop();
        Assert.True(s.Camera.Linked); // the SDK keeps its handle to resume at the old address

        // The router hands the camera a new address while it's away.
        s.Camera.OnNetwork.Clear();
        s.Camera.OnNetwork.Add(("192.168.1.77", Mac, "ILCE-7SM3"));

        s.Wait(AutoReconnect.Grace + TimeSpan.FromSeconds(1));
        s.Tick();
        Rig.WaitFor(() => s.State == LinkState.Connected);
        s.Rig.Settle();

        Assert.Equal(1, s.Camera.StaleReleases); // the search let go of the dead session first
        Assert.Equal("192.168.1.77", s.Camera.ConnectedIp);
        Assert.Equal("192.168.1.77", s.Settings.CameraIp);
        Assert.Equal(0, s.Auto.Attempts);
        Assert.Null(s.Auto.Status);
    }

    [Fact]
    public void After_a_drop_the_sdk_gets_a_grace_period_to_resume_by_itself()
    {
        using var s = new Setup();
        s.ConnectFirst();
        int before = s.Searches;
        s.Drop();
        Assert.Contains("connection lost", s.Rig.Ui.Invoke(() => s.Auto.Status));

        s.Wait(AutoReconnect.Grace - TimeSpan.FromSeconds(1));
        s.Tick();
        Assert.Equal(before, s.Searches);

        // The SDK resumes the session at the same address: nothing for the app to do.
        s.Camera.Fire(Native.EvConnected);
        Rig.WaitFor(() => s.State == LinkState.Connected);
        s.Rig.Settle();
        s.Wait(TimeSpan.FromMinutes(5));
        s.Tick();
        Assert.Equal(before, s.Searches);
        Assert.Equal(0, s.Camera.StaleReleases);
        Assert.Null(s.Rig.Ui.Invoke(() => s.Auto.Status));
    }

    [Fact]
    public void Tries_back_off_while_the_camera_stays_away()
    {
        using var s = new Setup();
        s.ConnectFirst();
        s.Drop();
        s.Camera.OnNetwork.Clear();
        s.Camera.FingerprintText = null; // nothing answers at the old address either

        s.Wait(AutoReconnect.Grace);
        s.Tick();
        Assert.Equal(1, s.Auto.Attempts);
        Assert.Contains("try 1, next in 10 s", s.Rig.Ui.Invoke(() => s.Auto.Status));

        foreach (var (wait, attempts) in new[] { (10, 2), (20, 3), (40, 4), (60, 5), (60, 6) })
        {
            s.Wait(TimeSpan.FromSeconds(wait - 1));
            s.Tick();
            Assert.Equal(attempts - 1, s.Auto.Attempts); // not yet
            s.Wait(TimeSpan.FromSeconds(1));
            s.Tick();
            Assert.Equal(attempts, s.Auto.Attempts);
        }
        Assert.Equal(LinkState.Disconnected, s.State);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)]
    [InlineData(30, 60)]
    public void The_backoff_doubles_up_to_a_minute(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), AutoReconnect.Delay(attempt));

    [Fact]
    public void Another_camera_on_the_old_ip_is_not_taken_for_ours()
    {
        using var s = new Setup();
        s.ConnectFirst();
        s.Drop();
        s.Camera.OnNetwork.Clear();
        s.Camera.OnNetwork.Add(("192.168.1.50", "DE:AD:BE:EF:00:01", "ILCE-7SM3"));
        int connects = s.Camera.Calls.Count(c => c.StartsWith("Connect"));

        s.Wait(AutoReconnect.Grace);
        s.Tick();

        Assert.Equal(connects, s.Camera.Calls.Count(c => c.StartsWith("Connect")));
        Assert.Equal(Mac, s.Settings.CameraMac);
        Assert.Equal(LinkState.Disconnected, s.State);
    }

    [Fact]
    public void Nothing_is_tried_while_reconnecting_is_held_or_not_allowed()
    {
        using var s = new Setup();
        s.ConnectFirst();
        s.Drop();
        int before = s.Searches;
        s.Wait(TimeSpan.FromMinutes(2));

        s.Tick(allowed: false); // e.g. the Connect dialog is open
        Assert.Equal(before, s.Searches);

        s.Rig.Ui.Invoke(() => s.Rig.Cam.HoldReconnect = true); // e.g. a rejected password
        s.Tick();
        Assert.Equal(before, s.Searches);
        Assert.Null(s.Rig.Ui.Invoke(() => s.Auto.Status));
    }

    [Fact]
    public void A_connect_stuck_for_too_long_is_tried_again()
    {
        using var s = new Setup();
        s.Camera.ConnectFiresEvent = false; // the camera never answers
        s.Tick();
        Assert.Equal(LinkState.Connecting, s.State);
        int connects = s.Camera.Calls.Count(c => c.StartsWith("Connect"));

        s.Wait(AutoReconnect.StuckConnecting - TimeSpan.FromSeconds(1));
        s.Tick();
        Assert.Equal(connects, s.Camera.Calls.Count(c => c.StartsWith("Connect")));

        s.Wait(TimeSpan.FromSeconds(2));
        s.Tick();
        Assert.Equal(connects + 1, s.Camera.Calls.Count(c => c.StartsWith("Connect")));
    }

    [Theory]
    [InlineData("AA:BB:CC:00:11:22", "aa-bb-cc-00-11-22", true)]
    [InlineData("AA:BB:CC:00:11:22", "AA:BB:CC:00:11:23", false)]
    [InlineData("", "", false)]
    public void Mac_addresses_match_however_they_are_written(string a, string b, bool same) =>
        Assert.Equal(same, CameraConnect.SameMac(a, b));
}
