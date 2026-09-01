using System;
using System.Collections.Generic;
using Arena.Core.Calc;
// PRODUCTION - Arena.Core
// SPEC-0005 §5: SweepSolver——全工程唯一空间扫掠实现点（Client/Server/Replay 三宿主同源）。
// 总原则（SPEC-0005 §5 章首）: 全解析求解，无逐类二分；二分仅测试对照 oracle（K=32）。
// 统一引擎: ConvexRegion（半平面 ∩ 圆盘）× 线性扫掠 ⇒ 逐约束区间裁剪（PA-1 P1/P2 区间交）。
// 特例路径: 3D 球（头部，PA-H1.2）。
// PA-2 语义: [0,1] 裁剪；起点重叠 TOI=0；相切=接触（≤）；t_out<0 无接触。
// PA-3 语义: 全部决策使用量化后 toiFixed；同一 toiFixed = 同一离散时刻。
namespace Arena.Core.Collision;

public static class SweepSolver
{
    /// <summary>
    /// 通用扫掠: 点 mover 从 (fromX,fromZ) 沿 (dispX,dispZ)（Q32.16 米/Tick）线性运动 vs 凸区域。
    /// moverRadius: 运动侧半径（Minkowski ⊕ disk(r)——半平面 B+r / 圆盘 R+r，按调用即时并入，
    /// 不修改 region（地形 region 共享只读））。moverRadius=0 = 纯点。
    /// 返回接触区间 [toiRaw, tOutRaw]（量化 Q32.16 of [0,1]）；false = 无接触。
    /// 法线 = 产生进入约束的面外向法线（指向 mover 一侧，SPEC-0005 §5.3）；
    /// 起点已重叠（无进入约束）→ 法线 = 运动反方向归一化（零向量规则同源，构造确定）。
    /// </summary>
    public static bool SweepRegion(ConvexRegion region, long fromX, long fromZ, long dispX, long dispZ, long moverRadius,
        out long toiRaw, out long tOutRaw, out long nX, out long nZ)
    {
        toiRaw = -1; tOutRaw = -1; nX = 0; nZ = 0;
        long tLo = 0, tHi = Fixed.ONE;          // PA-2.1: [0,1] 裁剪
        long bindNx = 0, bindNz = 0;            // 进入约束法线（半平面）
        long bindCx = 0, bindCz = 0;            // 进入约束盘心（圆盘）
        bool planeBound = false, diskBound = false;

        var planes = region.HalfPlanes;
        for (int i = 0; i < planes.Count; i++)
        {
            var hp = planes[i];
            long b = hp.B + moverRadius;        // Minkowski 膨胀（|n|≈1，≤1 量子保守误差）
            long nP = MulShift(hp.NX, fromX) + MulShift(hp.NZ, fromZ);
            long nD = MulShift(hp.NX, dispX) + MulShift(hp.NZ, dispZ);
            if (nD == 0)
            {
                if (nP > b) return false;        // 平行且在约束外侧——永不相交
                continue;                         // 平行且已在内侧——无约束
            }
            // t ∈ [0, ONE]: nP + (t/ONE)·nD ≤ B  ⇒  nD>0: 上界；nD<0: 下界
            long bound = DeterministicMath.DivRoundHalfEven((b - nP) * Fixed.ONE, nD);
            if (nD > 0)
            {
                if (bound < tHi) tHi = bound;
            }
            else
            {
                if (bound > tLo)
                {
                    tLo = bound; bindNx = hp.NX; bindNz = hp.NZ;
                    planeBound = true; diskBound = false;
                }
            }
            if (tLo > tHi) return false;
        }

        var disks = region.Disks;
        for (int i = 0; i < disks.Count; i++)
        {
            var d = disks[i];
            long r = d.R + moverRadius;
            long ex = fromX - d.CX, ez = fromZ - d.CZ;
            // s²·(D·D) + s·2(E·D) + (E·E − R²) ≤ 0，s = t/ONE（Int128 判别式域，E-7）
            Int128 a = (Int128)dispX * dispX + (Int128)dispZ * dispZ;
            if (a == 0)
            {
                // 零位移: 静态谓词（PA-2.2 起点重叠语义）
                if (ex * ex + ez * ez > r * r) return false;
                continue;
            }
            Int128 b2 = 2 * ((Int128)ex * dispX + (Int128)ez * dispZ);
            Int128 c = (Int128)ex * ex + (Int128)ez * ez - (Int128)r * r;
            if (!DeterministicMath.SolveQuadraticIntervalQ(a, b2, c, out long t1, out long t2))
                return false;
            if (t1 > tLo)
            {
                tLo = t1; bindCx = d.CX; bindCz = d.CZ;
                diskBound = true; planeBound = false;
            }
            if (t2 < tHi) tHi = t2;
            if (tLo > tHi) return false;
        }

        // 相切语义: tLo == tHi = 接触（PA-2.3，闭区间）；t_out < 0 已被初始裁剪排除（PA-2.4）
        toiRaw = tLo;
        tOutRaw = tHi;

        if (diskBound)
        {
            // 圆盘进入: 法线 = TOI 处 mover 位置 − 盘心（指向 mover 一侧）
            long px = fromX + MulShift(dispX, toiRaw);
            long pz = fromZ + MulShift(dispZ, toiRaw);
            DeterministicMath.Normalize(px - bindCx, pz - bindCz, out nX, out nZ);
        }
        else if (planeBound)
        {
            nX = bindNx; nZ = bindNz;
        }
        else
        {
            // 起点已重叠（PA-2.2）: 运动反方向归一化（SPEC-0005 §5.3 零向量规则同源）
            DeterministicMath.Normalize(-dispX, -dispZ, out nX, out nZ);
        }
        return true;
    }

