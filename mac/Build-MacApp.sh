#!/bin/bash
# Builds ComfyLauncher.app and ComfyLauncher-mac.zip. Runs on a Mac: the
# GitHub Actions job in .github/workflows/mac.yml, or by hand.
#
#   mac/Build-MacApp.sh <ComfyLauncher.exe> <version> <out folder>
#
# Wine and Wine Mono are downloaded and checked against the hashes below. To
# move to a new Wine, change WINE_VERSION and WINE_SHA256, then MONO_VERSION
# to the WINE_MONO_VERSION in that Wine's dlls/mscoree/mscoree_private.h, and
# MONO_SHA256.
#
# The zip is made with ditto. A zip made on Windows drops the executable
# flag from the script and from Wine, and the app does not open.

set -euo pipefail

WINE_VERSION="11.0_1"
WINE_SHA256="b50dc50ec7f41d58b115a6b685d4d1315ba3c797bd3aa0f49213f2703cb82388"
MONO_VERSION="10.4.1"
MONO_SHA256="a16606ef0724202e6a6848ece6e0cbba64d11e2f11aefe744af1d93c6d9f99bb"

EXE="$1"
VERSION="$2"
OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fetch() { # url sha256 file
    curl -fsSL -o "$3" "$1"
    echo "$2  $3" | shasum -a 256 -c -
}

fetch "https://github.com/Gcenx/macOS_Wine_builds/releases/download/$WINE_VERSION/wine-stable-$WINE_VERSION-osx64.tar.xz" "$WINE_SHA256" "$WORK/wine-stable.tar.xz"
fetch "https://github.com/madewokherd/wine-mono/releases/download/wine-mono-$MONO_VERSION/wine-mono-$MONO_VERSION-x86.tar.xz" "$MONO_SHA256" "$WORK/mono.tar.xz"

# Only Wine itself, from inside Wine Stable.app, with Wine Mono where Wine
# looks for it. Packed again as wine/..., the layout the app script expects.
mkdir -p "$WORK/unpack"
tar -xJf "$WORK/wine-stable.tar.xz" -C "$WORK/unpack"
SRC="$WORK/unpack/Wine Stable.app/Contents/Resources/wine"
[ -x "$SRC/bin/wine" ] || { echo "no bin/wine in the Wine download"; exit 1; }
mkdir -p "$WORK/pack"
mv "$SRC" "$WORK/pack/wine"
mkdir -p "$WORK/pack/wine/share/wine/mono"
tar -xJf "$WORK/mono.tar.xz" -C "$WORK/pack/wine/share/wine/mono"

APP="$WORK/ComfyLauncher.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
sed "s/VERSION/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
cp "$HERE/ComfyLauncher" "$APP/Contents/MacOS/ComfyLauncher"
chmod 755 "$APP/Contents/MacOS/ComfyLauncher"
cp "$EXE" "$APP/Contents/Resources/ComfyLauncher.exe"
printf '%s' "$WINE_VERSION" > "$APP/Contents/Resources/wine-version"
tar -cf - -C "$WORK/pack" wine | xz -T0 -6 > "$APP/Contents/Resources/wine.tar.xz"

# The icon, from the exe's .ico. An app with no icon still opens.
ICONSET="$WORK/ComfyLauncher.iconset"
mkdir -p "$ICONSET"
if sips -s format png "$HERE/../src/ComfyLauncher.ico" --out "$WORK/icon.png" >/dev/null 2>&1; then
    for s in 16 32 128 256 512; do
        sips -z $s $s "$WORK/icon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
        sips -z $((s * 2)) $((s * 2)) "$WORK/icon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/ComfyLauncher.icns" || echo "no icon: iconutil failed"
else
    echo "no icon: sips could not read src/ComfyLauncher.ico"
fi

mkdir -p "$OUT"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT/ComfyLauncher-mac.zip"
ls -l "$OUT/ComfyLauncher-mac.zip"
