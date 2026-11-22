#!/usr/bin/env bash
# Builds build/Lull.app (with the Lull.saver screen saver inside it) using only the Xcode Command Line Tools.
#   ./build.sh            build universal (arm64 + x86_64) app, falling back to native arch
#   ./build.sh run        build, then launch
#   ./build.sh install    build, then copy to /Applications
#   ./build.sh dist       build, then package build/Lull-<version>.dmg to share with friends
#
# Signing: ad-hoc by default. To sign with a self-signed certificate instead (keeps location
# permission and Launch at Login across rebuilds), create one in Keychain Access > Certificate
# Assistant > Create a Certificate (type: Code Signing) and run e.g.
#   LULL_SIGN_IDENTITY="Lull Self-Signed" ./build.sh dist
set -euo pipefail

cd "$(dirname "$0")"
HERE="$(pwd)"
WEB="$HERE/../web"
OUT="$HERE/build"
APP="$OUT/Lull.app"
MIN_OS="13.0"

if ! command -v swiftc >/dev/null 2>&1; then
  echo "error: swiftc not found. Install the Command Line Tools with: xcode-select --install" >&2
  exit 1
fi
if [ ! -f "$WEB/index.html" ]; then
  echo "error: $WEB/index.html not found" >&2
  exit 1
fi

SDK="$(xcrun --sdk macosx --show-sdk-path)"
SOURCES=("$HERE"/Sources/*.swift "$HERE"/Shared/*.swift)
FLAGS=(-O -sdk "$SDK" -swift-version 5
       -framework AppKit -framework WebKit -framework CoreLocation -framework ServiceManagement)
SAVER_SOURCES=("$HERE"/Saver/*.swift "$HERE"/Shared/*.swift)
SAVER_FLAGS=(-O -sdk "$SDK" -swift-version 5 -parse-as-library -emit-library -module-name LullSaver
             -framework AppKit -framework WebKit -framework ScreenSaver)
SAVER="$OUT/Lull.saver"

rm -rf "$OUT"
mkdir -p "$OUT/obj"

built=()
saver_built=()
for arch in arm64 x86_64; do
  echo "==> Compiling for $arch"
  if swiftc "${FLAGS[@]}" -target "$arch-apple-macos$MIN_OS" "${SOURCES[@]}" -o "$OUT/obj/Lull-$arch" &&
     swiftc "${SAVER_FLAGS[@]}" -target "$arch-apple-macos$MIN_OS" "${SAVER_SOURCES[@]}" -o "$OUT/obj/LullSaver-$arch"; then
    built+=("$OUT/obj/Lull-$arch")
    saver_built+=("$OUT/obj/LullSaver-$arch")
  else
    echo "warning: $arch build failed; continuing without it" >&2
  fi
done

if [ ${#built[@]} -eq 0 ]; then
  echo "error: compilation failed for every architecture (see errors above)" >&2
  exit 1
fi

echo "==> Assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
if [ ${#built[@]} -gt 1 ]; then
  lipo -create "${built[@]}" -output "$APP/Contents/MacOS/Lull"
else
  cp "${built[0]}" "$APP/Contents/MacOS/Lull"
fi
cp "$HERE/Info.plist" "$APP/Contents/Info.plist"
# only the built, self-contained scene goes in the app (not the dev sources next to it)
mkdir -p "$APP/Contents/Resources/web"
cp "$WEB/index.html" "$APP/Contents/Resources/web/index.html"

# Optional icon: put a 1024x1024 PNG at mac/icon.png
if [ -f "$HERE/icon.png" ] && command -v iconutil >/dev/null 2>&1; then
  echo "==> Building AppIcon.icns"
  ICONSET="$OUT/AppIcon.iconset"
  mkdir -p "$ICONSET"
  for size in 16 32 128 256 512; do
    sips -z $size $size "$HERE/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z $double $double "$HERE/icon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
else
  echo "    (no mac/icon.png; skipping icon)"
fi

SIGN_ID="${LULL_SIGN_IDENTITY:--}"

# The screen saver: a plug-in bundle for legacyScreenSaver, shipped inside the app, which installs it
# into ~/Library/Screen Savers from its menu (mac/Sources/ScreenSaverInstall.swift).
echo "==> Assembling $SAVER"
mkdir -p "$SAVER/Contents/MacOS" "$SAVER/Contents/Resources/web"
lipo -create "${saver_built[@]}" -output "$SAVER/Contents/MacOS/LullSaver"
cp "$HERE/Saver/Info.plist" "$SAVER/Contents/Info.plist"
cp "$WEB/index.html" "$SAVER/Contents/Resources/web/index.html"
cp "$HERE/Saver/thumbnail.png" "$HERE/Saver/thumbnail@2x.png" "$SAVER/Contents/Resources/"   # System Settings picker
[ -f "$APP/Contents/Resources/AppIcon.icns" ] && cp "$APP/Contents/Resources/AppIcon.icns" "$SAVER/Contents/Resources/"
codesign --force -s "$SIGN_ID" "$SAVER"
cp -R "$SAVER" "$APP/Contents/Resources/"

if [ "$SIGN_ID" = "-" ]; then echo "==> Ad-hoc signing"; else echo "==> Signing with \"$SIGN_ID\""; fi
codesign --force --deep -s "$SIGN_ID" "$APP"
codesign --verify --strict "$APP"
codesign --verify --strict "$APP/Contents/Resources/Lull.saver"

echo
echo "Built: $APP ($(lipo -archs "$APP/Contents/MacOS/Lull"))"
echo "Run it:      open \"$APP\""
echo "Install it:  ./build.sh install   (copies to /Applications; needed for Launch at Login)"
echo "Logs:        log stream --predicate 'process == \"Lull\"'"

case "${1:-}" in
  run)
    pkill -x Lull 2>/dev/null || true
    pkill -x Sunstill 2>/dev/null || true   # the app's old name: don't run two wallpapers at once
    open "$APP"
    ;;
  install)
    pkill -x Lull 2>/dev/null || true
    pkill -x Sunstill 2>/dev/null || true   # the app's old name: don't run two wallpapers at once
    rm -rf /Applications/Lull.app
    cp -R "$APP" /Applications/
    echo "Installed to /Applications/Lull.app"
    [ -d /Applications/Sunstill.app ] && echo "Note: the old /Applications/Sunstill.app is still there; delete it when you like (Lull already copied its settings)."
    open /Applications/Lull.app
    ;;
  dist)
    VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist" 2>/dev/null || echo 1.0)"
    STAGE="$OUT/dmg"
    DMG="$OUT/Lull-$VERSION.dmg"
    echo "==> Packaging $DMG"
    rm -rf "$STAGE" "$DMG"
    mkdir -p "$STAGE"
    cp -R "$APP" "$STAGE/"
    ln -s /Applications "$STAGE/Applications"
    cp "$HERE/dist/How to open Lull.txt" "$STAGE/"
    hdiutil create -volname "Lull $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
    rm -rf "$STAGE"
    echo
    echo "Share this file: $DMG ($(du -h "$DMG" | cut -f1))"
    echo "Your friends will need to approve it once (System Settings > Privacy & Security > Open Anyway);"
    echo "the steps are in \"How to open Lull.txt\" inside the disk image."
    [ -n "${CI:-}" ] || open -R "$DMG"   # reveal in Finder, except on CI
    ;;
esac
