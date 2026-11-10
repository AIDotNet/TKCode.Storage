#!/usr/bin/env bash
# Builds O_O for Linux (NativeAOT) and packs a .tar.gz and a .deb (Debian / Ubuntu).
#   build/linux/package.sh --arch x64|arm64 --version 1.2.3
# The .deb installs to /opt/o-o with a launcher /usr/bin/o-o, a desktop entry and icons. O_O runs on X11 or XWayland.
set -euo pipefail

ARCH=x64
VERSION=0.0.0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
case "$ARCH" in
  x64) DEB_ARCH=amd64 ;;
  arm64) DEB_ARCH=arm64 ;;
  *) echo "unsupported arch $ARCH" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"          # the O_O folder
HERE="$ROOT/build/linux"
OUT="$ROOT/build/out/linux-$ARCH"
PUBLISH="$OUT/publish"
DIST="$ROOT/build/out"

echo "==> Publishing NativeAOT ($ARCH)"
rm -rf "$OUT"
dotnet publish "$ROOT/src/Island.App" -c Release -r "linux-$ARCH" -p:PublishAot=true -p:Version="$VERSION" -o "$PUBLISH"
find "$PUBLISH" \( -name "*.pdb" -o -name "*.dbg" \) -delete

echo "==> tar.gz"
TAR="$DIST/O_O-$VERSION-linux-$ARCH.tar.gz"
tar -C "$OUT" --transform "s,^publish,O_O-$VERSION," -czf "$TAR" publish

echo "==> .deb"
PKG="$OUT/deb"
mkdir -p "$PKG/DEBIAN" "$PKG/opt/o-o" "$PKG/usr/bin" "$PKG/usr/share/applications"
cp -R "$PUBLISH/." "$PKG/opt/o-o/"
ln -s /opt/o-o/O_O "$PKG/usr/bin/o-o"
cp "$HERE/o-o.desktop" "$PKG/usr/share/applications/o-o.desktop"
for size in 16 32 64 128 256 512; do
  mkdir -p "$PKG/usr/share/icons/hicolor/${size}x${size}/apps"
  cp "$ROOT/assets/icon/icon_${size}.png" "$PKG/usr/share/icons/hicolor/${size}x${size}/apps/o-o.png"
done
INSTALLED_KB="$(du -sk "$PKG" | cut -f1)"
cat > "$PKG/DEBIAN/control" <<CONTROL
Package: o-o
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: AIDotNet <noreply@github.com>
Installed-Size: $INSTALLED_KB
Depends: libx11-6, libice6, libsm6, libfontconfig1, libxrandr2, libxi6, libxcursor1, libxext6
Recommends: libsecret-tools, playerctl, libnotify-bin
Section: utils
Priority: optional
Homepage: https://github.com/AIDotNet/TKCode.Storage
Description: O_O desktop Dynamic Island
 A thin desktop "Dynamic Island" whose content comes from plugins: AI usage, clipboard history,
 file shelf, calendar, mail, music, to-do and focus.
CONTROL
DEB="$DIST/o-o_${VERSION}_${DEB_ARCH}.deb"
dpkg-deb --build --root-owner-group "$PKG" "$DEB"

echo "==> Done"
ls -la "$TAR" "$DEB"
