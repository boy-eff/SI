#!/usr/bin/env bash
# Builds a self-contained SImulator AppImage for x86_64 Linux.
set -euo pipefail

RID="${1:-linux-x64}"

if [[ "$RID" != "linux-x64" ]]; then
    echo "Unsupported runtime: $RID (only linux-x64 is currently supported)" >&2
    exit 2
fi

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
VERSION="$(sed -n 's:.*<SImulatorAvaloniaVersion>\(.*\)</SImulatorAvaloniaVersion>.*:\1:p' "$REPO/Directory.Build.props")"
OUT="$REPO/bin/appimage/$RID"
APPDIR="$OUT/SImulator.AppDir"
PUBLISH="$APPDIR/usr/bin"
APPIMAGE="$REPO/bin/appimage/SImulator-$VERSION-x86_64.AppImage"
DOTNET="${DOTNET:-dotnet}"
FFMPEG="${FFMPEG:-$(command -v ffmpeg || true)}"
FFPROBE="${FFPROBE:-$(command -v ffprobe || true)}"
NUGET_PACKAGES="${NUGET_PACKAGES:-/tmp/simulator-appimage-nuget}"
NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-/tmp/simulator-appimage-http-cache}"

rm -rf "$OUT"
mkdir -p "$PUBLISH" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

NUGET_PACKAGES="$NUGET_PACKAGES" NUGET_HTTP_CACHE_PATH="$NUGET_HTTP_CACHE_PATH" \
    "$DOTNET" publish "$HERE/SImulator.Avalonia.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:UseAppHost=true -p:SignAssembly=false \
    -o "$PUBLISH"

if [[ -z "$FFMPEG" || -z "$FFPROBE" ]]; then
    echo "ffmpeg and ffprobe were not found. Set FFMPEG and FFPROBE to portable Linux binaries." >&2
    exit 4
fi

install -Dm755 "$FFMPEG" "$PUBLISH/ffmpeg"
install -Dm755 "$FFPROBE" "$PUBLISH/ffprobe"

install -Dm644 "$HERE/../SImulator/Images/logo.png" "$APPDIR/SImulator.png"
install -Dm644 "$HERE/../SImulator/Images/logo.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/SImulator.png"

cat > "$APPDIR/SImulator.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=SImulator
Comment=Offline SIGame simulator
Exec=SImulator %f
Icon=SImulator
Categories=Game;
Terminal=false
MimeType=application/x-sigame-package;
DESKTOP

cp "$APPDIR/SImulator.desktop" "$APPDIR/usr/share/applications/SImulator.desktop"

cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
APPDIR="$(dirname "$(readlink -f "$0")")"
export PATH="$APPDIR/usr/bin:$PATH"
exec "$APPDIR/usr/bin/SImulator" "$@"
APPRUN
chmod +x "$APPDIR/AppRun" "$PUBLISH/SImulator"

APPIMAGETOOL="${APPIMAGETOOL:-}"

if [[ -z "$APPIMAGETOOL" ]]; then
    APPIMAGETOOL="$(command -v appimagetool || true)"
fi

if [[ -z "$APPIMAGETOOL" ]]; then
    echo "appimagetool was not found. Set APPIMAGETOOL to its path." >&2
    echo "The complete AppDir is available at: $APPDIR" >&2
    exit 3
fi

rm -f "$APPIMAGE"
ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$APPIMAGE"
chmod +x "$APPIMAGE"

echo "Done: $APPIMAGE"
