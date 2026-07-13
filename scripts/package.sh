#!/usr/bin/env bash
# 把 dotnet 交叉发布的裸二进制打成各平台标准分发包：
#   Windows .exe / macOS .app(zip) / Linux .AppImage
# 输入：out/<rid>/AltSnip[.exe]   输出：dist/*
set -euo pipefail

mkdir -p dist

# ---- Windows ----
cp out/win-x64/AltSnip.exe dist/AltSnip-Windows-x64.exe

# ---- macOS 图标：把 logo 缩进 80% 安全区(四周透明留白) → 生成多分辨率 .icns ----
# macOS(Big Sur+)图标规范：画面只占中间 ~824/1024，四周约 100px 透明；系统不自动裁切留白，
# 必须画进图里，否则图标会铺满方块、比 Dock 里其它 app 偏大。Pillow 可跨平台直接写 .icns。
python3 -m pip install --quiet --disable-pip-version-check pillow 2>/dev/null || true
python3 - <<'PY'
from PIL import Image
src = Image.open("logo_256.png").convert("RGBA")
BASE, ART = 1024, 824               # 画面占 ~80%，四周透明留白
art = src.resize((ART, ART), Image.LANCZOS)
canvas = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))
off = (BASE - ART) // 2
canvas.paste(art, (off, off), art)
canvas.save("AltSnip.icns", format="icns")
print("AltSnip.icns written (%dpx art in %dpx canvas)" % (ART, BASE))
PY

# ---- macOS：.app 应用包 → zip ----
for arch in arm64 x64; do
  APP="AltSnip.app"
  rm -rf "$APP"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp "out/osx-$arch/AltSnip" "$APP/Contents/MacOS/AltSnip"
  chmod +x "$APP/Contents/MacOS/AltSnip"
  cp AltSnip.icns "$APP/Contents/Resources/AltSnip.icns"
  cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>AltSnip</string>
  <key>CFBundleDisplayName</key><string>AltSnip</string>
  <key>CFBundleIdentifier</key><string>dev.altsnip.app</string>
  <key>CFBundleVersion</key><string>2.0.0</string>
  <key>CFBundleShortVersionString</key><string>2.0.0</string>
  <key>CFBundleExecutable</key><string>AltSnip</string>
  <key>CFBundleIconFile</key><string>AltSnip</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSUIElement</key><true/>
</dict></plist>
PLIST
  zip -r -q "dist/AltSnip-macOS-$arch.zip" "$APP"
  rm -rf "$APP"
done

# ---- Linux：AppImage ----
APPDIR=AppDir
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
cp out/linux-x64/AltSnip "$APPDIR/usr/bin/AltSnip"
chmod +x "$APPDIR/usr/bin/AltSnip"
cp cross/Assets/logo.png "$APPDIR/AltSnip.png"
cat > "$APPDIR/AltSnip.desktop" <<'DESK'
[Desktop Entry]
Type=Application
Name=AltSnip
Exec=AltSnip
Icon=AltSnip
Categories=Utility;Graphics;
Terminal=false
DESK
cat > "$APPDIR/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/AltSnip" "$@"
RUN
chmod +x "$APPDIR/AppRun"

wget -q https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage -O appimagetool
chmod +x appimagetool
ARCH=x86_64 ./appimagetool --appimage-extract-and-run "$APPDIR" dist/AltSnip-Linux-x64.AppImage

echo "== dist =="
ls -lh dist
