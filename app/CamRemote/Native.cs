using System.Runtime.InteropServices;
using System.Text;

namespace CamRemote;

/// <summary>P/Invoke surface of native/CamBridge (a thin C wrapper over Sony's Camera Remote SDK).</summary>
internal static class Native
{
    private const string Dll = "CamBridge"; // CamBridge.dll, or libCamBridge.dylib on a Mac

    public const int EvConnected = 1, EvDisconnected = 2, EvPropertyChanged = 3, EvError = 4, EvWarning = 5;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void EventFn(int kind, uint a, uint b);

    [DllImport(Dll)] public static extern int cb_init(EventFn fn);
    [DllImport(Dll)] public static extern void cb_release();
    [DllImport(Dll)] private static extern int cb_enum(int timeoutSec, byte[] output, int cap);
    [DllImport(Dll)] public static extern int cb_make_ip_camera([MarshalAs(UnmanagedType.LPUTF8Str)] string ip, int ssh);
    [DllImport(Dll)] private static extern int cb_fingerprint(int index, byte[] output, int cap);
    [DllImport(Dll)] public static extern int cb_connect(int index,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fingerprint);
    [DllImport(Dll)] public static extern int cb_disconnect();
    [DllImport(Dll)] private static extern int cb_get_props(uint[]? codes, int n, byte[] output, int cap);
    [DllImport(Dll)] public static extern int cb_set_prop(uint code, ulong value, uint dataType);
    [DllImport(Dll)] public static extern int cb_command(uint command, uint param);
    [DllImport(Dll)] public static extern int cb_liveview(byte[] output, int cap, out uint frameNo);

    // Property lists can be large; one buffer is reused (calls are serialized by CameraService).
    private static readonly byte[] Buffer = new byte[8 * 1024 * 1024];

    private static string? Text(int n) => n >= 0 ? Encoding.UTF8.GetString(Buffer, 0, n) : null;

    public static string? Enumerate(int timeoutSec) => Text(cb_enum(timeoutSec, Buffer, Buffer.Length));
    public static string? Fingerprint(int index) => Text(cb_fingerprint(index, Buffer, Buffer.Length));
    public static string? GetProps(uint[]? codes) =>
        Text(cb_get_props(codes, codes?.Length ?? 0, Buffer, Buffer.Length));

    public static string ErrorName(int code)
    {
        long c = Math.Abs((long)code);
        return SdkNames.Errors.TryGetValue(c, out var name) ? $"{name} (0x{c:X})" : $"0x{c:X}";
    }
}
