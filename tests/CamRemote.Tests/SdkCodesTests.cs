using Xunit;

namespace CamRemote.Tests;

/// <summary>
/// Sony's codes are explained by name. The fake header gives those names made-up numbers, away from the
/// numbers the app used to hard-code, so these pass only if the lookup really goes through the names.
/// </summary>
public class SdkCodesTests
{
    /// <summary>The number the (fake) SDK headers give a name.</summary>
    public static uint Code(string name) => (uint)SdkNames.Errors.Single(e => e.Value == name).Key;

    [Theory]
    [InlineData("CrError_Connect_Connect", "Couldn't reach the camera")]
    [InlineData("CrError_Connect_FailBusy", "Another app is using the camera")]
    [InlineData("CrError_Connect_SessionAlreadyOpened", "Another app is using the camera")]
    [InlineData("CrError_Connect_FailRejected", "refused the connection")]
    [InlineData("CrError_Connect_TimeOut", "Lost contact")]
    [InlineData("CrError_Connect_Reconnect_TimeOut", "Lost contact")]
    [InlineData("CrError_Connect_SSH_GetFingerprintFailed", "secure connection")]
    [InlineData("CrError_Connect_SSH_UserAuthenticationFailed", "rejected the user or password")]
    [InlineData("CrError_Connect_RemoteTransfer_NotSupported", "mode CamRemote can't control")]
    public void Errors_are_explained_by_their_sony_name(string name, string expected)
    {
        Assert.Contains(expected, SdkCodes.Explain(Code(name)));
        Assert.Contains(expected, SdkCodes.Explain(-Code(name))); // the bridge returns failures negated
    }

    [Fact]
    public void A_named_code_on_an_old_hard_coded_number_keeps_its_real_meaning()
    {
        // 0x8216 used to mean "wrong password" whatever the headers said.
        Assert.Equal("CrError_Connect_Fake_OnOldPasswordNumber", SdkCodes.Name(0x8216));
        Assert.Equal(SdkCodes.Meaning.Other, SdkCodes.MeaningOf(0x8216));
        Assert.Contains("error 0x8216", SdkCodes.Explain(0x8216));
    }

    [Fact]
    public void A_code_the_headers_dont_name_falls_back_to_the_old_numbers()
    {
        Assert.Null(SdkCodes.Name(0x820B));
        Assert.Equal(SdkCodes.Meaning.Busy, SdkCodes.MeaningOf(0x820B));
        Assert.Equal(SdkCodes.Notice.CardFull, SdkCodes.NoticeOf(0x20003));
    }

    [Fact]
    public void An_unknown_code_gets_a_general_message_with_its_number()
    {
        Assert.Equal("Something went wrong talking to the camera (error 0x8F00).", SdkCodes.Explain(-0x8F00));
        Assert.Null(SdkCodes.ExplainNotice(0x29999));
    }

    [Theory]
    [InlineData("CrWarning_Connect_Reconnecting", "Reconnecting")]
    [InlineData("CrWarning_Connect_Reconnected", "Back in touch")]
    [InlineData("CrWarning_File_StorageFull", "memory card is full")]
    [InlineData("CrWarning_NetworkErrorOccurred", "having trouble")]
    [InlineData("CrWarning_NetworkErrorRecovered", "is back")]
    [InlineData("CrWarning_MovieRecordingOperation_Result_NG", "couldn't start or stop recording")]
    public void Notices_are_explained_by_their_sony_name(string name, string expected) =>
        Assert.Contains(expected, SdkCodes.ExplainNotice(Code(name)));

    [Fact]
    public void Uninteresting_notices_stay_in_the_log() => Assert.Null(SdkCodes.ExplainNotice(Code("CrWarning_Unknown")));

    [Fact]
    public void The_fake_headers_name_everything_the_app_looks_for() => Assert.Empty(SdkCodes.MissingNames());

    [Fact]
    public void A_wrong_password_returned_by_connect_holds_auto_reconnect()
    {
        using var rig = new Rig();
        rig.Camera.ConnectResult = -(int)Code("CrError_Connect_SSH_UserAuthenticationFailed");
        rig.Ui.Invoke(() => rig.Cam.Connect(0, "admin", "wrong", "fp"));
        rig.Settle();
        Assert.True(rig.Ui.Invoke(() => rig.Cam.HoldReconnect));
        Assert.True(rig.Said("rejected the user or password"));
    }

    [Fact]
    public void The_old_password_number_no_longer_holds_auto_reconnect_when_it_means_something_else()
    {
        using var rig = new Rig();
        rig.Camera.ConnectFiresEvent = false;
        rig.Ui.Invoke(() => rig.Cam.Connect(0, "admin", "pw", "fp"));
        rig.Settle();
        rig.Camera.Fire(Native.EvError, 0x8216);
        rig.Settle();
        Assert.False(rig.Ui.Invoke(() => rig.Cam.HoldReconnect));
    }
}
