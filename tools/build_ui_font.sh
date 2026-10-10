#!/bin/bash
# 生成界面用 CJK 字体子集（OFL 许可的 Noto Sans CJK）。
# 为何要子集: 完整 Noto Sans CJK 约 19MB，而游戏实际只用约 1600 个字符 → 303KB。
# 何时重跑: 凡是新增/修改了界面文案、职业名、技能名等**会显示出来的文字**，都要重跑本脚本，
#           否则新字会渲染成方块（缺字形）。
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC=/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc
OUT="$ROOT/src/Arena.Client/assets/fonts/UiCJK-Regular.ttf"

python3 - "$ROOT" <<'PY'
import glob, sys, os
root = sys.argv[1]
chars = set()
for f in glob.glob(os.path.join(root, 'docs/**/*.csv'), recursive=True):
    chars |= set(open(f, encoding='utf-8', errors='ignore').read())
for pat in ('src/**/*.cs', 'tests/**/*.cs'):
    for f in glob.glob(os.path.join(root, pat), recursive=True):
        chars |= set(open(f, encoding='utf-8', errors='ignore').read())
chars = {c for c in chars if ord(c) > 31} | {chr(i) for i in range(32, 127)}
open('/tmp/font_charset.txt', 'w', encoding='utf-8').write(''.join(sorted(chars)))
print("charset:", len(chars))
PY

python3 -m fontTools.subset "$SRC" --text-file=/tmp/font_charset.txt \
    --output-file="$OUT" --font-number=0 --layout-features='' --no-hinting --desubroutinize
ls -la "$OUT"
