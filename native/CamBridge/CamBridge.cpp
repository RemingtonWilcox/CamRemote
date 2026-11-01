// CamBridge: a flat C API over Sony's Camera Remote SDK so the C# app can P/Invoke it.
// One camera at a time. Strings cross the boundary as UTF-8; lists come back as JSON.

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <shared_mutex>
#include <string>
#include <vector>

#include "CameraRemote_SDK.h"
#include "CrDeviceProperty.h"
#include "IDeviceCallback.h"

namespace SDK = SCRSDK;

#ifdef _WIN32
#define CB_API extern "C" __declspec(dllexport)
#else
#define CB_API extern "C" __attribute__((visibility("default")))
#define __stdcall
#endif

// Event kinds sent to the app's callback.
enum CbEventKind : int {
    CbConnected = 1,
    CbDisconnected = 2,
    CbPropertyChanged = 3,
    CbError = 4,
    CbWarning = 5,
    CbLiveViewPropsChanged = 6,
};

typedef void(__stdcall* CbEventFn)(int kind, uint32_t a, uint32_t b);

static CbEventFn g_event = nullptr;
// Connect/disconnect take this exclusively; other calls share it, so live view can run beside settings.
// (The app already serializes its non-live-view calls on one thread.)
static std::shared_mutex g_lock;
static SDK::ICrEnumCameraObjectInfo* g_enum = nullptr;
static SDK::ICrCameraObjectInfo* g_ipInfo = nullptr;
static SDK::CrDeviceHandle g_handle = 0;
static std::atomic<bool> g_connected{false};

static void emit(int kind, uint32_t a, uint32_t b = 0)
{
    if (g_event) g_event(kind, a, b);
}

class Callback : public SDK::IDeviceCallback {
public:
    void OnConnected(SDK::DeviceConnectionVersioin) override
    {
        g_connected = true;
        emit(CbConnected, 0);
    }
    void OnDisconnected(CrInt32u error) override
    {
        g_connected = false;
        emit(CbDisconnected, error);
    }
    void OnPropertyChanged() override { emit(CbPropertyChanged, 0, 0); }
    void OnPropertyChangedCodes(CrInt32u num, CrInt32u* codes) override
    {
        for (CrInt32u i = 0; i < num; ++i) emit(CbPropertyChanged, codes[i], 1);
    }
    void OnLvPropertyChanged() override { emit(CbLiveViewPropsChanged, 0); }
    void OnLvPropertyChangedCodes(CrInt32u, CrInt32u*) override { emit(CbLiveViewPropsChanged, 0); }
    void OnError(CrInt32u error) override { emit(CbError, error); }
    void OnWarning(CrInt32u warning) override { emit(CbWarning, warning); }
    void OnWarningExt(CrInt32u warning, CrInt32, CrInt32, CrInt32) override { emit(CbWarning, warning); }
};

static Callback g_callback;

#ifdef _WIN32
static std::string utf8(const CrChar* s, size_t len = (size_t)-1)
{
    if (!s) return {};
    int wlen = len == (size_t)-1 ? -1 : (int)len;
    int n = WideCharToMultiByte(CP_UTF8, 0, s, wlen, nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s, wlen, out.data(), n, nullptr, nullptr);
    // Fixed-size SDK fields can carry junk after the terminator.
    out.resize(strlen(out.c_str()));
    return out;
}
#else
// The Mac SDK's strings are already UTF-8.
static std::string utf8(const CrChar* s, size_t len = (size_t)-1)
{
    if (!s) return {};
    std::string out = len == (size_t)-1 ? std::string(s) : std::string(s, len);
    out.resize(strlen(out.c_str()));
    return out;
}
#endif

static void jsonStr(std::string& j, const std::string& s)
{
    j += '"';
    for (char c : s) {
        if (c == '"' || c == '\\') { j += '\\'; j += c; }
        else if ((unsigned char)c < 0x20) { char b[8]; snprintf(b, sizeof b, "\\u%04x", c); j += b; }
        else j += c;
    }
    j += '"';
}

