#!/bin/bash
# 重新生成官网截图：用演示数据（虚构的用户名 / 路径 / 主机名）渲染中英文两套，缩到 1280 宽放进 site/assets/shots/。
set -euo pipefail
cd "$(dirname "$0")/.."
swift build 2>&1 | grep -v PlatformPath | tail -1
TMP=$(mktemp -d)
SHOTS="expanded collapsed toast toast-failed detail-nep detail-vasp detail-confirm partitions history settings-rules settings-rule-editor expanded-idle costs jobs-starred settings-push"
for l in zh en; do
  .build/debug/ServerIsland --render-preview "$TMP/$l" "$l" > /dev/null
  mkdir -p "site/assets/shots/$l"
  for f in $SHOTS; do sips -Z 1280 "$TMP/$l/$f.png" --out "site/assets/shots/$l/$f.png" > /dev/null; done
done
rm -rf "$TMP"
echo "✓ site/assets/shots 已更新"
