#!/usr/bin/env bash
# Build the Linux AppImage.
#
# Needs the RecompOne checkout (scripts/setup_tools.sh) and the .NET 10 SDK. It
# does NOT need the disc: the launcher carries the inputs to a build and makes
# the game on the player's machine, which is what lets this run in CI at all.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${OUT:-$ROOT/dist}"
APPDIR="$OUT/Verdite2.AppDir"
VERSION="${VERSION:-$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/Verdite2.Launcher/Verdite2.Launcher.csproj")}"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

echo "==> publishing linux-x64"
dotnet publish "$ROOT/Verdite2.Launcher/Verdite2.Launcher.csproj" \
    -c Release -r linux-x64 --self-contained \
    -p:DebugType=none -p:DebugSymbols=false \
    -o "$APPDIR/usr/bin"

# AppRun, not a symlink to the binary: the working directory an AppImage starts
# in is wherever the user invoked it, and $APPDIR is a read-only mount, so the
# launcher's own data-directory handling has to be the thing that decides where
# files go. It does -- this just execs it.
cat > "$APPDIR/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Verdite2" "$@"
RUN
chmod +x "$APPDIR/AppRun"

cp "$ROOT/packaging/shared/verdite2.desktop" "$APPDIR/usr/share/applications/verdite2.desktop"
cp "$ROOT/packaging/shared/verdite2.desktop" "$APPDIR/verdite2.desktop"
cp "$ROOT/packaging/shared/verdite2.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/verdite2.png"
cp "$ROOT/packaging/shared/verdite2.png" "$APPDIR/verdite2.png"

echo "==> appimagetool"
TOOL="${APPIMAGETOOL:-}"
if [ -z "$TOOL" ]; then
    TOOL="$OUT/appimagetool"
    if [ ! -x "$TOOL" ]; then
        curl -fsSL -o "$TOOL" \
            https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
        chmod +x "$TOOL"
    fi
fi

# ARCH is read from the environment by appimagetool and is not inferred.
ARCH=x86_64 "$TOOL" --no-appstream "$APPDIR" "$OUT/Verdite2-$VERSION-x86_64.AppImage"

echo "done: $OUT/Verdite2-$VERSION-x86_64.AppImage"
