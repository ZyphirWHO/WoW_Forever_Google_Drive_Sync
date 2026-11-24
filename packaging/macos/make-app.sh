#!/usr/bin/env bash
# Builds "<ProductDisplayName>.app" for one macOS architecture and zips it with the "How to open" guide.
#   packaging/macos/make-app.sh osx-arm64 3.0.0 out [3.0.0-dev.42]
# Names, bundle id and version come from Directory.Build.props; the result is ad-hoc signed
# (Apple silicon won't run unsigned code) but not notarised.
set -euo pipefail
RID="$1"; VERSION="$2"; OUT="$3"; INFO_VERSION="${4:-$2}"   # 4th: shown version, e.g. 3.0.0-dev.42
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
prop() { python3 - "$ROOT/Directory.Build.props" "$1" <<'PY'
import sys, xml.etree.ElementTree as ET
for e in ET.parse(sys.argv[1]).getroot().iter(sys.argv[2]):
    if e.text: print(e.text.strip()); break
PY
}
NAME="$(prop ProductDisplayName)"; ID="$(prop ProductId)"; BUNDLE_ID="$(prop ProductBundleId)"
COPYRIGHT="Copyright (c) 2026 $NAME contributors. MIT License."

PUBLISH="$OUT/publish-$RID"
dotnet publish "$ROOT/src/InterfaceSync.Mac/InterfaceSync.Mac.csproj" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:PublishTrimmed=false -p:DebugType=none -p:Version="$VERSION" -p:InformationalVersion="$INFO_VERSION" -o "$PUBLISH"

APP="$OUT/$RID/$NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$ROOT/packaging/macos/InterfaceSync.icns" "$APP/Contents/Resources/InterfaceSync.icns"
cp "$ROOT/assets/fonts/OFL-Cinzel.txt" "$APP/Contents/Resources/OFL-Cinzel.txt"   # licence of the embedded heading font
sed -e "s|{{NAME}}|$NAME|g" -e "s|{{ID}}|$ID|g" -e "s|{{BUNDLE_ID}}|$BUNDLE_ID|g" \
    -e "s|{{VERSION}}|$VERSION|g" -e "s|{{COPYRIGHT}}|$COPYRIGHT|g" \
    "$ROOT/packaging/macos/Info.plist.template" > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/$ID"

# Same rule as the Windows exe: the bundle must carry the project's product name.
plutil -lint "$APP/Contents/Info.plist"
[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleName' "$APP/Contents/Info.plist")" = "$NAME" ] || { echo "CFBundleName differs from $NAME"; exit 1; }
[ -x "$APP/Contents/MacOS/$ID" ] || { echo "missing executable $ID"; exit 1; }

codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

# The zip holds a folder with the app and a guide to getting past Gatekeeper on first open
# (an app macOS has blocked can't show anything itself).
PKG="$OUT/pkg-$RID/$NAME"
rm -rf "$OUT/pkg-$RID"; mkdir -p "$PKG"
ditto "$APP" "$PKG/$NAME.app"
cp "$ROOT/packaging/guides/mac.html" "$PKG/How to open $NAME.html"
ZIP="$OUT/$ID-$VERSION-macos-${RID#osx-}.zip"
(cd "$OUT/pkg-$RID" && ditto -c -k --sequesterRsrc --keepParent "$NAME" "$ZIP")
echo "Built $ZIP"
shasum -a 256 "$ZIP"

# Disk image: drag the app onto Applications (the usual Mac install), with the "How to open" guide.
DMG_SRC="$OUT/dmg-$RID"
rm -rf "$DMG_SRC"; mkdir -p "$DMG_SRC"
ditto "$APP" "$DMG_SRC/$NAME.app"
ln -s /Applications "$DMG_SRC/Applications"
cp "$ROOT/packaging/guides/mac.html" "$DMG_SRC/How to open $NAME.html"
DMG="$OUT/$ID-$VERSION-macos-${RID#osx-}.dmg"
# hdiutil sometimes fails with "Resource busy" on build machines (a transient disk-arbitration race): retry.
for attempt in 1 2 3 4 5; do
  if hdiutil create -volname "$NAME" -srcfolder "$DMG_SRC" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null; then break; fi
  if [ "$attempt" = 5 ]; then echo "hdiutil create failed 5 times" >&2; exit 1; fi
  echo "hdiutil create failed (attempt $attempt); retrying in $((attempt * 5)) s" >&2
  sleep $((attempt * 5))
done
hdiutil verify "$DMG" >/dev/null
echo "Built $DMG"
shasum -a 256 "$DMG"
