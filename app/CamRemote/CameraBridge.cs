namespace CamRemote;

/// <summary>
/// The calls <see cref="CameraService"/> makes into the native bridge. The app uses <see cref="NativeBridge"/>;
/// tests swap in a fake camera so the connection, settings and focus logic can run without Sony's SDK.
/// Return values follow CamBridge: 0 for success, a negative SDK error, or null when there was nothing to read.
/// </summary>
internal interface ICameraBridge
{
    int Init(Native.EventFn onEvent);
    void Release();
    string? Enumerate(int timeoutSec);
    int MakeIpCamera(string ip, bool ssh, string model);
    string? Fingerprint(int index);
    int Connect(int index, string user, string password, string fingerprint);
    int Disconnect();
    string? GetProps(uint[]? codes);
    int SetProp(uint code, ulong value, uint dataType);
    int Command(uint command, uint param);
    int QueueCount();
    string? FocusFrames();
}

/// <summary>The real bridge: CamBridge.dll or libCamBridge.dylib, wrapping Sony's SDK.</summary>
internal sealed class NativeBridge : ICameraBridge
{
    public static readonly NativeBridge Instance = new();

    public int Init(Native.EventFn onEvent) => Native.cb_init(onEvent);
    public void Release() => Native.cb_release();
    public string? Enumerate(int timeoutSec) => Native.Enumerate(timeoutSec);
    public int MakeIpCamera(string ip, bool ssh, string model) => Native.cb_make_ip_camera(ip, ssh ? 1 : 0, model);
    public string? Fingerprint(int index) => Native.Fingerprint(index);
    public int Connect(int index, string user, string password, string fingerprint) => Native.cb_connect(index, user, password, fingerprint);
    public int Disconnect() => Native.cb_disconnect();
    public string? GetProps(uint[]? codes) => Native.GetProps(codes);
    public int SetProp(uint code, ulong value, uint dataType) => Native.cb_set_prop(code, value, dataType);
    public int Command(uint command, uint param) => Native.cb_command(command, param);
    public int QueueCount() => Native.cb_queue_count();
    public string? FocusFrames() => Native.FocusFrames();
}
