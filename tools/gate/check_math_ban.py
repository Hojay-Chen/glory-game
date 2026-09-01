#!/usr/bin/env python3
"""Phase 0 Gate: Math.* 禁令门禁——扫描 Arena.Core/Arena.Infra 源码中的违禁调用。
ADR-0001 §2.4: Math.Pow/Sqrt/Sin/Cos/Tan/Exp/Log/Atan2 不得出现在 Core/Infra 任何代码路径。
测试: 将违规文件放入临时目录，传入 --path 参数验证捕获能力。
退出码: 0=合规, 1=发现违规"""
import os, re, sys

BANNED = re.compile(r'\bMath\.(Pow|Powf|Sqrt|Sqrtf|Sin|SinF|Cos|CosF|Tan|TanF|Exp|Log|Log2|Atan|Atan2|Atan2F|Acos|Asin)\b', re.IGNORECASE)
SCAN_DIRS = ['src/Arena.Core', 'src/Arena.Infra']
SCAN_EXT = {'.cs'}

def scan(base='.'):
    violations = []
    for scan_dir in SCAN_DIRS:
        root = os.path.join(base, scan_dir)
        if not os.path.isdir(root):
            continue
        for dirpath, _, filenames in os.walk(root):
            for fn in sorted(filenames):
                if not fn.endswith('.cs'):
                    continue
                fp = os.path.join(dirpath, fn)
                with open(fp, 'r', encoding='utf-8') as f:
                    for lineno, line in enumerate(f, 1):
                        if BANNED.search(line):
                            violations.append((fp, lineno, line.strip()[:80]))
    return violations

if __name__ == '__main__':
    base = sys.argv[sys.argv.index('--path') + 1] if '--path' in sys.argv else '.'
    v = scan(base)
    if v:
        print(f"MATH-BAN VIOLATIONS: {len(v)}")
        for fp, ln, code in v:
            print(f"  {fp}:{ln}: {code}")
        sys.exit(1)
    else:
        print("Math.* ban: PASS (0 violations)")
        sys.exit(0)
