namespace CamRemote;

/// <summary>
/// What Sony's error and notice codes mean for the person using the app.
///
/// The codes are looked up by their names in Sony's headers (CrError.h), through <see cref="SdkNames.Errors"/>,
/// which tools/gen_names.py generates from the SDK the app is built with. So the numbers always come from
/// the real headers, never from this file. gen_names.py also checks that every name quoted below exists in
/// those headers and prints a warning for any that doesn't, and the app logs the same list at startup.
///
/// VERIFICATION STATUS (November 2026): Sony's headers and API reference are only available inside the SDK
/// download, behind Sony's licence, so they could not be read when this was written. The names below follow
/// Sony's naming scheme as recalled from the SDK and are UNVERIFIED until a build against the real SDK runs
/// without "not in the SDK headers" warnings. The one code confirmed from a public source is
/// CrError_Connect_RemoteTransfer_NotSupported = 0x8219 (a CrSDK 2.01 log in github.com/goudvuur/sonshell issue 2).
/// If a name is wrong, the effect is limited: that error gets the generic message with its number.
/// </summary>
public static class SdkCodes
{
    public enum Meaning { Other, Unreachable, Busy, Refused, LostContact, Secure, BadPassword, NoRemoteMode }

    public enum Notice { Other, Reconnected, Reconnecting, CardFull, WifiTrouble, WifiBack, RecordFailed }

    // UNVERIFIED names; see the class comment.
    private static readonly Dictionary<string, Meaning> ErrorNames = new()
    {
        ["CrError_Connect_Connect"] = Meaning.Unreachable,
        ["CrError_Connect_FailBusy"] = Meaning.Busy,
        ["CrError_Connect_SessionAlreadyOpened"] = Meaning.Busy,
        ["CrError_Connect_FailRejected"] = Meaning.Refused,
        ["CrError_Connect_Disconnected"] = Meaning.LostContact,
        ["CrError_Connect_TimeOut"] = Meaning.LostContact,
        ["CrError_Connect_Reconnect_TimeOut"] = Meaning.LostContact,
        ["CrError_Connect_SSH_ServerConnectFailed"] = Meaning.Secure,
        ["CrError_Connect_SSH_ServerAuthenticationFailed"] = Meaning.Secure,
        ["CrError_Connect_SSH_PortForwardFailed"] = Meaning.Secure,
        ["CrError_Connect_SSH_GetFingerprintFailed"] = Meaning.Secure,
        ["CrError_Connect_SSH_UserAuthenticationFailed"] = Meaning.BadPassword,
        ["CrError_Connect_RemoteTransfer_NotSupported"] = Meaning.NoRemoteMode, // 0x8219, seen in a public log
    };

    // UNVERIFIED names; see the class comment.
    private static readonly Dictionary<string, Notice> NoticeNames = new()
    {
        ["CrWarning_Connect_Reconnected"] = Notice.Reconnected,
        ["CrWarning_Connect_Reconnecting"] = Notice.Reconnecting,
        ["CrWarning_File_StorageFull"] = Notice.CardFull,
        ["CrWarning_NetworkErrorOccurred"] = Notice.WifiTrouble,
        ["CrWarning_NetworkErrorRecovered"] = Notice.WifiBack,
        ["CrWarning_MovieRecordingOperation_Result_NG"] = Notice.RecordFailed,
    };

    // The numbers the app used before November 2026, typed in by hand and never checked against CrError.h.
    // Used ONLY for a code the SDK headers give no name at all, so a correct name always wins over them.
    private static readonly Dictionary<long, Meaning> LegacyErrors = new()
    {
        [0x8202] = Meaning.Unreachable,
        [0x820B] = Meaning.Busy, [0x8210] = Meaning.Busy,
        [0x820A] = Meaning.Refused,
        [0x8207] = Meaning.LostContact, [0x8208] = Meaning.LostContact, [0x8209] = Meaning.LostContact,
        [0x8212] = Meaning.Secure, [0x8213] = Meaning.Secure, [0x8214] = Meaning.Secure, [0x8215] = Meaning.Secure,
        [0x8216] = Meaning.BadPassword,
    };

    private static readonly Dictionary<long, Notice> LegacyNotices = new()
    {
        [0x20001] = Notice.Reconnected, [0x20002] = Notice.Reconnecting, [0x20003] = Notice.CardFull,
        [0x20007] = Notice.WifiTrouble, [0x20008] = Notice.WifiBack, [0x2006A] = Notice.RecordFailed,
    };

    /// <summary>Sony's name for a code, or null if the SDK headers don't have one.</summary>
    public static string? Name(long code) => SdkNames.Errors.TryGetValue(Math.Abs(code), out var n) ? n : null;

    public static Meaning MeaningOf(long code) =>
        Name(code) is { } name
            ? ErrorNames.GetValueOrDefault(name, Meaning.Other)
            : LegacyErrors.GetValueOrDefault(Math.Abs(code), Meaning.Other);

    public static Notice NoticeOf(long code) =>
        Name(code) is { } name
            ? NoticeNames.GetValueOrDefault(name, Notice.Other)
            : LegacyNotices.GetValueOrDefault(Math.Abs(code), Notice.Other);

    /// <summary>A connection error in plain words.</summary>
    public static string Explain(long err) => MeaningOf(err) switch
    {
        Meaning.Unreachable => "Couldn't reach the camera. It may be off, or another app like Imaging Edge or Monitor & Control may be using it.",
        Meaning.Busy => "Another app is using the camera. Close Imaging Edge or Monitor & Control, then CamRemote can connect.",
        Meaning.Refused => "The camera refused the connection. Check that Remote Shooting is on.",
        Meaning.LostContact => "Lost contact with the camera. Check that it's on and on the same Wi-Fi.",
        Meaning.Secure => "Couldn't make a secure connection to the camera. Turn it off and on, then try again.",
        Meaning.BadPassword => BadPasswordText,
        Meaning.NoRemoteMode => "The camera is in a mode CamRemote can't control. Check that Remote Shooting is on and the connection mode is set for remote shooting.",
        _ => $"Something went wrong talking to the camera (error 0x{Math.Abs(err):X}).",
    };

    public const string BadPasswordText =
        "The camera rejected the user or password. Check MENU › Network › Network Option › Access Authen. Info (case-sensitive).";

    /// <summary>The camera notices worth interrupting someone for. The rest only go in the log.</summary>
    public static string? ExplainNotice(long notice) => NoticeOf(notice) switch
    {
        Notice.Reconnecting => "Lost the camera for a moment. Reconnecting…",
        Notice.Reconnected => "Back in touch with the camera.",
        Notice.CardFull => "The camera's memory card is full.",
        Notice.WifiTrouble => "The Wi-Fi connection to the camera is having trouble.",
        Notice.WifiBack => "The Wi-Fi connection to the camera is back.",
        Notice.RecordFailed => "The camera couldn't start or stop recording.",
        _ => null,
    };

    /// <summary>The names this file relies on that the SDK it was built with doesn't have. Logged at startup.</summary>
    public static IEnumerable<string> MissingNames()
    {
        var known = SdkNames.Errors.Values.ToHashSet();
        return ErrorNames.Keys.Concat(NoticeNames.Keys).Where(n => !known.Contains(n));
    }
}