// Returns the string length, or -1 when the caller's buffer is too small.
// (SDK failures are returned as -error, and those are always >= 0x8000.)
static int copyOut(const std::string& s, char* out, int cap)
{
    int need = (int)s.size() + 1;
    if (!out || cap < need) return -1;
    memcpy(out, s.c_str(), need);
    return need - 1;
}

static std::vector<int64_t> decodeValues(SDK::CrDataType type, const uint8_t* buf, uint32_t size)
{
    std::vector<int64_t> v;
    if (!buf || !size) return v;
    int len;
    switch (type & 0x100F) {
    case SDK::CrDataType_UInt8: case SDK::CrDataType_Int8: len = 1; break;
    case SDK::CrDataType_UInt16: case SDK::CrDataType_Int16: len = 2; break;
    case SDK::CrDataType_UInt32: case SDK::CrDataType_Int32: len = 4; break;
    case SDK::CrDataType_UInt64: case SDK::CrDataType_Int64: len = 8; break;
    default: return v;
    }
    for (uint32_t i = 0; i + len <= size; i += len) {
        int64_t d = 0;
        switch (type & 0x100F) {
        case SDK::CrDataType_UInt8: d = buf[i]; break;
        case SDK::CrDataType_Int8: d = (int8_t)buf[i]; break;
        case SDK::CrDataType_UInt16: d = *(const uint16_t*)(buf + i); break;
        case SDK::CrDataType_Int16: d = *(const int16_t*)(buf + i); break;
        case SDK::CrDataType_UInt32: d = *(const uint32_t*)(buf + i); break;
        case SDK::CrDataType_Int32: d = *(const int32_t*)(buf + i); break;
        default: d = *(const int64_t*)(buf + i); break;
        }
        v.push_back(d);
    }
    return v;
}

CB_API int cb_init(CbEventFn fn)
{
    g_event = fn;
    return SDK::Init() ? 0 : -1;
}

CB_API void cb_release()
{
    std::unique_lock<std::shared_mutex> l(g_lock);
    if (g_handle) { SDK::Disconnect(g_handle); SDK::ReleaseDevice(g_handle); g_handle = 0; }
    if (g_enum) { g_enum->Release(); g_enum = nullptr; }
    if (g_ipInfo) { g_ipInfo->Release(); g_ipInfo = nullptr; }
    SDK::Release();
    g_event = nullptr;
}

// Finds cameras on USB and the local network. Returns a JSON array.
CB_API int cb_enum(int timeoutSec, char* out, int cap)
{
    std::unique_lock<std::shared_mutex> l(g_lock);
    if (g_handle) return -2;  // enumerating would invalidate the connected camera's info
    if (g_enum) { g_enum->Release(); g_enum = nullptr; }
    SDK::CrError err = SDK::EnumCameraObjects(&g_enum, (CrInt8u)timeoutSec);
    std::string j = "[";
    if (!err && g_enum) {
        for (CrInt32u i = 0; i < g_enum->GetCount(); ++i) {
            auto* info = g_enum->GetCameraObjectInfo(i);
            std::string conn = utf8(info->GetConnectionTypeName());
            if (i) j += ',';
            j += "{\"index\":" + std::to_string(i) + ",\"model\":";
            jsonStr(j, utf8(info->GetModel(), info->GetModelSize()));
            j += ",\"conn\":";
            jsonStr(j, conn);
            j += ",\"ip\":";
            jsonStr(j, conn == "IP" ? utf8(info->GetIPAddressChar(), info->GetIPAddressCharSize()) : "");
            j += ",\"mac\":";
            jsonStr(j, conn == "IP" ? utf8(info->GetMACAddressChar(), info->GetMACAddressCharSize()) : "");
            j += ",\"ssh\":" + std::string(info->GetSSHsupport() == SDK::CrSSHsupport_ON ? "true" : "false") + "}";
        }
    }
    j += "]";
    return copyOut(j, out, cap);
}

static SDK::ICrCameraObjectInfo* pickInfo(int index)
{
    if (index == -1) return g_ipInfo;
    if (!g_enum || index < 0 || (CrInt32u)index >= g_enum->GetCount()) return nullptr;
    return const_cast<SDK::ICrCameraObjectInfo*>(g_enum->GetCameraObjectInfo(index));
}

