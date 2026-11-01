using Xunit;

namespace CamRemote.Tests;

public class CameraServiceTests
{
    [Fact]
    public void Connecting_takes_control_of_the_settings_and_asks_for_the_sharper_picture()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        Assert.Equal(LinkState.Connected, rig.Ui.Invoke(() => rig.Cam.State));
        Assert.Contains((PropCode.PriorityKeySettings, 2L), rig.Camera.Sets);
        Assert.Contains((PropCode.LiveView_Image_Quality, 1L), rig.Camera.Sets);
        Assert.True(rig.Said("Connected."));
    }

    [Fact]
    public void A_refused_connection_goes_back_to_disconnected_with_plain_words()
    {
        using var rig = new Rig();
        rig.Camera.ConnectResult = -0x820B;
        rig.Ui.Invoke(() => rig.Cam.Connect(0, "admin", "pw", "fp"));
        rig.Settle();
        Assert.Equal(LinkState.Disconnected, rig.Ui.Invoke(() => rig.Cam.State));
        Assert.True(rig.Said("Another app is using the camera"));
    }

    [Fact]
    public void Reading_settings_parses_every_property()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.IsoSensitivity, 640, type: 0x0003, candidates: [100, 640, 12800]);
        rig.ConnectAndRead();
        var iso = rig.Ui.Invoke(() => rig.Cam.Get(PropCode.IsoSensitivity));
        Assert.NotNull(iso);
        Assert.Equal(640, iso!.Value);
        Assert.Equal(new long[] { 100, 640, 12800 }, iso.Candidates);
        Assert.Equal(0x0003u, iso.Type);
    }

    [Fact]
    public void Quick_clicks_on_one_setting_send_only_the_newest_value()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.IsoSensitivity, 100, type: 0x0003, candidates: [100, 200, 400, 800]);
        rig.ConnectAndRead();
        rig.Camera.Sets.Clear();
        rig.Jam(); // the camera is busy, so the clicks pile up
        rig.Ui.Invoke(() =>
        {
            rig.Cam.Set(PropCode.IsoSensitivity, 200);
            rig.Cam.Set(PropCode.IsoSensitivity, 400);
            rig.Cam.Set(PropCode.IsoSensitivity, 800);
        });
        rig.Camera.Open.Set();
        rig.Settle();
        Assert.Equal([(PropCode.IsoSensitivity, 800L)], rig.Camera.SetsOf(PropCode.IsoSensitivity));
    }

    [Fact]
    public void A_refused_setting_is_explained()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.IsoSensitivity, 100, type: 0x0003);
        rig.ConnectAndRead();
        rig.Camera.SetPropResult = -0x8402;
        rig.Ui.Invoke(() => rig.Cam.Set(PropCode.IsoSensitivity, 200));
        rig.Settle();
        Assert.True(rig.Said("didn't accept that setting"));
    }

    [Fact]
    public void Setting_something_the_camera_does_not_report_sends_nothing()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        rig.Camera.Sets.Clear();
        rig.Ui.Invoke(() => rig.Cam.Set(PropCode.ShutterSpeed, 5));
        rig.Settle();
        Assert.Empty(rig.Camera.Sets);
    }

    [Fact]
    public void Losing_control_to_the_dials_asks_again_then_restarts_the_link()
    {
        using var rig = new Rig();
        rig.ConnectAndRead(); // had control once
        rig.Camera.Set(PropCode.PriorityKeySettings, 1).Ignores = true; // the dials took over and keep it
        rig.Camera.Sets.Clear();
        for (int i = 0; i < 4; i++) rig.Refresh();
        Assert.Equal(4, rig.Camera.SetsOf(PropCode.PriorityKeySettings).Count);
        Assert.Equal(0, rig.Camera.Disconnects);
        rig.Refresh(); // fifth miss
        Assert.Equal(1, rig.Camera.Disconnects);
        Assert.Equal(LinkState.Disconnected, rig.Ui.Invoke(() => rig.Cam.State));
        Assert.True(rig.Said("Reconnecting"));
    }

    [Fact]
    public void A_camera_that_stops_answering_gets_its_link_restarted()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        rig.Camera.GetPropsFailures = 5;
        for (int i = 0; i < 5; i++) rig.Refresh();
        Assert.Equal(1, rig.Camera.Disconnects);
        Assert.True(rig.Said("stopped answering"));
    }

    [Fact]
    public void Disconnecting_by_hand_holds_auto_reconnect_and_forgets_the_old_values()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        Assert.NotEmpty(rig.Ui.Invoke(() => rig.Cam.Props));
        rig.Ui.Invoke(rig.Cam.Disconnect);
        rig.Settle();
        Assert.True(rig.Cam.HoldReconnect);
        Assert.Empty(rig.Ui.Invoke(() => rig.Cam.Props));
        Assert.Equal(LinkState.Disconnected, rig.Ui.Invoke(() => rig.Cam.State));
    }

    [Fact]
    public void A_dropped_link_clears_the_stale_values()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.RecordingState, 1);
        rig.ConnectAndRead();
        Assert.True(rig.Ui.Invoke(() => rig.Cam.IsRecording));
        rig.Camera.Fire(Native.EvDisconnected, 0x8207);
        rig.Settle();
        Assert.False(rig.Ui.Invoke(() => rig.Cam.IsRecording)); // "recording" from before the drop would mislead
        Assert.Equal(LinkState.Disconnected, rig.Ui.Invoke(() => rig.Cam.State));
    }

    [Fact]
    public void A_rejected_password_holds_auto_reconnect()
    {
        using var rig = new Rig();
        rig.Camera.ConnectFiresEvent = false;
        rig.Ui.Invoke(() => rig.Cam.Connect(0, "admin", "wrong", "fp"));
        rig.Settle();
        rig.Camera.Fire(Native.EvError, 0x8216);
        rig.Settle();
        Assert.True(rig.Cam.HoldReconnect);
        Assert.True(rig.Said("rejected the user or password"));
    }

    [Fact]
    public void The_same_notice_twice_in_a_row_is_shown_once()
    {
        using var rig = new Rig();
        rig.Camera.Fire(Native.EvWarning, 0x20003);
        rig.Camera.Fire(Native.EvWarning, 0x20003);
        rig.Settle();
        lock (rig.Messages) Assert.Single(rig.Messages, m => m.Contains("memory card is full"));
    }

    [Fact]
    public void Record_toggles_with_down_then_up()
    {
        using var rig = new Rig();
        rig.ConnectAndRead();
        rig.Ui.Invoke(rig.Cam.ToggleRecord);
        rig.Settle();
        Assert.Equal([(FakeCamera.CmdMovieRecord, 1u), (FakeCamera.CmdMovieRecord, 0u)], rig.Camera.Commands);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0u, 0u)]
    [InlineData(1.0, 1.0, 639u, 479u)]
    [InlineData(0.5, 0.5, 320u, 240u)]
    [InlineData(-3, 7, 0u, 479u)] // off the picture is pinned to its edge
    public void A_tap_becomes_a_point_on_sonys_640_by_480_grid(double fx, double fy, uint x, uint y)
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.FocusMode, 3);
        rig.Camera.Set(PropCode.TouchOperation, 2);
        rig.Camera.Set(PropCode.FunctionOfTouchOperation, 3);
        rig.Camera.Set(PropCode.RemoteTouchOperationEnableStatus, 1);
        rig.ConnectAndRead();
        Assert.Null(rig.Ui.Invoke(() => rig.Cam.TouchFocus(fx, fy)));
        rig.Settle();
        Assert.Equal([(PropCode.RemoteTouchOperation, (long)((x << 16) | y))], rig.Camera.SetsOf(PropCode.RemoteTouchOperation));
    }

    [Fact]
    public void A_tap_in_manual_focus_focuses_once_then_goes_back_to_manual()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.FocusMode, 1);
        rig.Camera.Set(PropCode.TouchOperation, 2);
        rig.Camera.Set(PropCode.FunctionOfTouchOperation, 3);
        rig.ConnectAndRead();
        Assert.Null(rig.Ui.Invoke(() => rig.Cam.TouchFocus(0.5, 0.5)));
        Rig.WaitFor(() => rig.Said("Back in manual"), 8000);
        rig.Settle();
        Assert.Equal([3L, 1L], rig.Camera.SetsOf(PropCode.FocusMode).Select(s => s.Value));
        Assert.Single(rig.Camera.SetsOf(PropCode.RemoteTouchOperation));
        Assert.Contains((FakeCamera.CmdCancelRemoteTouch, 1u), rig.Camera.Commands);
        Assert.Equal(1L, rig.Camera.Value(PropCode.FocusMode));
        Assert.Empty(rig.Ui.Errors);
    }

    [Fact]
    public void A_tap_with_tap_does_off_explains_itself()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.FunctionOfTouchOperation, 1);
        rig.ConnectAndRead();
        Assert.Contains("Off", rig.Ui.Invoke(() => rig.Cam.TouchFocus(0.5, 0.5)));
    }

    [Fact]
    public void Focus_steps_are_limited_to_sonys_range()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.NearFar, 0, type: 0x1002);
        rig.ConnectAndRead();
        rig.Cam.FocusStep(99);
        Rig.WaitFor(() => rig.Camera.SetsOf(PropCode.NearFar).Count == 1);
        rig.Cam.FocusStep(-99);
        Rig.WaitFor(() => rig.Camera.SetsOf(PropCode.NearFar).Count == 2);
        Assert.Equal([7L, -7L], rig.Camera.SetsOf(PropCode.NearFar).Select(s => s.Value));
    }

    [Fact]
    public void The_base_iso_button_prefers_the_plain_value_over_an_expanded_one()
    {
        using var rig = new Rig();
        const long Expanded = (1L << 24) | 12800;
        rig.Camera.Set(PropCode.IsoSensitivity, 640, type: 0x0003, candidates: [640, Expanded, 12800]);
        rig.ConnectAndRead();
        Assert.Null(rig.Ui.Invoke(() => rig.Cam.SetBaseIso(12800)));
        rig.Settle();
        Assert.Equal([(PropCode.IsoSensitivity, 12800L)], rig.Camera.SetsOf(PropCode.IsoSensitivity));
        Assert.Contains("isn't offering", rig.Ui.Invoke(() => rig.Cam.SetBaseIso(3200)));
    }

    [Fact]
    public void The_meter_reads_thousandths_of_a_stop_and_hides_nonsense()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.MeteredManualLevel, unchecked((ushort)(short)-1300), type: 0x1002);
        rig.ConnectAndRead();
        Assert.Equal(-1.3, rig.Ui.Invoke(() => rig.Cam.MeterStops));
        rig.Camera.Set(PropCode.MeteredManualLevel, 0x7FFF, type: 0x1002);
        rig.Refresh();
        Assert.Null(rig.Ui.Invoke(() => rig.Cam.MeterStops));
    }

    [Fact]
    public void Magnifier_ratio_and_choices_come_from_the_upper_four_bytes()
    {
        using var rig = new Rig();
        long Mag(long ratio, long pos = 0xFFFFFFFF) => (ratio << 32) | pos;
        rig.Camera.Set(PropCode.Focus_Magnifier_Setting, Mag(40, 0x014000F0), type: 0x0004,
            candidates: [Mag(10), Mag(40), Mag(20), Mag(40), 0xFFFFFFFF]);
        rig.ConnectAndRead();
        Assert.Equal(40, rig.Ui.Invoke(() => rig.Cam.MagnifyRatio));
        Assert.Equal([20L, 40L], rig.Ui.Invoke(() => rig.Cam.MagnifyChoices()));
    }

    [Fact]
    public void No_magnifier_choices_when_the_camera_will_not_change_it()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.Focus_Magnifier_Setting, 0xFFFFFFFF, type: 0x0004, candidates: [40L << 32], writable: false);
        rig.ConnectAndRead();
        Assert.Equal(0, rig.Ui.Invoke(() => rig.Cam.MagnifyRatio));
        Assert.Empty(rig.Ui.Invoke(() => rig.Cam.MagnifyChoices()));
    }

    [Fact]
    public void Focus_position_is_a_share_of_the_lens_range()
    {
        using var rig = new Rig();
        rig.Camera.Set(PropCode.FocusPositionCurrentValue, 750, candidates: [500, 1500, 1]);
        rig.Camera.Set(PropCode.FocusPositionSetting, 0, candidates: [500, 1500, 1]);
        rig.ConnectAndRead();
        Assert.Equal(0.25, rig.Ui.Invoke(() => rig.Cam.FocusPosition));
        Assert.Null(rig.Ui.Invoke(() => rig.Cam.FocusTo(1.0)));
        rig.Settle();
        Assert.Equal([(PropCode.FocusPositionSetting, 1500L)], rig.Camera.SetsOf(PropCode.FocusPositionSetting));
    }

    [Fact]
    public void The_cameras_focus_boxes_are_read_as_shares_of_the_picture()
    {
        using var rig = new Rig();
        rig.Camera.FramesJson = "{\"f\":[[1,0,2,320,640,240,480,64,48],[3,0,1,160,640,120,480,32,24]]}";
        rig.ConnectAndRead();
        Rig.WaitFor(() => rig.Ui.Invoke(() => rig.Cam.Frames.Count) == 2);
        var frames = rig.Ui.Invoke(() => rig.Cam.Frames);
        Assert.Equal(new FocusFrame(FrameKind.Focus, true, 0.5, 0.5, 0.1, 0.1), frames[0]);
        Assert.Equal(new FocusFrame(FrameKind.Tracking, false, 0.25, 0.25, 0.05, 0.05), frames[1]);
        Assert.True(rig.Ui.Invoke(() => rig.Cam.IsTracking));
    }

    [Fact]
    public void Broken_focus_box_data_is_skipped_not_fatal()
    {
        using var rig = new Rig();
        rig.Camera.FramesJson = "{\"f\":[[1,0,2,320,0,240,480,64,48]]}"; // a zero denominator
        rig.ConnectAndRead();
        Thread.Sleep(300);
        Assert.Empty(rig.Ui.Invoke(() => rig.Cam.Frames));
        rig.Camera.FramesJson = "{\"f\":[[1,0]]}"; // too short
        Thread.Sleep(300);
        Assert.Empty(rig.Ui.Invoke(() => rig.Cam.Frames));
        Assert.Empty(rig.Ui.Errors);
    }

    [Fact]
    public void Closing_releases_the_sdk()
    {
        var rig = new Rig();
        rig.Dispose();
        Assert.Equal(1, rig.Camera.Releases);
    }
}
