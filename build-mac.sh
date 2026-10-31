#!/bin/bash
# Builds dist/CamRemote.app. Needs the Sony SDK unpacked in vendor/CrSDK (see README).
# Signs with your Developer ID if you have one, or for this Mac only if you don't.
# Set CAMREMOTE_SIGN to a signing identity to choose one, or to - to skip the Developer ID.
set -euo pipefail
cd "$(dirname "$0")"

export PATH="$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin:$PATH"
for tool in cmake dotnet python3; do
    command -v "$tool" >/dev/null || { echo "$tool not found. See 'Build it yourself' in README.md."; exit 1; }
done

sdk=vendor/CrSDK/RemoteCli
# Sony ships the sample (with the SDK binaries) as RemoteCli.zip inside the SDK; unpack it if that hasn't been done.
if [ ! -d "$sdk" ] && [ -f vendor/CrSDK/RemoteCli.zip ]; then
    unzip -q vendor/CrSDK/RemoteCli.zip -d "$sdk"
fi
crsdk="$sdk/external/crsdk"
if [ ! -f "$crsdk/libCr_Core.dylib" ]; then
    echo "Sony's Mac SDK not found: expected $crsdk/libCr_Core.dylib. See 'Build it yourself' in README.md."
    exit 1
fi

# CAMREMOTE_RID picks osx-arm64 or osx-x64; package-mac.sh builds both.
case "$(uname -m)" in
    arm64) rid="${CAMREMOTE_RID:-osx-arm64}" ;;
    *) rid="${CAMREMOTE_RID:-osx-x64}" ;;
esac

python3 -I tools/gen_names.py "$sdk/app/CRSDK" app/CamRemote/SdkNames.g.cs
cmake -S native/CamBridge -B build/bridge-mac -DCMAKE_BUILD_TYPE=Release "-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64" >/dev/null
cmake --build build/bridge-mac --config Release
rm -rf build/mac-publish
dotnet publish app/CamRemote.Mac/CamRemote.Mac.csproj -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:DebugType=none -o build/mac-publish

app=dist/CamRemote.app
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks/CrAdapter" "$app/Contents/Resources"
cp tools/mac/Info.plist "$app/Contents/"
cp -R build/mac-publish/. "$app/Contents/MacOS/"
cp build/bridge-mac/libCamBridge.dylib "$crsdk"/*.dylib "$app/Contents/MacOS/"
cp "$crsdk"/CrAdapter/*.dylib "$app/Contents/Frameworks/CrAdapter/" # where Cr_Core looks for them inside an app

icons=build/CamRemote.iconset
rm -rf "$icons"
mkdir -p "$icons"
for size in 16 32 64 128 256 512; do
    sips -z "$size" "$size" app/CamRemote/phone-icon.png --out "$icons/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) app/CamRemote/phone-icon.png --out "$icons/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icons" -o "$app/Contents/Resources/CamRemote.icns"

identity="${CAMREMOTE_SIGN:-}"
if [ -z "$identity" ]; then
    identity="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application:.*\)".*/\1/p' | head -1)"
fi
if [ -z "$identity" ] || [ "$identity" = - ]; then
    identity=-
    library=()
    options=()
else
    library=(--options runtime --timestamp)
    options=("${library[@]}" --entitlements tools/mac/CamRemote.entitlements)
fi
# Inside out: every library first, then the app around them.
find "$app/Contents" -type f -name '*.dylib' -print0 | while IFS= read -r -d '' library_file; do
    codesign --force --sign "$identity" "${library[@]+"${library[@]}"}" "$library_file"
done
codesign --force --sign "$identity" "${options[@]+"${options[@]}"}" "$app"

echo "Built $app (signed with: $identity)"
