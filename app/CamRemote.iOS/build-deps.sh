#!/bin/bash
# Builds libssh2 and mbedTLS for iPhone from their official releases, and fetches Microsoft's ready-made
# ONNX Runtime for iPhone, into deps/. Run once on a Mac.
set -e
MBEDTLS=3.6.7
LIBSSH2=1.11.1
ONNXRUNTIME=1.24.2
ONNXRUNTIME_SHA256=f7100a992d2a8135168c8afd831e6a58b465349101982aa58b3e11d36e600b54 # from its Swift package
ROOT="$(cd "$(dirname "$0")" && pwd)/deps"
mkdir -p "$ROOT/src"
cd "$ROOT/src"
curl -fsSL -o mbedtls.tar.bz2 "https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-$MBEDTLS/mbedtls-$MBEDTLS.tar.bz2"
curl -fsSL -o libssh2.tar.gz "https://github.com/libssh2/libssh2/releases/download/libssh2-$LIBSSH2/libssh2-$LIBSSH2.tar.gz"
tar xjf mbedtls.tar.bz2
tar xzf libssh2.tar.gz
IOS=(-DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT=iphoneos -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=16.0
     -DCMAKE_BUILD_TYPE=Release "-DCMAKE_INSTALL_PREFIX=$ROOT/ios")
cmake -S "mbedtls-$MBEDTLS" -B build-mbedtls "${IOS[@]}" -DENABLE_TESTING=OFF -DENABLE_PROGRAMS=OFF \
    -DUSE_SHARED_MBEDTLS_LIBRARY=OFF -DUSE_STATIC_MBEDTLS_LIBRARY=ON
cmake --build build-mbedtls -j8 --target install
cmake -S "libssh2-$LIBSSH2" -B build-libssh2 "${IOS[@]}" "-DCMAKE_PREFIX_PATH=$ROOT/ios" -DCRYPTO_BACKEND=mbedTLS \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON -DBUILD_EXAMPLES=OFF -DBUILD_TESTING=OFF -DENABLE_ZLIB_COMPRESSION=OFF \
    "-DMBEDTLS_INCLUDE_DIR=$ROOT/ios/include" "-DMBEDCRYPTO_LIBRARY=$ROOT/ios/lib/libmbedcrypto.a"
cmake --build build-libssh2 -j8 --target install
curl -fsSL -o onnxruntime.zip "https://download.onnxruntime.ai/pod-archive-onnxruntime-c-$ONNXRUNTIME.zip"
echo "$ONNXRUNTIME_SHA256  onnxruntime.zip" | shasum -a 256 -c -
rm -rf onnxruntime "$ROOT/onnxruntime.xcframework"
unzip -q onnxruntime.zip -d onnxruntime
mv onnxruntime/onnxruntime.xcframework "$ROOT/"
