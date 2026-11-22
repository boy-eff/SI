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
NUGET_PACKAGES="${NUGET_PACKAGES:-/tmp/simulator-appimage-nuget}"
NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-/tmp/simulator-appimage-http-cache}"

# ffmpeg/ffprobe: use FFMPEG/FFPROBE if both are set explicitly,
# otherwise download a portable static build (cached between runs).
# The system ffmpeg is intentionally NOT used: it is dynamically linked.
FFMPEG="${FFMPEG:-}"
FFPROBE="${FFPROBE:-}"
FFMPEG_URL="${FFMPEG_URL:-https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz}"
FFMPEG_CACHE="${FFMPEG_CACHE:-$HOME/.cache/simulator-build/ffmpeg}"

if [[ -z "$FFMPEG" || -z "$FFPROBE" ]]; then
    if [[ ! -x "$FFMPEG_CACHE/ffmpeg" || ! -x "$FFMPEG_CACHE/ffprobe" ]]; then
        echo "Downloading static ffmpeg..." >&2
        TMP="$(mktemp -d)"
        curl -fL "$FFMPEG_URL" -o "$TMP/ffmpeg.tar.xz"
        tar -xf "$TMP/ffmpeg.tar.xz" -C "$TMP"
        mkdir -p "$FFMPEG_CACHE"
        install -m755 "$TMP"/ffmpeg-*/ffmpeg  "$FFMPEG_CACHE/ffmpeg"
        install -m755 "$TMP"/ffmpeg-*/ffprobe "$FFMPEG_CACHE/ffprobe"
        install -m644 "$TMP"/ffmpeg-*/GPLv3.txt "$FFMPEG_CACHE/GPLv3.txt" 2>/dev/null || true
        rm -rf "$TMP"
    fi
    FFMPEG="$FFMPEG_CACHE/ffmpeg"
    FFPROBE="$FFMPEG_CACHE/ffprobe"
fi

rm -rf "$OUT"
mkdir -p "$PUBLISH" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

NUGET_PACKAGES="$NUGET_PACKAGES" NUGET_HTTP_CACHE_PATH="$NUGET_HTTP_CACHE_PATH" \
    "$DOTNET" publish "$HERE/SImulator.Avalonia.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:UseAppHost=true -p:SignAssembly=false \
    -o "$PUBLISH"

install -Dm755 "$FFMPEG" "$PUBLISH/ffmpeg"
install -Dm755 "$FFPROBE" "$PUBLISH/ffprobe"

# FFmpeg license (static builds are GPL because of libx264)
mkdir -p "$APPDIR/usr/share/licenses/ffmpeg"
LICENSE_SRC="$(dirname "$FFMPEG")/GPLv3.txt"
if [[ -f "$LICENSE_SRC" ]]; then
    install -Dm644 "$LICENSE_SRC" "$APPDIR/usr/share/licenses/ffmpeg/GPLv3.txt"
fi
echo "FFmpeg static build from $FFMPEG_URL" > "$APPDIR/usr/share/licenses/ffmpeg/SOURCE.txt"

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