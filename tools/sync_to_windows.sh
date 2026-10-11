#!/bin/bash
# tools/sync_to_windows.sh —— 把工作区源码同步到 Windows 主机并校验哈希。
# 用法: tools/sync_to_windows.sh
# 依赖: Windows 侧自带 tar.exe (Win10+ / bsdtar) 与 ssh win 通道。
set -euo pipefail
cd "$(dirname "$0")/.."
DEST='D:\luxera\glory-game\repo'

echo "[1/3] 打包本地源码..."
STAMP=$(date +%s)
tar czf - \
  --exclude='.git' --exclude='bin' --exclude='obj' --exclude='.godot' \
  --exclude='*.user' --exclude='.vs' \
  src docs tools arena.sln CLAUDE.md 2>/dev/null > /tmp/arena_sync_$STAMP.tar.gz
ls -l /tmp/arena_sync_$STAMP.tar.gz

echo "[2/3] 传输并在 Windows 侧解包到 $DEST ..."
cat /tmp/arena_sync_$STAMP.tar.gz | ssh -o BatchMode=yes win \
  "tar -xzf - -C \"$DEST\"" 
echo "   解包完成"

echo "[3/3] 校验关键文件哈希 (local | windows)..."
for f in src/Arena.Client/CameraFactory.cs src/Arena.Client/MatchRoot.cs src/Arena.Client/ArenaBuilder.cs; do
  L=$(md5sum "$f" | cut -d' ' -f1)
  W=$(ssh -o BatchMode=yes win "certutil -hashfile \"$DEST\\${f//\//\\}\" MD5" 2>/dev/null | sed -n 2p | tr -d ' \r')
  if [ "$L" = "$W" ]; then echo "  OK   $f   $L"; else echo "  FAIL $f  local=$L win=$W"; fi
done
rm -f /tmp/arena_sync_$STAMP.tar.gz