static SDK::CrCameraDeviceModelList modelFromName(const std::string& name)
{
    static const std::pair<const char*, SDK::CrCameraDeviceModelList> models[] = {
        {"ILCE-1M2", SDK::CrCameraDeviceModel_ILCE_1M2}, {"ILCE-1", SDK::CrCameraDeviceModel_ILCE_1},
        {"ILCE-9M3", SDK::CrCameraDeviceModel_ILCE_9M3}, {"ILCE-7RM6", SDK::CrCameraDeviceModel_ILCE_7RM6},
        {"ILCE-7RM5", SDK::CrCameraDeviceModel_ILCE_7RM5}, {"ILCE-7CR", SDK::CrCameraDeviceModel_ILCE_7CR},
        {"ILCE-7M5", SDK::CrCameraDeviceModel_ILCE_7M5}, {"ILCE-7M4", SDK::CrCameraDeviceModel_ILCE_7M4},
        {"ILCE-7CM2", SDK::CrCameraDeviceModel_ILCE_7CM2}, {"ILCE-6700", SDK::CrCameraDeviceModel_ILCE_6700},
        {"ILME-FX6", SDK::CrCameraDeviceModel_ILME_FX6}, {"ILME-FX3A", SDK::CrCameraDeviceModel_ILME_FX3A},
        {"ILME-FX3", SDK::CrCameraDeviceModel_ILME_FX3}, {"ILME-FX2", SDK::CrCameraDeviceModel_ILME_FX2},
        {"ILME-FX30", SDK::CrCameraDeviceModel_ILME_FX30}, {"MPC-2610", SDK::CrCameraDeviceModel_MPC_2610},
        {"ZV-E1", SDK::CrCameraDeviceModel_ZV_E1}, {"ZV-E10M2", SDK::CrCameraDeviceModel_ZV_E10M2},
        {"PXW-Z300", SDK::CrCameraDeviceModel_PXW_Z300}, {"PXW-Z200", SDK::CrCameraDeviceModel_PXW_Z200},
        {"HXR-NX800", SDK::CrCameraDeviceModel_HXR_NX800}, {"DSC-RX1RM3", SDK::CrCameraDeviceModel_DSC_RX1RM3},
        {"ILX-LR1", SDK::CrCameraDeviceModel_ILX_LR1},
    };
    for (const auto& m : models)
        if (name == m.first) return m.second;
    return SDK::CrCameraDeviceModel_ILCE_7SM3;
}

// Builds camera info for a known IP when discovery doesn't find it. Use index -1 afterwards.
CB_API int cb_make_ip_camera(const char* ip, int ssh, const char* model)
{
    std::unique_lock<std::shared_mutex> l(g_lock);
    unsigned a, b, c, d;
    if (sscanf(ip, "%u.%u.%u.%u", &a, &b, &c, &d) != 4) return -1;
    if (g_ipInfo) { g_ipInfo->Release(); g_ipInfo = nullptr; }
    CrInt32u addr = a | (b << 8) | (c << 16) | (d << 24);
    CrInt8u mac[6] = {0};
    return (int)SDK::CreateCameraObjectInfoEthernetConnection(
        &g_ipInfo, modelFromName(model), addr, mac, ssh ? 1 : 0);
}

CB_API int cb_fingerprint(int index, char* out, int cap)
{
    std::shared_lock<std::shared_mutex> l(g_lock);
    auto* info = pickInfo(index);
    if (!info) return -1;
    char fp[512] = {0};
    CrInt32u len = 0;
    SDK::CrError err = SDK::GetFingerprint(info, fp, &len);
    if (err) return -(int)err;
    return copyOut(std::string(fp, len), out, cap);
}

// Starts connecting. Completion arrives as a CbConnected or CbError event.
CB_API int cb_connect(int index, const char* user, const char* pass, const char* fingerprint)
{
    std::unique_lock<std::shared_mutex> l(g_lock);
    auto* info = pickInfo(index);
    if (!info) return -1;
    if (g_handle) { SDK::Disconnect(g_handle); SDK::ReleaseDevice(g_handle); g_handle = 0; }
    bool ssh = info->GetSSHsupport() == SDK::CrSSHsupport_ON;
    std::string fp = fingerprint ? fingerprint : "";
    return (int)SDK::Connect(info, &g_callback, &g_handle, SDK::CrSdkControlMode_Remote, SDK::CrReconnecting_ON,
                             ssh ? user : nullptr, ssh ? pass : nullptr,
                             ssh ? fp.c_str() : nullptr, ssh ? (CrInt32u)fp.size() : 0);
}

