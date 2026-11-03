#!/bin/bash
# Builds and signs the app. Run it in Terminal on the Mac itself, so signing can reach your Keychain.
#   DEVELOPMENT_TEAM=<your team id> ./build-iphone.sh
set -e
cd "$(dirname "$0")"
xcodegen generate
xcodebuild -project CamRemote.xcodeproj -scheme CamRemote -destination "generic/platform=iOS" \
    -allowProvisioningUpdates -derivedDataPath build build "$@"
echo "Built build/Build/Products/Debug-iphoneos/CamRemote.app"
