cmake_minimum_required(VERSION 3.20)
project(CamBridge LANGUAGES CXX)

# Sony Camera Remote SDK, unpacked from CrSDK_*_Win64.zip (not committed; see README).
set(CRSDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../vendor/CrSDK/RemoteCli" CACHE PATH "RemoteCli folder of the Sony SDK")

add_library(CamBridge SHARED CamBridge.cpp)
set_target_properties(CamBridge PROPERTIES CXX_STANDARD 17 CXX_STANDARD_REQUIRED YES)
target_compile_definitions(CamBridge PRIVATE UNICODE _UNICODE)
target_include_directories(CamBridge PRIVATE "${CRSDK_ROOT}/app/CRSDK")
target_link_libraries(CamBridge PRIVATE "${CRSDK_ROOT}/external/crsdk/Cr_Core.lib")
if(MSVC)
    set_property(TARGET CamBridge PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()
