// PRODUCTION - Arena.Core
// ADR-0001 §2.2/§2.3: 预计算衰减/修正系数表——构建期精确有理数生成产物（勿手改）。
// 生成器: tools/gen_tables.py（Python Fraction → RHE → Q32.16，含下限钳制）。
// 重跑同脚本必须逐位复现本文件。
namespace Arena.Core.Calc;

public static class DeterministicTables
{
    public const string Version = "DC-2026-09-01";
    public const string ContentSha256 = "3b604e5ce63e9d4c573a2f29c342cc05";

    /// 浮空衰减 (4/5)^n，n=0..8。下限 3.0 m/s 由 Gates 在浮空刷新时钳制（非表值钳制）
    public static readonly long[] LaunchDecay =
    {
        65536,
        52429,
        41943,
        33554,
        26844,
        21475,
        17180,
        13744,
        10995
    };

    /// 硬直递减 (97/100)^n，n=0..64。×0.5 最终时长下限由 HitResolve 在应用时钳制（GDD §8.5②）
    public static readonly long[] HitstunDecay =
    {
        65536,
        63570,
        61663,
        59813,
        58019,
        56278,
        54590,
        52952,
        51363,
        49823,
        48328,
        46878,
        45472,
        44108,
        42784,
        41501,
        40256,
        39048,
        37877,
        36740,
        35638,
        34569,
        33532,
        32526,
        31550,
        30604,
        29686,
        28795,
        27931,
        27093,
        26280,
        25492,
        24727,
        23985,
        23266,
        22568,
        21891,
        21234,
        20597,
        19979,
        19380,
        18798,
        18234,
        17687,
        17157,
        16642,
        16143,
        15659,
        15189,
        14733,
        14291,
        13862,
        13447,
        13043,
        12652,
        12272,
        11904,
        11547,
        11201,
        10865,
        10539,
        10222,
        9916,
        9618,
        9330
    };

    /// 伤害递减 max((47/50)^n, ×0.40)，n=0..64。表值已钳制 0.40 下限（GDD §8.5③）
    public static readonly long[] DamageDecay =
    {
        65536,
        61604,
        57908,
        54433,
        51167,
        48097,
        45211,
        42499,
        39949,
        37552,
        35299,
        33181,
        31190,
        29319,
        27559,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214,
        26214
    };

    public static class Modifiers
    {
        public const long BackstabX120 = 78643;
        public const long AntiAirX115 = 75366;
        public const long AirborneX105 = 68813;
        public const long SweepX070 = 45875;
        public const long FreezeDecayX088 = 57672;
        public const long GetupProtX090 = 58982;
        public const long WeakPointX150 = 98304;
        public const long WeakPointX200 = 131072;
        public const long DefDownX125 = 81920;
    }
}
