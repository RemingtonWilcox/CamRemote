cmake_minimum_required(VERSION 3.20)
if(APPLE)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "14.1" CACHE STRING "Oldest macOS the SDK supports")
endif()
project(CamBridge LANGUAGES CXX)

# Sony Camera Remote SDK, unpacked from CrSDK_*_Win64.zip or CrSDK_*_Mac.zip (not committed; see README).
set(CRSDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../vendor/CrSDK/RemoteCli" CACHE PATH "RemoteCli folder of the Sony SDK")

add_library(CamBridge SHARED CamBridge.cpp)
set_target_properties(CamBridge PROPERTIES CXX_STANDARD 17 CXX_STANDARD_REQUIRED YES)
target_include_directories(CamBridge PRIVATE "${CRSDK_ROOT}/app/CRSDK")
if(APPLE)
    enable_language(OBJCXX)
    target_sources(CamBridge PRIVATE MacSupport.mm)
    set_source_files_properties(MacSupport.mm PROPERTIES COMPILE_OPTIONS "-fobjc-arc")
    set_target_properties(CamBridge PROPERTIES BUILD_RPATH "@loader_path" INSTALL_RPATH "@loader_path" CXX_VISIBILITY_PRESET hidden)
    target_link_libraries(CamBridge PRIVATE "${CRSDK_ROOT}/external/crsdk/libCr_Core.dylib"
        "-framework AVFoundation" "-framework CoreMedia" "-framework CoreVideo" "-framework CoreAudio"
        "-framework Foundation" "-framework IOKit" "-framework Security")
else()
    target_compile_definitions(CamBridge PRIVATE UNICODE _UNICODE)
    target_link_libraries(CamBridge PRIVATE "${CRSDK_ROOT}/external/crsdk/Cr_Core.lib")
endif()
if(MSVC)
    set_property(TARGET CamBridge PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()
