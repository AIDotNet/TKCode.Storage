#!/usr/bin/env bash
# Builds O_O.app (NativeAOT) for one architecture, signs it with a Developer ID, packs a .dmg, and — when Apple
# credentials are given — notarizes and staples it.
#
#   build/macos/package.sh --arch arm64|x64 --version 1.2.3 [--identity "Developer ID Application: …"] [--notarize]
#
# Notarization reads APPLE_ID, APPLE_APP_SPECIFIC_PASSWORD and APPLE_TEAM_ID from the environment.
# Without --identity the app is signed ad hoc (local testing only; Gatekeeper will refuse it elsewhere).
set -euo pipefail

ARCH=arm64
VERSION=0.0.0
IDENTITY="-"
NOTARIZE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --identity) IDENTITY="$2"; shift 2 ;;
    --notarize) NOTARIZE=1; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"          # the O_O folder
HERE="$ROOT/build/macos"
OUT="$ROOT/build/out/macos-$ARCH"
APP="$OUT/O_O.app"
DMG="$ROOT/build/out/O_O-$VERSION-macos-$ARCH.dmg"
BUILD_NUMBER="${GITHUB_RUN_NUMBER:-$(date +%Y%m%d%H%M)}"

echo "==> Publishing NativeAOT ($ARCH)"
rm -rf "$OUT"
dotnet publish "$ROOT/src/Island.App" -c Release -r "osx-$ARCH" -p:PublishAot=true \
  -p:Version="$VERSION" -o "$OUT/publish"

echo "==> Assembling O_O.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$OUT/publish/O_O" "$APP/Contents/MacOS/"
cp "$OUT/publish/"*.dylib "$APP/Contents/MacOS/"
# Contents/MacOS may only hold code: the bundled plugins and notices are resources (App.BundledPluginDirectory).
cp -R "$OUT/publish/plugins" "$APP/Contents/Resources/plugins"
cp "$OUT/publish/"*.md "$APP/Contents/Resources/" 2>/dev/null || true
cp "$ROOT/assets/icon/O_O.icns" "$APP/Contents/Resources/O_O.icns"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD_NUMBER/" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"
find "$APP" -name ".DS_Store" -delete

echo "==> Signing ($IDENTITY)"
# Inside out: every library first, then the executable with the entitlements, then the bundle (no --deep).
SIGN=(codesign --force --sign "$IDENTITY" --options runtime)
[[ "$IDENTITY" != "-" ]] && SIGN+=(--timestamp)
for lib in "$APP/Contents/MacOS/"*.dylib; do
  "${SIGN[@]}" "$lib"
done
"${SIGN[@]}" --entitlements "$HERE/O_O.entitlements" "$APP/Contents/MacOS/O_O"
"${SIGN[@]}" --entitlements "$HERE/O_O.entitlements" "$APP"
codesign --verify --strict --verbose=2 "$APP"

echo "==> Building $(basename "$DMG")"
STAGE="$OUT/dmg"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"          # drag O_O onto Applications to install
hdiutil create -volname "O_O $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
if [[ "$IDENTITY" != "-" ]]; then
  codesign --force --sign "$IDENTITY" --timestamp "$DMG"
fi

if [[ "$NOTARIZE" == 1 ]]; then
  echo "==> Notarizing"
  : "${APPLE_ID:?APPLE_ID is required to notarize}"
  : "${APPLE_APP_SPECIFIC_PASSWORD:?APPLE_APP_SPECIFIC_PASSWORD is required to notarize}"
  : "${APPLE_TEAM_ID:?APPLE_TEAM_ID is required to notarize}"
  AUTH=(--apple-id "$APPLE_ID" --password "$APPLE_APP_SPECIFIC_PASSWORD" --team-id "$APPLE_TEAM_ID")
  RESULT="$(xcrun notarytool submit "$DMG" "${AUTH[@]}" --wait --timeout 30m --output-format json)"
  echo "$RESULT"
  STATUS="$(/usr/bin/plutil -extract status raw - <<< "$RESULT" 2>/dev/null || true)"
  if [[ "$STATUS" != "Accepted" ]]; then
    # Apple's report says which file and why (unsigned binary, no hardened runtime, ...).
    ID="$(/usr/bin/plutil -extract id raw - <<< "$RESULT" 2>/dev/null || true)"
    [[ -n "$ID" ]] && xcrun notarytool log "$ID" "${AUTH[@]}" || true
    echo "Notarization failed: ${STATUS:-no status}" >&2
    exit 1
  fi
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$DMG"
  spctl --assess --type open --context context:primary-signature --verbose=2 "$DMG"
fi

echo "==> Done: $DMG"
