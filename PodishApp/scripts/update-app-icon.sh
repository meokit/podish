#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SRC="${1:-$ROOT_DIR/icon.png}"
DST_DIR="${2:-$ROOT_DIR/PodishApp/Assets.xcassets/AppIcon.appiconset}"
ICON_BG="${ICON_BG:-#000000}"

if [ ! -f "$SRC" ]; then
  echo "error: source image not found: $SRC" >&2
  exit 1
fi

if command -v magick >/dev/null 2>&1; then
  IM_CMD=(magick)
elif command -v convert >/dev/null 2>&1; then
  IM_CMD=(convert)
else
  echo "error: neither 'magick' nor 'convert' found" >&2
  exit 1
fi

mkdir -p "$DST_DIR"

render_icon() {
  local src="$1"
  local size="$2"
  local out="$3"
  "${IM_CMD[@]}" "$src" \
    -gravity center \
    -resize "${size}x${size}^" \
    -extent "${size}x${size}" \
    -background "$ICON_BG" \
    -alpha remove -alpha off \
    -colorspace sRGB \
    "PNG24:$out"
}

BASE_1024="$DST_DIR/icon-1024.png"
render_icon "$SRC" 1024 "$BASE_1024"

# iPhone
render_icon "$BASE_1024" 40  "$DST_DIR/icon-iphone-20@2x.png"
render_icon "$BASE_1024" 60  "$DST_DIR/icon-iphone-20@3x.png"
render_icon "$BASE_1024" 58  "$DST_DIR/icon-iphone-29@2x.png"
render_icon "$BASE_1024" 87  "$DST_DIR/icon-iphone-29@3x.png"
render_icon "$BASE_1024" 80  "$DST_DIR/icon-iphone-40@2x.png"
render_icon "$BASE_1024" 120 "$DST_DIR/icon-iphone-40@3x.png"
render_icon "$BASE_1024" 120 "$DST_DIR/icon-iphone-60@2x.png"
render_icon "$BASE_1024" 180 "$DST_DIR/icon-iphone-60@3x.png"

# iPad
render_icon "$BASE_1024" 20  "$DST_DIR/icon-ipad-20@1x.png"
render_icon "$BASE_1024" 40  "$DST_DIR/icon-ipad-20@2x.png"
render_icon "$BASE_1024" 29  "$DST_DIR/icon-ipad-29@1x.png"
render_icon "$BASE_1024" 58  "$DST_DIR/icon-ipad-29@2x.png"
render_icon "$BASE_1024" 40  "$DST_DIR/icon-ipad-40@1x.png"
render_icon "$BASE_1024" 80  "$DST_DIR/icon-ipad-40@2x.png"
render_icon "$BASE_1024" 76  "$DST_DIR/icon-ipad-76@1x.png"
render_icon "$BASE_1024" 152 "$DST_DIR/icon-ipad-76@2x.png"
render_icon "$BASE_1024" 167 "$DST_DIR/icon-ipad-83.5@2x.png"

# macOS
render_icon "$BASE_1024" 16   "$DST_DIR/icon-mac-16@1x.png"
render_icon "$BASE_1024" 32   "$DST_DIR/icon-mac-16@2x.png"
render_icon "$BASE_1024" 32   "$DST_DIR/icon-mac-32@1x.png"
render_icon "$BASE_1024" 64   "$DST_DIR/icon-mac-32@2x.png"
render_icon "$BASE_1024" 128  "$DST_DIR/icon-mac-128@1x.png"
render_icon "$BASE_1024" 256  "$DST_DIR/icon-mac-128@2x.png"
render_icon "$BASE_1024" 256  "$DST_DIR/icon-mac-256@1x.png"
render_icon "$BASE_1024" 512  "$DST_DIR/icon-mac-256@2x.png"
render_icon "$BASE_1024" 512  "$DST_DIR/icon-mac-512@1x.png"
render_icon "$BASE_1024" 1024 "$DST_DIR/icon-mac-512@2x.png"

