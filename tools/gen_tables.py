#!/usr/bin/env python3
"""ADR-0001 §2.2: 构建期系数表生成器——Python Fraction 精确有理数 → RHE → Q32.16 C# 常量。
重跑必须逐位复现 src/Arena.Core/Calc/DeterministicTables.cs（ContentSha256 校验）。
含下限钳制：伤害递减 ×0.40（GDD §8.5③）；硬直 ×0.5 下限由消费侧钳制（非表值）。"""
from fractions import Fraction
import hashlib, sys

OUT = "src/Arena.Core/Calc/DeterministicTables.cs"

def rhe(fr):
    n, d = fr.numerator, fr.denominator
    q, r = divmod(n, d)
    twice = r * 2
    if twice > d or (twice == d and q % 2 != 0): q += 1
    return q

def pow_table(p, q, max_n, floor=None):
    result = []
    for n in range(max_n + 1):
        v = Fraction(p, q) ** n
        if floor is not None and v < Fraction(*floor):
            v = Fraction(*floor)
        result.append(rhe(v * 65536))
    return result

launch = pow_table(4, 5, 8)
hitstun = pow_table(97, 100, 64)
damage = pow_table(47, 50, 64, floor=(40, 100))

MODS = {
    "BackstabX120": (120, 100), "AntiAirX115": (115, 100), "AirborneX105": (105, 100),
    "SweepX070": (70, 100), "FreezeDecayX088": (88, 100), "GetupProtX090": (90, 100),
    "WeakPointX150": (150, 100), "WeakPointX200": (200, 100), "DefDownX125": (125, 100),
}
mods = {k: rhe(Fraction(n, d) * 65536) for k, (n, d) in MODS.items()}

def fmt_arr(name, arr, comment):
    lines = ",\n".join("        " + str(v) for v in arr)
    return "    /// " + comment + "\n    public static readonly long[] " + name + " =\n    {\n" + lines + "\n    };"

content_all = repr(launch) + repr(hitstun) + repr(damage) + repr(mods)
sha = hashlib.sha256(content_all.encode()).hexdigest()[:32]
mods_lines = "\n".join("        public const long " + k + " = " + str(v) + ";" for k, v in mods.items())

src = """// PRODUCTION - Arena.Core
// ADR-0001 §2.2/§2.3: 预计算衰减/修正系数表——构建期精确有理数生成产物（勿手改）。
// 生成器: tools/gen_tables.py（Python Fraction → RHE → Q32.16，含下限钳制）。
// 重跑同脚本必须逐位复现本文件。
namespace Arena.Core.Calc;

public static class DeterministicTables
{
    public const string Version = "DC-2026-09-01";
    public const string ContentSha256 = \"""" + sha + """";
"""
for name, arr, cmt in [
    ("LaunchDecay", launch, "浮空衰减 (4/5)^n，n=0..8。下限 3.0 m/s 由 Gates 在浮空刷新时钳制（非表值钳制）"),
    ("HitstunDecay", hitstun, "硬直递减 (97/100)^n，n=0..64。×0.5 最终时长下限由 HitResolve 在应用时钳制（GDD §8.5②）"),
    ("DamageDecay", damage, "伤害递减 max((47/50)^n, ×0.40)，n=0..64。表值已钳制 0.40 下限（GDD §8.5③）"),
]:
    src += "\n    /// " + cmt + "\n    public static readonly long[] " + name + " =\n    {\n" + ",\n".join("        " + str(v) for v in arr) + "\n    };\n"
src += "\n    public static class Modifiers\n    {\n" + mods_lines + "\n    }\n}\n"
open(OUT, 'w', encoding='utf-8').write(src)
print(f"OK sha={sha}")
