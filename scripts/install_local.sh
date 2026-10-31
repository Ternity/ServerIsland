#!/usr/bin/env bash
# 重新编译 Mac 版并替换本机安装的 App（/Applications/ServerIsland.app），然后启动新版本
#   ./scripts/install_local.sh               # 装到 /Applications
#   DEST=~/Applications ./scripts/install_local.sh   # 装到别的目录
#   ./scripts/install_local.sh --no-build    # 不编译，直接安装 build/ServerIsland.app（package_all.sh 用）
# 设置和日志不受影响（在 ~/Library/Application Support/ServerIsland 和 ~/Library/Logs/ServerIsland）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="${DEST:-/Applications}"
APP="$DEST/ServerIsland.app"

[[ "${1:-}" == "--no-build" ]] || "$ROOT/scripts/build_app.sh"

# 退出正在运行的 ServerIsland（包括在 Mac 上试用的跨平台版，两个不要同时运行），等它把设置写完
pkill -x ServerIsland 2>/dev/null || true
for _ in $(seq 1 50); do pgrep -x ServerIsland >/dev/null || break; sleep 0.1; done

rm -rf "$APP"
ditto "$ROOT/build/ServerIsland.app" "$APP"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
# build 里的副本只是中间产物，删掉，免得启动台 / 聚焦搜索里出现两个 ServerIsland
rm -rf "$ROOT/build/ServerIsland.app"

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")"
echo "✓ 已安装 ${APP}（版本 ${VERSION}）"
open "$APP"
