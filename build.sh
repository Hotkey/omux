#!/bin/zsh
# Builds omux.app into ./build. Run ./build.sh && open build/omux.app
set -e
setopt null_glob
cd "$(dirname "$0")"

swift build -c release 2>&1 | grep -E "error|warning: unre|Compiling|Build complete" | tail -20

APP=build/omux.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/omux "$APP/Contents/MacOS/omux"
cp Info.plist "$APP/Contents/Info.plist"
cp Resources/omux.icns "$APP/Contents/Resources/"
for bundle in .build/release/*.bundle; do
  [ -e "$bundle" ] && cp -R "$bundle" "$APP/Contents/Resources/"
done
# A stable signing identity keeps macOS from re-asking permissions after each rebuild.
IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null | grep "Apple Development" | head -1 | awk '{print $2}')
codesign --force --sign "${IDENTITY:--}" "$APP" 2>&1 | grep -v "replacing existing signature" || true
# Install to /Applications so Finder's "Open in omux" service and Dock stay registered.
rm -rf /Applications/omux.app
cp -R "$APP" /Applications/omux.app
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f /Applications/omux.app
/System/Library/CoreServices/pbs -flush 2>/dev/null || true
echo "Built and installed /Applications/omux.app ($(du -sh "$APP" | cut -f1))"