cat > "$DST_DIR/Contents.json" << 'JSON'
{
  "images" : [
    { "idiom" : "iphone", "size" : "20x20", "scale" : "2x", "filename" : "icon-iphone-20@2x.png" },
    { "idiom" : "iphone", "size" : "20x20", "scale" : "3x", "filename" : "icon-iphone-20@3x.png" },
    { "idiom" : "iphone", "size" : "29x29", "scale" : "2x", "filename" : "icon-iphone-29@2x.png" },
    { "idiom" : "iphone", "size" : "29x29", "scale" : "3x", "filename" : "icon-iphone-29@3x.png" },
    { "idiom" : "iphone", "size" : "40x40", "scale" : "2x", "filename" : "icon-iphone-40@2x.png" },
    { "idiom" : "iphone", "size" : "40x40", "scale" : "3x", "filename" : "icon-iphone-40@3x.png" },
    { "idiom" : "iphone", "size" : "60x60", "scale" : "2x", "filename" : "icon-iphone-60@2x.png" },
    { "idiom" : "iphone", "size" : "60x60", "scale" : "3x", "filename" : "icon-iphone-60@3x.png" },

    { "idiom" : "ipad", "size" : "20x20", "scale" : "1x", "filename" : "icon-ipad-20@1x.png" },
    { "idiom" : "ipad", "size" : "20x20", "scale" : "2x", "filename" : "icon-ipad-20@2x.png" },
    { "idiom" : "ipad", "size" : "29x29", "scale" : "1x", "filename" : "icon-ipad-29@1x.png" },
    { "idiom" : "ipad", "size" : "29x29", "scale" : "2x", "filename" : "icon-ipad-29@2x.png" },
    { "idiom" : "ipad", "size" : "40x40", "scale" : "1x", "filename" : "icon-ipad-40@1x.png" },
    { "idiom" : "ipad", "size" : "40x40", "scale" : "2x", "filename" : "icon-ipad-40@2x.png" },
    { "idiom" : "ipad", "size" : "76x76", "scale" : "1x", "filename" : "icon-ipad-76@1x.png" },
    { "idiom" : "ipad", "size" : "76x76", "scale" : "2x", "filename" : "icon-ipad-76@2x.png" },
    { "idiom" : "ipad", "size" : "83.5x83.5", "scale" : "2x", "filename" : "icon-ipad-83.5@2x.png" },

    { "idiom" : "mac", "size" : "16x16", "scale" : "1x", "filename" : "icon-mac-16@1x.png" },
    { "idiom" : "mac", "size" : "16x16", "scale" : "2x", "filename" : "icon-mac-16@2x.png" },
    { "idiom" : "mac", "size" : "32x32", "scale" : "1x", "filename" : "icon-mac-32@1x.png" },
    { "idiom" : "mac", "size" : "32x32", "scale" : "2x", "filename" : "icon-mac-32@2x.png" },
    { "idiom" : "mac", "size" : "128x128", "scale" : "1x", "filename" : "icon-mac-128@1x.png" },
    { "idiom" : "mac", "size" : "128x128", "scale" : "2x", "filename" : "icon-mac-128@2x.png" },
    { "idiom" : "mac", "size" : "256x256", "scale" : "1x", "filename" : "icon-mac-256@1x.png" },
    { "idiom" : "mac", "size" : "256x256", "scale" : "2x", "filename" : "icon-mac-256@2x.png" },
    { "idiom" : "mac", "size" : "512x512", "scale" : "1x", "filename" : "icon-mac-512@1x.png" },
    { "idiom" : "mac", "size" : "512x512", "scale" : "2x", "filename" : "icon-mac-512@2x.png" },

    { "idiom" : "ios-marketing", "size" : "1024x1024", "scale" : "1x", "filename" : "icon-1024.png" }
  ],
  "info" : {
    "author" : "xcode",
    "version" : 1
  }
}
JSON

echo "Updated AppIcon set: $DST_DIR"
echo "Background used for alpha removal: $ICON_BG"
