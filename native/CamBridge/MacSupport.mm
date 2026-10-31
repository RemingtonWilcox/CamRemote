// The Mac's side of what Windows gets from WinRT and WASAPI: Keychain, staying awake, capture cards and their audio.
// Build with ARC. Strings cross the boundary as UTF-8; the device list comes back as JSON.

#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <Foundation/Foundation.h>
#import <IOKit/pwr_mgt/IOPMLib.h>
#import <Security/Security.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>

#define CB_API extern "C" __attribute__((visibility("default")))

static NSString* str(const char* s) { return [NSString stringWithUTF8String:s ? s : ""]; }

static int copyOut(NSString* s, char* out, int cap)
{
    const char* utf8 = s.UTF8String;
    int n = (int)strlen(utf8);
    if (n > cap) return -1;
    memcpy(out, utf8, n);
    return n;
}

// ---- Keychain ----

static NSDictionary* secretQuery(const char* service, const char* account)
{
    return @{
        (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService: str(service),
        (__bridge id)kSecAttrAccount: str(account),
    };
}

CB_API int cb_secret_get(const char* service, const char* account, char* out, int cap)
{
    NSMutableDictionary* query = [secretQuery(service, account) mutableCopy];
    query[(__bridge id)kSecReturnData] = @YES;
    CFTypeRef found = nullptr;
    if (SecItemCopyMatching((__bridge CFDictionaryRef)query, &found) != errSecSuccess) return -1;
    NSData* data = (__bridge_transfer NSData*)found;
    if ((int)data.length > cap) return -1;
    memcpy(out, data.bytes, data.length);
    return (int)data.length;
}

CB_API int cb_secret_set(const char* service, const char* account, const char* password)
{
    NSDictionary* query = secretQuery(service, account);
    SecItemDelete((__bridge CFDictionaryRef)query);
    if (!password || !*password) return 0;
    NSMutableDictionary* item = [query mutableCopy];
    item[(__bridge id)kSecValueData] = [NSData dataWithBytes:password length:strlen(password)];
    return (int)SecItemAdd((__bridge CFDictionaryRef)item, nullptr);
}

// ---- Staying awake ----

static IOPMAssertionID g_awake = kIOPMNullAssertionID;

// 0: let the Mac sleep. 1: keep it running. 2: keep the display on too.
CB_API void cb_awake(int level)
{
    if (g_awake != kIOPMNullAssertionID) {
        IOPMAssertionRelease(g_awake);
        g_awake = kIOPMNullAssertionID;
    }
    if (level == 0) return;
    CFStringRef kind = level == 2 ? kIOPMAssertionTypePreventUserIdleDisplaySleep : kIOPMAssertionTypePreventUserIdleSystemSleep;
    IOPMAssertionCreateWithName(kind, kIOPMAssertionLevelOn, CFSTR("CamRemote is connected to a camera"), &g_awake);
}

// ---- Capture cards ----

typedef void (*CbFrameFn)(const uint8_t* bgra, int width, int height, int stride);

static NSArray<AVCaptureDevice*>* devices(AVMediaType media)
{
    AVCaptureDeviceDiscoverySession* s = [AVCaptureDeviceDiscoverySession
        discoverySessionWithDeviceTypes:media == AVMediaTypeVideo
            ? @[AVCaptureDeviceTypeExternal, AVCaptureDeviceTypeBuiltInWideAngleCamera]
            : @[AVCaptureDeviceTypeExternal, AVCaptureDeviceTypeMicrophone]
                              mediaType:media
                               position:AVCaptureDevicePositionUnspecified];
    return s.devices;
}

CB_API int cb_cap_list(char* out, int cap)
{
    NSMutableArray* list = [NSMutableArray array];
    for (AVCaptureDevice* d in devices(AVMediaTypeVideo))
        [list addObject:@{@"id": d.uniqueID, @"name": d.localizedName}];
    NSData* json = [NSJSONSerialization dataWithJSONObject:list options:0 error:nil];
    return copyOut([[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding], out, cap);
}

@interface CbCapture : NSObject <AVCaptureVideoDataOutputSampleBufferDelegate, AVCaptureAudioDataOutputSampleBufferDelegate> {
@public
    CbFrameFn frame;
    std::atomic<float> peakL, peakR;
}
@end

@implementation CbCapture
- (void)captureOutput:(AVCaptureOutput*)output didOutputSampleBuffer:(CMSampleBufferRef)sample fromConnection:(AVCaptureConnection*)connection
{
    if ([output isKindOfClass:[AVCaptureVideoDataOutput class]]) {
        CVImageBufferRef image = CMSampleBufferGetImageBuffer(sample);
        if (!image || !frame) return;
        CVPixelBufferLockBaseAddress(image, kCVPixelBufferLock_ReadOnly);
        frame((const uint8_t*)CVPixelBufferGetBaseAddress(image), (int)CVPixelBufferGetWidth(image),
              (int)CVPixelBufferGetHeight(image), (int)CVPixelBufferGetBytesPerRow(image));
        CVPixelBufferUnlockBaseAddress(image, kCVPixelBufferLock_ReadOnly);
        return;
    }

    // Audio arrives as interleaved 32-bit floats, as asked for in cb_audio_start.
    CMFormatDescriptionRef format = CMSampleBufferGetFormatDescription(sample);
    const AudioStreamBasicDescription* asbd = format ? CMAudioFormatDescriptionGetStreamBasicDescription(format) : nullptr;
    CMBlockBufferRef block = CMSampleBufferGetDataBuffer(sample);
    if (!asbd || !block || asbd->mChannelsPerFrame == 0) return;
    size_t length = 0;
    char* data = nullptr;
    if (CMBlockBufferGetDataPointer(block, 0, nullptr, &length, &data) != kCMBlockBufferNoErr) return;
    const float* samples = (const float*)data;
    size_t count = length / sizeof(float);
    int channels = asbd->mChannelsPerFrame;
    float l = 0, r = 0;
    for (size_t i = 0; i < count; ++i) {
        float v = fabsf(samples[i]);
        if (i % channels == 0) l = std::max(l, v);
        else if (i % channels == 1) r = std::max(r, v);
    }
    if (channels == 1) r = l;
    if (l > peakL.load()) peakL = l;
    if (r > peakR.load()) peakR = r;
}
@end

static AVCaptureSession* g_video = nil;
static AVCaptureSession* g_audio = nil;
static CbCapture* g_videoSink = nil;
static CbCapture* g_audioSink = nil;
static dispatch_queue_t g_videoQueue = nil;
static dispatch_queue_t g_audioQueue = nil;
static char g_format[128];

static bool allowed(AVMediaType media)
{
    AVAuthorizationStatus status = [AVCaptureDevice authorizationStatusForMediaType:media];
    if (status == AVAuthorizationStatusAuthorized) return true;
    if (status != AVAuthorizationStatusNotDetermined) return false;
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    __block BOOL granted = NO;
    [AVCaptureDevice requestAccessForMediaType:media completionHandler:^(BOOL ok) {
        granted = ok;
        dispatch_semaphore_signal(done);
    }];
    dispatch_semaphore_wait(done, DISPATCH_TIME_FOREVER);
    return granted;
}

CB_API void cb_cap_stop()
{
    [g_video stopRunning];
    g_video = nil;
    if (g_videoQueue) dispatch_sync(g_videoQueue, ^{}); // no more frames after this returns
    g_videoSink = nil;
    g_format[0] = 0;
}

// 0 on success, 1 for no permission, 2 for no such device, 3 if the device wouldn't start.
CB_API int cb_cap_start(const char* uniqueId, CbFrameFn frame)
{
    cb_cap_stop();
    if (!allowed(AVMediaTypeVideo)) return 1;
    AVCaptureDevice* device = [AVCaptureDevice deviceWithUniqueID:str(uniqueId)];
    if (!device) return 2;

    // Largest picture first, then the highest frame rate.
    AVCaptureDeviceFormat* best = nil;
    AVFrameRateRange* bestRate = nil;
    int64_t bestArea = 0;
    for (AVCaptureDeviceFormat* f in device.formats) {
        CMVideoDimensions d = CMVideoFormatDescriptionGetDimensions(f.formatDescription);
        int64_t area = (int64_t)d.width * d.height;
        for (AVFrameRateRange* r in f.videoSupportedFrameRateRanges)
            if (area > bestArea || (area == bestArea && r.maxFrameRate > bestRate.maxFrameRate)) {
                best = f;
                bestRate = r;
                bestArea = area;
            }
    }

    NSError* error = nil;
    AVCaptureDeviceInput* input = [AVCaptureDeviceInput deviceInputWithDevice:device error:&error];
    if (!input) return 3;
    AVCaptureSession* session = [AVCaptureSession new];
    [session beginConfiguration];
    if (![session canAddInput:input]) return 3;
    [session addInput:input];
    if (best && [device lockForConfiguration:nil]) {
        device.activeFormat = best;
        device.activeVideoMinFrameDuration = bestRate.minFrameDuration;
        device.activeVideoMaxFrameDuration = bestRate.minFrameDuration;
        [device unlockForConfiguration];
        CMVideoDimensions d = CMVideoFormatDescriptionGetDimensions(best.formatDescription);
        snprintf(g_format, sizeof g_format, "%d×%d @ %.4g", d.width, d.height, bestRate.maxFrameRate);
    }

    g_videoSink = [CbCapture new];
    g_videoSink->frame = frame;
    if (!g_videoQueue) g_videoQueue = dispatch_queue_create("CamRemote.video", DISPATCH_QUEUE_SERIAL);
    AVCaptureVideoDataOutput* output = [AVCaptureVideoDataOutput new];
    output.alwaysDiscardsLateVideoFrames = YES;
    output.videoSettings = @{(id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA)};
    [output setSampleBufferDelegate:g_videoSink queue:g_videoQueue];
    if (![session canAddOutput:output]) return 3;
    [session addOutput:output];
    [session commitConfiguration];
    [session startRunning];
    g_video = session;
    return session.running ? 0 : 3;
}

CB_API int cb_cap_format(char* out, int cap) { return copyOut(str(g_format), out, cap); }

// ---- Capture card audio ----

CB_API void cb_audio_stop()
{
    [g_audio stopRunning];
    g_audio = nil;
    if (g_audioQueue) dispatch_sync(g_audioQueue, ^{});
    g_audioSink = nil;
}

// Meters the audio input whose name contains match. 0 on success, 1 for no permission, 2 for none found, 3 if it wouldn't start.
CB_API int cb_audio_start(const char* match)
{
    cb_audio_stop();
    if (!allowed(AVMediaTypeAudio)) return 1;
    NSString* wanted = str(match);
    AVCaptureDevice* device = nil;
    for (AVCaptureDevice* d in devices(AVMediaTypeAudio))
        if ([d.localizedName rangeOfString:wanted options:NSCaseInsensitiveSearch].location != NSNotFound) {
            device = d;
            break;
        }
    if (!device) return 2;

    AVCaptureDeviceInput* input = [AVCaptureDeviceInput deviceInputWithDevice:device error:nil];
    if (!input) return 3;
    AVCaptureSession* session = [AVCaptureSession new];
    if (![session canAddInput:input]) return 3;
    [session addInput:input];
    g_audioSink = [CbCapture new];
    if (!g_audioQueue) g_audioQueue = dispatch_queue_create("CamRemote.audio", DISPATCH_QUEUE_SERIAL);
    AVCaptureAudioDataOutput* output = [AVCaptureAudioDataOutput new];
    output.audioSettings = @{
        AVFormatIDKey: @(kAudioFormatLinearPCM),
        AVLinearPCMBitDepthKey: @32,
        AVLinearPCMIsFloatKey: @YES,
        AVLinearPCMIsNonInterleaved: @NO,
        AVLinearPCMIsBigEndianKey: @NO,
    };
    [output setSampleBufferDelegate:g_audioSink queue:g_audioQueue];
    if (![session canAddOutput:output]) return 3;
    [session addOutput:output];
    [session startRunning];
    g_audio = session;
    return session.running ? 0 : 3;
}

// Highest samples since the last call, per channel.
CB_API void cb_audio_peaks(float* left, float* right)
{
    CbCapture* sink = g_audioSink;
    *left = sink ? sink->peakL.exchange(0) : 0;
    *right = sink ? sink->peakR.exchange(0) : 0;
}
