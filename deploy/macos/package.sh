#!/usr/bin/env bash
# macOS packages for Karvon: Karvon Studio.app in a .dmg (drag it to Applications), and the Homebrew cask that installs
# it. Karvon is published self-contained, so it needs no .NET runtime; working on solutions still needs the .NET SDK.
# Not notarized (no Apple Developer account): the app is signed ad hoc, which Apple Silicon needs to run it at all, and
# the first launch asks to confirm it (System Settings > Privacy & Security > Open Anyway). Runs on macOS:
#   dotnet publish src/Karvon -c Release -r osx-arm64 --self-contained -o out -p:Version=1.2.3
#   deploy/macos/package.sh out 1.2.3 arm64 dist
set -euo pipefail

SRC=$(cd "$1" && pwd)
VERSION=$2
ARCH=$3
DIST=$(mkdir -p "$4" && cd "$4" && pwd)
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

if [[ ! -x "$SRC/Karvon" ]]; then
  echo "$SRC/Karvon not found: publish Karvon first (see the header of this script)" >&2
  exit 1
fi

# ---- Karvon Studio.app ----
APP="$WORK/Karvon Studio.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -a "$SRC/." "$APP/Contents/MacOS/"
chmod 755 "$APP/Contents/MacOS/Karvon"
# How it updates itself: its bundle replaced by the next .dmg's.
echo dmg > "$APP/Contents/MacOS/karvon-distribution"

# Its icon at every size macOS asks for: the small ones as drawn for them, the rest from macOS's own, with its margin.
ICONS="$HERE/../icons"
ICONSET="$WORK/karvon.iconset"
mkdir -p "$ICONSET"
icon() {
  case $1 in
    16 | 32 | 64) cp "$ICONS/karvon-$1.png" "$2" ;;
    *) sips -z "$1" "$1" "$ICONS/karvon-macos.png" --out "$2" >/dev/null ;;
  esac
}
for size in 16 32 128 256 512; do
  icon $size "$ICONSET/icon_${size}x${size}.png"
  icon $((size * 2)) "$ICONSET/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/karvon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Karvon Studio</string>
  <key>CFBundleDisplayName</key><string>Karvon Studio</string>
  <key>CFBundleIdentifier</key><string>io.github.karvon-studio.karvon</string>
  <key>CFBundleExecutable</key><string>Karvon</string>
  <key>CFBundleIconFile</key><string>karvon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

# Ad hoc: the bundle as one, each of its native libraries first.
find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -perm -u+x \) -exec codesign --force --sign - {} \; 2>/dev/null || true
codesign --force --deep --sign - "$APP"
codesign --verify --deep "$APP"

# ---- .dmg ----
DMG_DIR="$WORK/dmg"
mkdir -p "$DMG_DIR"
cp -a "$APP" "$DMG_DIR/"
ln -s /Applications "$DMG_DIR/Applications"
DMG="$DIST/Karvon-Studio-$VERSION-$ARCH.dmg"
hdiutil create -volname "Karvon Studio" -srcfolder "$DMG_DIR" -ov -format UDZO "$DMG" >/dev/null
echo "$DMG"

# ---- Homebrew cask (for a tap; its sha256 is this .dmg's) ----
SHA=$(shasum -a 256 "$DMG" | cut -d' ' -f1)
BREW_ARCH=$([[ "$ARCH" == arm64 ]] && echo arm64 || echo intel)
sed -e "s/@VERSION@/$VERSION/g" -e "s/@ARCH@/$ARCH/g" -e "s/@BREW_ARCH@/$BREW_ARCH/g" -e "s/@SHA256@/$SHA/g" "$HERE/karvon-studio.rb.in" > "$DIST/karvon-studio-$ARCH.rb"
echo "$DIST/karvon-studio-$ARCH.rb"
