#!/usr/bin/env bash
# 打包 ServerIsland.dmg（只需 Command Line Tools）
#
#   ./scripts/make_dmg.sh            # 方案 A：ad-hoc 签名，自己和同学用
#
# 以后有了 Apple Developer ID（方案 B），设置下面两个环境变量即可签名 + 公证：
#   SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
#   NOTARY_PROFILE="serverisland"   # 事先执行一次：xcrun notarytool store-credentials serverisland --apple-id … --team-id …
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)"
APP="$ROOT/build/ServerIsland.app"
STAGE="$ROOT/build/dmg-stage"
DMG="$ROOT/build/ServerIsland-$VERSION.dmg"

# Sources/ServerIsland/Private/ 是本机的实验功能（不进仓库），发布包里不带：编译前挪开，脚本结束时放回。
PRIVATE="$ROOT/Sources/ServerIsland/Private"
HOLD="$ROOT/build/.private-hold"
mkdir -p "$ROOT/build"
if [[ -d "$PRIVATE" ]]; then
  rm -rf "$HOLD"
  mv "$PRIVATE" "$HOLD"
  trap 'mv "$HOLD" "$PRIVATE"' EXIT
  echo "▸ 本机实验功能不进发布包（已暂时挪开 Private/）"
fi

echo "▸ 编译 release（arm64）"
swift build -c release --arch arm64 2>&1 | grep -E "error|warning: unre|Compiling|Build complete" || true
ARM_BIN="$(swift build -c release --arch arm64 --show-bin-path)/ServerIsland"

# 尝试同时编译 Intel 版本并合成通用二进制；失败时只打包 Apple Silicon 版本。
BIN="$ARM_BIN"
X86_LOG="$ROOT/build/x86_64-build.log"
mkdir -p "$ROOT/build"
if swift build -c release --triple x86_64-apple-macosx14.0 >"$X86_LOG" 2>&1; then
  X86_BIN="$(swift build -c release --triple x86_64-apple-macosx14.0 --show-bin-path)/ServerIsland"
  mkdir -p build
  lipo -create "$ARM_BIN" "$X86_BIN" -output build/ServerIsland-universal
  BIN="build/ServerIsland-universal"
  echo "▸ 已合成通用二进制（Apple Silicon + Intel）"
else
  echo "▸ Intel 版本编译失败，只打包 Apple Silicon 版本（完整输出见 build/x86_64-build.log）："
  grep -E "error" "$X86_LOG" | grep -v PlatformPath | tail -5 || true
fi

echo "▸ 组装 ServerIsland.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/ServerIsland"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
cp -R Resources/Sounds "$APP/Contents/Resources/Sounds"
cp Branding/menubar.png Branding/menubar@2x.png "$APP/Contents/Resources/"

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  echo "▸ 使用 Developer ID 签名：$SIGN_IDENTITY"
  codesign --force --deep --options runtime --timestamp --sign "$SIGN_IDENTITY" "$APP"
else
  echo "▸ ad-hoc 签名（方案 A）"
  codesign --force --deep --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"

echo "▸ 制作 DMG"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
cat > "$STAGE/安装说明 · Install.txt" <<'EOF'
ServerIsland 安装说明
====================
1. 把 ServerIsland 拖进旁边的「Applications / 应用程序」文件夹。
2. 第一次打开时，macOS 会提示“无法验证开发者”（这个版本没有经过 Apple 公证），允许一次即可：
   - macOS 15 及以后：先双击打开一次（会被拦住），再到 系统设置 → 隐私与安全性，
     在页面下方找到 ServerIsland，点“仍要打开”；
   - macOS 14：在“应用程序”里按住 Control 点 ServerIsland →“打开”→ 再点“打开”；
   - 或者在终端执行：xattr -dr com.apple.quarantine /Applications/ServerIsland.app
3. 打开后它只出现在菜单栏和刘海里（不占 Dock）；第一次会打开设置，从 ~/.ssh/config 添加服务器。

ServerIsland 是免费的开源软件（GPL-3.0）：https://github.com/zhyan0603/ServerIsland

Install
=======
1. Drag ServerIsland into the Applications folder.
2. macOS will say the developer cannot be verified (this build is not notarized). Allow it once:
   - macOS 15 and later: double-click the app once (it gets blocked), then open
     System Settings → Privacy & Security, scroll down and click "Open Anyway";
   - macOS 14: Control-click ServerIsland in Applications → Open → Open;
   - or run: xattr -dr com.apple.quarantine /Applications/ServerIsland.app
3. ServerIsland lives in the menu bar and the notch. Settings opens on first launch.

ServerIsland is free and open source (GPL-3.0): https://github.com/zhyan0603/ServerIsland
EOF
# DMG 卷图标用 App 图标
cp Resources/AppIcon.icns "$STAGE/.VolumeIcon.icns"
SetFile -c icnC "$STAGE/.VolumeIcon.icns" 2>/dev/null || true
SetFile -a C "$STAGE" 2>/dev/null || true

hdiutil create -volname "ServerIsland $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$DMG" >/dev/null
rm -rf "$STAGE"

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
  if [[ -n "${NOTARY_PROFILE:-}" ]]; then
    echo "▸ 提交公证（通常几分钟）"
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
  fi
fi

echo "✓ 已生成 $DMG（$(du -h "$DMG" | cut -f1)）"
