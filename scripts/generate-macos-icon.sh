#!/bin/zsh
set -euo pipefail

ROOT="${0:A:h:h}"
SOURCE="$ROOT/src/VoiceInput/Assets/gujiguji.svg"
ICONSET="$ROOT/src/VoiceInputMac/Resources/gujiguji.iconset"
OUTPUT="$ROOT/src/VoiceInputMac/Resources/gujiguji.icns"
RASTER_DIR="$(mktemp -d /tmp/gujiguji-icon.XXXXXX)"
RASTER="$RASTER_DIR/${SOURCE:t}.png"

trap 'rm -rf "$RASTER_DIR"' EXIT

# ImageMagick's internal SVG renderer silently drops the waveform strokes on
# macOS. ImageIO (through sips) preserves both the complete Windows artwork and
# its transparent background before ImageMagick creates the ICNS sizes.
sips -z 1024 1024 -s format png "$SOURCE" --out "$RASTER" >/dev/null
[[ -f "$RASTER" ]] || { echo "Failed to rasterize $SOURCE" >&2; exit 1; }

rm -rf "$ICONSET"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  magick "$RASTER" -resize "${size}x${size}" "$ICONSET/icon_${size}x${size}.png"
  double=$((size * 2))
  magick "$RASTER" -resize "${double}x${double}" "$ICONSET/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$ICONSET" -o "$OUTPUT"
rm -rf "$ICONSET"
