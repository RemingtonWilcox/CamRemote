// A stand-in for Sony's SDK headers, used only to build the tests.
// The names match the ones the app uses; the numbers are made up and are NOT Sony's.
// tools/gen_names.py turns this into SdkNames.g.cs for the test build, just as it does with the real headers.

enum CrDevicePropertyCode : CrInt32u
{
    CrDeviceProperty_Undefined = 0,
    CrDeviceProperty_FocusMode = 0x0100u,
    CrDeviceProperty_IsoSensitivity,
    CrDeviceProperty_RecordingState,
    CrDeviceProperty_PriorityKeySettings,
    CrDeviceProperty_LiveView_Image_Quality,
    CrDeviceProperty_NearFar,
    CrDeviceProperty_TouchOperation,
    CrDeviceProperty_FunctionOfTouchOperation,
    CrDeviceProperty_RemoteTouchOperation,
    CrDeviceProperty_RemoteTouchOperationEnableStatus,
    CrDeviceProperty_CancelRemoteTouchOperationEnableStatus,
    CrDeviceProperty_FocusTrackingStatus,
    CrDeviceProperty_AF_Area_Position,
    CrDeviceProperty_Focus_Magnifier_Setting,
    CrDeviceProperty_FocusPositionCurrentValue,
    CrDeviceProperty_FocusPositionSetting,
    CrDeviceProperty_FocalDistanceInMeter,
    CrDeviceProperty_MeteredManualLevel,
    CrDeviceProperty_RecognitionTarget,
    CrDeviceProperty_SubjectRecognitionInAF,
    CrDeviceProperty_RightLeftEyeSelect,
    CrDeviceProperty_ShutterSpeed = 0x0200,
    CrDeviceProperty_FNumber,
    CrDeviceProperty_ExposureProgramMode,
    // Used by the two windows, so the Mac app can be compiled without the SDK too.
    CrDeviceProperty_FirstForTheWindows = 0x0300,
    CrDeviceProperty_AudioInputMasterLevel,
    CrDeviceProperty_AudioRecording,
    CrDeviceProperty_BatteryLevel,
    CrDeviceProperty_BatteryRemain,
    CrDeviceProperty_Colortemp,
    CrDeviceProperty_FocusArea,
    CrDeviceProperty_MediaSLOT1_RemainingTime,
    CrDeviceProperty_MediaSLOT1_Status,
    CrDeviceProperty_MediaSLOT2_RemainingTime,
    CrDeviceProperty_MediaSLOT2_Status,
    CrDeviceProperty_MonitorLUTSetting,
    CrDeviceProperty_MovieShootingMode,
    CrDeviceProperty_Movie_File_Format,
    CrDeviceProperty_Movie_HDMIOutputResolution,
    CrDeviceProperty_Movie_Recording_FrameRateSetting,
    CrDeviceProperty_Movie_Recording_Setting,
    CrDeviceProperty_WhiteBalance,
    CrDeviceProperty_WindNoiseReduct,
};

enum CrExposureProgram : CrInt32u
{
    CrExposure_M_Manual = 0x00000001,
    CrExposure_Movie_P = 0x00078050,
};

enum CrError
{
    CrError_None = 0,
    CrError_Connect = 0x8200,
    CrError_Connect_Unknown = CrError_Connect,
    CrError_Connect_Fake_One,
};
