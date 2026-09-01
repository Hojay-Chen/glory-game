#!/usr/bin/env python3
"""Phase 0 Gate: 程序集依赖方向检查——违反 ADR-0009 依赖图即失败。退出码: 0=合规, 1=违规"""
import os, re, sys

ALLOWED = {
    'Arena.Core': set(),
    'Arena.Infra': {'Arena.Core'},
    'Arena.Infra.Godot': {'Arena.Infra', 'Arena.Core'},
    'Arena.Client': {'Arena.Infra.Godot', 'Arena.Infra', 'Arena.Core'},
    'Arena.Headless': {'Arena.Infra', 'Arena.Core'},
}

def check(base='.'):
    violations = []
    src_dir = os.path.join(base, 'src')
    if not os.path.isdir(src_dir):
        return violations
    for proj_dir in sorted(os.listdir(src_dir)):
        csproj = os.path.join(src_dir, proj_dir, proj_dir + '.csproj')
        if not os.path.isfile(csproj):
            continue
        if proj_dir not in ALLOWED:
            violations.append((csproj, f'未知程序集 {proj_dir}'))
            continue
        content = open(csproj, encoding='utf-8').read()
        for m in re.finditer(r'<ProjectReference\s+Include="([^"]+)"', content):
            ref = m.group(1).replace('\\', '/')
            dep = ref.split('/')[-1].replace('.csproj', '')
            allowed = ALLOWED.get(proj_dir, set())
            if dep not in allowed:
                violations.append((csproj, f'{proj_dir} → {dep}: 非法（允许: {sorted(allowed) or "无"}）'))
    return violations

if __name__ == '__main__':
    base = sys.argv[sys.argv.index('--path') + 1] if '--path' in sys.argv else '.'
    v = check(base)
    if v:
        print(f"DEP-DIRECTION VIOLATIONS: {len(v)}")
        for fp, msg in v: print(f"  {fp}: {msg}")
        sys.exit(1)
    else:
        print("Dep direction: PASS")
        sys.exit(0)
