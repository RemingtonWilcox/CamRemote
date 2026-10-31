using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CamRemote;

/// <summary>P/Invoke surface of native/CamBridge/MacSupport.mm.</summary>
internal static class MacNative
{
    private const string Dll = "CamBridge";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FrameFn(IntPtr bgra, int width, int height, int stride);

    [DllImport(Dll)] private static extern int cb_secret_get([MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string account, byte[] output, int cap);
    [DllImport(Dll)] private static extern int cb_secret_set([MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string account, [MarshalAs(UnmanagedType.LPUTF8Str)] string password);
    [DllImport(Dll)] public static extern void cb_awake(int level);
    [DllImport(Dll)] private static extern int cb_cap_list(byte[] output, int cap);
    [DllImport(Dll)] public static extern int cb_cap_start([MarshalAs(UnmanagedType.LPUTF8Str)] string id, FrameFn frame);
    [DllImport(Dll)] public static extern void cb_cap_stop();
    [DllImport(Dll)] private static extern int cb_cap_format(byte[] output, int cap);
    [DllImport(Dll)] public static extern int cb_audio_start([MarshalAs(UnmanagedType.LPUTF8Str)] string match);
    [DllImport(Dll)] public static extern void cb_audio_stop();
    [DllImport(Dll)] public static extern void cb_audio_peaks(out float left, out float right);

    public static string LoadSecret(string service, string account)
    {
        var buffer = new byte[4096];
        int n = cb_secret_get(service, account, buffer, buffer.Length);
        return n > 0 ? Encoding.UTF8.GetString(buffer, 0, n) : "";
    }

    public static void SaveSecret(string service, string account, string password) => cb_secret_set(service, account, password);

    public static List<VideoDevice> ListCaptureDevices()
    {
        var buffer = new byte[64 * 1024];
        int n = cb_cap_list(buffer, buffer.Length);
        if (n <= 0) return [];
        return JsonDocument.Parse(buffer.AsMemory(0, n)).RootElement.EnumerateArray()
            .Select(d => new VideoDevice(d.GetProperty("id").GetString()!, d.GetProperty("name").GetString()!))
            .ToList();
    }

    public static string CaptureFormat()
    {
        var buffer = new byte[128];
        int n = cb_cap_format(buffer, buffer.Length);
        return n > 0 ? Encoding.UTF8.GetString(buffer, 0, n) : "";
    }
}