CB_API int cb_disconnect()
{
    std::unique_lock<std::shared_mutex> l(g_lock);
    if (!g_handle) return 0;
    SDK::Disconnect(g_handle);
    SDK::ReleaseDevice(g_handle);
    g_handle = 0;
    g_connected = false;
    return 0;
}

// Reads properties as JSON. With n == 0 it reads everything the camera reports.
// Each item: {"c":code,"t":type,"e":enableFlag,"w":settable,"v":current,"s":"string","p":[candidates]}
CB_API int cb_get_props(const uint32_t* codes, int n, char* out, int cap)
{
    std::shared_lock<std::shared_mutex> l(g_lock);
    if (!g_handle) return -1;
    SDK::CrDeviceProperty* list = nullptr;
    CrInt32 count = 0;
    SDK::CrError err = n > 0
        ? SDK::GetSelectDeviceProperties(g_handle, (CrInt32u)n, const_cast<CrInt32u*>(codes), &list, &count)
        : SDK::GetDeviceProperties(g_handle, &list, &count);
    if (err) return -(int)err;

    std::string j = "[";
    for (CrInt32 i = 0; i < count; ++i) {
        auto& p = list[i];
        auto type = p.GetValueType();
        if (i) j += ',';
        j += "{\"c\":" + std::to_string(p.GetCode());
        j += ",\"t\":" + std::to_string((uint32_t)type);
        j += ",\"e\":" + std::to_string((int)p.GetPropertyEnableFlag());
        j += ",\"w\":" + std::string(p.IsSetEnableCurrentValue() ? "true" : "false");
        j += ",\"v\":" + std::to_string(p.GetCurrentValue());
        if (type == SDK::CrDataType_STR) {
            const CrInt16u* s = p.GetCurrentStr();
            // First element is the length; the text after it is null-terminated.
            std::string str = (s && s[0] > 0) ? utf8((const CrChar*)(s + 1)) : "";
            j += ",\"s\":";
            jsonStr(j, str);
        }
        // Sony's guide reads the magnifier's ratios from GetValues, not the settable list.
        bool preferAll = p.GetCode() == SDK::CrDevicePropertyCode::CrDeviceProperty_Focus_Magnifier_Setting;
        auto values = p.GetSetValueSize() > 0 && !preferAll ? decodeValues(type, p.GetSetValues(), p.GetSetValueSize())
                                                            : decodeValues(type, p.GetValues(), p.GetValueSize());
        j += ",\"p\":[";
        for (size_t k = 0; k < values.size(); ++k) {
            if (k) j += ',';
            j += std::to_string(values[k]);
        }
        j += "]}";
    }
    j += "]";
    SDK::ReleaseDeviceProperties(g_handle, list);
    return copyOut(j, out, cap);
}

CB_API int cb_set_prop(uint32_t code, uint64_t value, uint32_t dataType)
{
    std::shared_lock<std::shared_mutex> l(g_lock);
    if (!g_handle) return -1;
    SDK::CrDeviceProperty p;
    p.SetCode(code);
    p.SetCurrentValue(value);
    p.SetValueType((SDK::CrDataType)dataType);
    return (int)SDK::SetDeviceProperty(g_handle, &p);
}

CB_API int cb_command(uint32_t command, uint32_t param)
{
    std::shared_lock<std::shared_mutex> l(g_lock);
    if (!g_handle) return -1;
    return (int)SDK::SendCommand(g_handle, command, (SDK::CrCommandParam)param);
}

CB_API int cb_is_connected() { return g_connected ? 1 : 0; }

// Commands still waiting in the SDK. Sony: while these pile up, properties and live view stop updating.
CB_API int cb_queue_count()
{
    std::shared_lock<std::shared_mutex> l(g_lock);
    if (!g_handle) return 0;
    CrInt32u count = 0;
    return SDK::GetCommandQueueCount(g_handle, &count) ? 0 : (int)count;
}

