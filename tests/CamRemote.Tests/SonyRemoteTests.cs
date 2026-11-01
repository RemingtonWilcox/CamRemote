using Xunit;

namespace CamRemote.Tests;

public class SonyRemoteTests
{
    private sealed class Row(PropCode code, long[] allowed, List<long> picked) : IPropRow
    {
        public PropCode Code => code;
        public bool Allows(long value) => allowed.Contains(value);
        public void Pick(long value) => picked.Add(value);
        public object? Snapshot() => new { code };
    }

    private static SonyRemote Remote(Rig rig, List<long> picked) =>
        new(rig.Ui, rig.Cam, () => null, new Settings(), [new Row(PropCode.IsoSensitivity, [100, 200], picked)], () => ("", "", ""));

    [Fact]
    public void Nothing_is_sent_while_disconnected()
    {
        using var rig = new Rig();
        var remote = Remote(rig, new());
        Assert.Contains("isn't connected", remote.Record(true));
        Assert.Contains("isn't connected", remote.Set(((uint)PropCode.IsoSensitivity).ToString(), "100"));
        Assert.Contains("isn't connected", remote.Focus(0.5, 0.5));
        rig.Settle();
        Assert.Empty(rig.Camera.Commands);
    }

    [Fact]
    public void A_setting_must_be_one_the_camera_is_offering()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        var picked = new List<long>();
        var remote = Remote(rig, picked);
        string id = PropCode.IsoSensitivity.ToString();
        Assert.Null(remote.Set(id, "200"));
        Assert.Contains("isn't offering", remote.Set(id, "300"));
        Assert.Contains("isn't valid", remote.Set(id, "lots"));
        Assert.Contains("isn't offering", remote.Set("Nonsense", "1"));
        Assert.Equal([200L], picked);
    }

    [Fact]
    public void Record_on_when_already_recording_does_nothing()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.RecordingState, 1);
        rig.ConnectAndRead();
        Assert.Null(Remote(rig, new()).Record(true));
        rig.Settle();
        Assert.Empty(rig.Camera.Commands);
    }

    [Fact]
    public void The_state_reports_the_link_and_recording()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        var json = System.Text.Json.JsonSerializer.Serialize(Remote(rig, new()).State());
        Assert.Contains("\"link\":\"connected\"", json);
        Assert.Contains("\"recording\":false", json);
    }
}
