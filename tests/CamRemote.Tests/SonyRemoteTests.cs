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
    public void A_second_record_tap_before_the_camera_reports_does_not_stop_the_take()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        var remote = Remote(rig, new());
        Assert.Null(remote.Record(true));
        Assert.Null(remote.Record(true)); // a slow phone link: the same tap again, before the next settings read
        rig.Settle();
        Assert.Single(rig.Camera.Commands, c => c == (FakeCamera.CmdMovieRecord, 1u));
        rig.Refresh();
        Assert.True(rig.Ui.Invoke(() => rig.Cam.IsRecording));
    }

    [Fact]
    public void Stop_right_after_start_still_stops()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        var remote = Remote(rig, new());
        remote.Record(true);
        remote.Record(false);
        rig.Settle();
        Assert.Equal(2, rig.Camera.Commands.Count(c => c == (FakeCamera.CmdMovieRecord, 1u)));
        rig.Refresh();
        Assert.False(rig.Ui.Invoke(() => rig.Cam.IsRecording));
    }

    [Fact]
    public void A_record_press_the_camera_refused_can_be_retried_at_once()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        var remote = Remote(rig, new());
        rig.Camera.CommandResult = -0x8402;
        remote.Record(true);
        rig.Settle();
        rig.Camera.CommandResult = 0;
        remote.Record(true);
        rig.Settle();
        Assert.Equal(2, rig.Camera.Commands.Count(c => c == (FakeCamera.CmdMovieRecord, 1u)));
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