template <typename T>
static void addFrames(std::string& j, int kind, const SDK::CrLiveViewProperty& p)
{
    auto* frames = (const T*)p.GetValue();
    for (CrInt32u i = 0; i < p.GetValueSize() / sizeof(T); ++i) {
        const T& f = frames[i];
        if (j.back() != '[') j += ',';
        j += "[" + std::to_string(kind) + "," + std::to_string((int)f.type) + "," + std::to_string((int)f.state) + ","
           + std::to_string(f.xNumerator) + "," + std::to_string(f.xDenominator) + ","
           + std::to_string(f.yNumerator) + "," + std::to_string(f.yDenominator) + ","
           + std::to_string(f.width) + "," + std::to_string(f.height) + "]";
    }
}

// Reads the boxes the camera is drawing on its own screen, as JSON:
// {"f":[[kind,type,state,x,xMax,y,yMax,width,height]...],"m":[x,xMax,y,yMax,width,height]}. x and y are frame centres.
CB_API int cb_focus_frames(char* out, int cap)
{
    std::shared_lock<std::shared_mutex> l(g_lock);
    if (!g_handle || !g_connected) return -1;
    SDK::CrLiveViewProperty* list = nullptr;
    CrInt32 count = 0;
    SDK::CrError err = SDK::GetLiveViewProperties(g_handle, &list, &count);
    if (err) return -(int)err;

    std::string j = "{\"f\":[";
    std::string mag;
    for (CrInt32 i = 0; i < count; ++i) {
        auto& p = list[i];
        if (!p.GetValue() || !p.GetValueSize()) continue;
        switch (p.GetFrameInfoType()) {
        case SDK::CrFrameInfoType_FocusFrameInfo: addFrames<SDK::CrFocusFrameInfo>(j, 1, p); break;
        case SDK::CrFrameInfoType_FaceFrameInfo: addFrames<SDK::CrFaceFrameInfo>(j, 2, p); break;
        case SDK::CrFrameInfoType_TrackingFrameInfo: addFrames<SDK::CrTrackingFrameInfo>(j, 3, p); break;
        case SDK::CrFrameInfoType_Magnifier_Position: {
            auto* m = (const SDK::CrMagPosInfo*)p.GetValue();
            mag = "[" + std::to_string(m->xNumerator) + "," + std::to_string(m->xDenominator) + ","
                + std::to_string(m->yNumerator) + "," + std::to_string(m->yDenominator) + ","
                + std::to_string(m->width) + "," + std::to_string(m->height) + "]";
            break;
        }
        default: break;
        }
    }
    j += "]";
    if (!mag.empty()) j += ",\"m\":" + mag;
    j += "}";
    SDK::ReleaseLiveViewProperties(g_handle, list);
    return copyOut(j, out, cap);
}

// Copies the newest live-view JPEG into out. Returns its size, 0 when the camera has no new
// frame yet, -1 when out is too small, or -error.
CB_API int cb_liveview(uint8_t* out, int cap, uint32_t* frameNo)
{
    std::shared_lock<std::shared_mutex> l(g_lock);
    if (!g_handle || !g_connected) return -2;
    SDK::CrImageInfo info;
    SDK::CrError err = SDK::GetLiveViewImageInfo(g_handle, &info);
    if (err) return -(int)err;
    CrInt32u size = info.GetBufferSize();
    if (size == 0) return 0;
    static thread_local std::vector<CrInt8u> buf;
    if (buf.size() < size) buf.resize(size);
    SDK::CrImageDataBlock block;
    block.SetSize(size);
    block.SetData(buf.data());
    err = SDK::GetLiveViewImage(g_handle, &block);
    if (err == SDK::CrWarning_Frame_NotUpdated) return 0;
    if (err) return -(int)err;
    CrInt32u n = block.GetImageSize();
    if (n == 0) return 0;
    if ((int)n > cap) return -1;
    memcpy(out, block.GetImageData(), n);
    if (frameNo) *frameNo = block.GetFrameNo();
    return (int)n;
}
