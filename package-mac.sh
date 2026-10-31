#!/bin/bash
# Builds notarized downloads for both kinds of Mac: dist/CamRemote-Mac-AppleSilicon.dmg and dist/CamRemote-Mac-Intel.dmg.
# Run it in Terminal on the Mac itself, with a Developer ID in your Keychain. Once beforehand, save your notarization login:
#   xcrun notarytool store-credentials camremote-notary
set -euo pipefail
cd "$(dirname "$0")"

python3 -I tools/make_notices.py build/THIRD-PARTY-NOTICES.txt

for target in "osx-arm64 AppleSilicon" "osx-x64 Intel"; do
    set -- $target
    CAMREMOTE_RID="$1" ./build-mac.sh

    stage=build/dmg
    rm -rf "$stage"
    mkdir -p "$stage"
    cp -R dist/CamRemote.app "$stage/"
    ln -s /Applications "$stage/Applications"
    cp build/THIRD-PARTY-NOTICES.txt "$stage/"

    dmg="dist/CamRemote-Mac-$2.dmg"
    rm -f "$dmg"
    hdiutil create -quiet -volname CamRemote -srcfolder "$stage" -format UDZO "$dmg"
    codesign --force --sign "$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application:.*\)".*/\1/p' | head -1)" --timestamp "$dmg"
    xcrun notarytool submit "$dmg" --keychain-profile camremote-notary --wait
    xcrun stapler staple "$dmg"
done

cp vendor/CrSDK/SourceCodeOfOpenSourceSoftware/libusb.zip dist/libusb-source.zip
echo "Built the notarized downloads in dist/"