    /// 3D 球扫掠（头部命中唯一路径，PA-H1.2 真 3D 球测试——非高度带近似）。
    /// (fromX,fromY,fromZ) + s·(dx,dy,dz)/ONE 相对运动 vs 球心 (cx,cy,cz) 半径 r（双方半径已并入）。
    public static bool SweepPointVsSphere3D(
        long fromX, long fromY, long fromZ, long dx, long dy, long dz,
        long cx, long cy, long cz, long r,
        out long toiRaw, out long tOutRaw, out long nX, out long nZ)
    {
        toiRaw = -1; tOutRaw = -1; nX = 0; nZ = 0;
        long ex = fromX - cx, ey = fromY - cy, ez = fromZ - cz;
        Int128 a = (Int128)dx * dx + (Int128)dy * dy + (Int128)dz * dz;
        if (a == 0)
            return ex * ex + ey * ey + ez * ez <= r * r;
        Int128 b = 2 * ((Int128)ex * dx + (Int128)ey * dy + (Int128)ez * dz);
        Int128 c = (Int128)ex * ex + (Int128)ey * ey + (Int128)ez * ez - (Int128)r * r;
        if (!DeterministicMath.SolveQuadraticIntervalQ(a, b, c, out long t1, out long t2))
            return false;
        // PA-2.1: [0,1] 裁剪
        if (t1 < 0) t1 = 0;
        if (t2 > Fixed.ONE) t2 = Fixed.ONE;
        if (t1 > t2) return false;
        toiRaw = t1; tOutRaw = t2;
        long px = fromX + MulShift(dx, toiRaw);
        long pz = fromZ + MulShift(dz, toiRaw);
        DeterministicMath.Normalize(px - cx, pz - cz, out nX, out nZ);
        return true;
    }

    /// 静态点含圆谓词（Overlap 快路径）
    public static bool PointInCircle(long px, long pz, long cx, long cz, long r)
    {
        long dx = px - cx, dz = pz - cz;
        return dx * dx + dz * dz <= r * r;
    }

    /// 静态点含 AABB 谓词（水平，双端 inclusive——PA-H1.1）
    public static bool PointInAabb(long px, long pz, long cx, long cz, long halfW, long halfD)
    {
        return px >= cx - halfW && px <= cx + halfW
            && pz >= cz - halfD && pz <= cz + halfD;
    }

    /// 测试对照 oracle（SPEC-0005 §5.2 裁定 2: K=32 离散采样，仅供测试对照——生产路径禁用二分）。
    /// 实现: [0,1] 均匀 2^20 采样 + 局部细化——覆盖「穿入又穿出」（前缀谓词二分无法对区间集定位）。
    /// 分辨率 9.5e-7（0.06 量子）——低于解析路径的 1 量化子决策粒度，可作 ORACLE 上界对照。
    public static bool SweepRegionOracle(ConvexRegion region, long fromX, long fromZ, long dispX, long dispZ, long moverRadius,
        out long toiRaw)
    {
        toiRaw = -1;
        const int N = 1 << 20;
        long prevT = -1;
        bool prevIn = RegionContains(region, fromX, fromZ, moverRadius);
        if (prevIn) { toiRaw = 0; return true; }
        for (int i = 1; i <= N; i++)
        {
            long t = (long)((double)i / N * Fixed.ONE);   // oracle 熵域允许 double 采样索引（仅测试）
            long px = fromX + MulShift(dispX, t);
            long pz = fromZ + MulShift(dispZ, t);
            bool nowIn = RegionContains(region, px, pz, moverRadius);
            if (nowIn && !prevIn)
            {
                // 局部细化（K=32 二分于 [prevT, t]）
                long lo = prevT < 0 ? 0 : prevT, hi = t;
                for (int k = 0; k < 32; k++)
                {
                    long mid = (lo + hi) / 2;
                    long mx = fromX + MulShift(dispX, mid);
                    long mz = fromZ + MulShift(dispZ, mid);
                    if (RegionContains(region, mx, mz, moverRadius)) hi = mid; else lo = mid;
                }
                toiRaw = hi;
                return true;
            }
            prevIn = nowIn;
            prevT = t;
        }
        return false;
    }

    /// 点含膨胀区域谓词（oracle 用——半平面 B+r / 圆盘 R+r 与生产路径同一膨胀语义）
    private static bool RegionContains(ConvexRegion region, long px, long pz, long moverRadius)
    {
        var planes = region.HalfPlanes;
        for (int i = 0; i < planes.Count; i++)
        {
            var hp = planes[i];
            if (MulShift(hp.NX, px) + MulShift(hp.NZ, pz) > hp.B + moverRadius) return false;
        }
        var disks = region.Disks;
        for (int i = 0; i < disks.Count; i++)
        {
            var d = disks[i];
            long dx = px - d.CX, dz = pz - d.CZ;
            long r = d.R + moverRadius;
            if (dx * dx + dz * dz > r * r) return false;
        }
        return true;
    }

    private static long MulShift(long x, long m) => DeterministicMath.MulShift(x, m);
}
