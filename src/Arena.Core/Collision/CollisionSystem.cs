using System;
using System.Collections.Generic;
using Arena.Core.Calc;
using Arena.Core.Sim;
// PRODUCTION - Arena.Core
// SPEC-0005 §1/§2/§6: CollisionSystem——统一运动路径 IntegrateMove + 静态地形注册表。
// 职责收窄: 本类只做几何（地形 sweep/L1 迭代/L2 推挤几何辅助）；规则消费在 Sim.HitResolve/SimWorld。
// L1 迭代上限 4（PA-5.2 通用上限）；超限 = 确定性降级（snap 最先接触面 + 法向速度清零）。
// 每 Tick 至多一次反弹（SPEC-0005 §6.3）；第二约束面 L1 clamp（PA-5.3）。
namespace Arena.Core.Collision;

/// 静态地形体（ArenaDef 装配；Id 稳定）
public sealed record TerrainBody(int Id, ConvexRegion Region, TerrainAction Action, long HeightTop);

/// IntegrateMove 结果（SPEC-0005 §2）
public readonly record struct MoveResult(
    long FinalX, long FinalZ,
    long FinalVelX, long FinalVelZ,
    bool TouchedWall, int BounceCount,
    long ContactNormalX, long ContactNormalZ);

public sealed class CollisionSystem
{
    private readonly List<TerrainBody> _terrain = new();
    private readonly BroadPhase _terrainBp;
    private bool _sealed;

    public CollisionSystem(long cellSize) => _terrainBp = new BroadPhase(cellSize);

    public void AddTerrain(TerrainBody body)
    {
        if (_sealed) throw new InvalidOperationException("terrain sealed");
        _terrain.Add(body);
    }

    /// 装配完成（构建期一次；此后地形只读）
    public void SealTerrain()
    {
        _terrain.Sort((a, b) => a.Id.CompareTo(b.Id));   // ADR-0001 §3.1: Id 序
        foreach (var t in _terrain)
            _terrainBp.Insert(t.Id, t.Region.MinX, t.Region.MinZ, t.Region.MaxX, t.Region.MaxZ, 0);
        _sealed = true;
    }

    public IReadOnlyList<TerrainBody> Terrain => _terrain;

    /// 地面高度查询（GDD §3.5/§19: 平台=可行走高地；HeightTop ≤ maxY 的最高平台顶）
    /// maxY: 调用方给定（PosY + 台阶高度）——高于它的平台不吸附（悬崖语义）
    public long QueryGround(long x, long z, long maxY)
    {
        long ground = 0;
        for (int i = 0; i < _terrain.Count; i++)
        {
            var body = _terrain[i];
            if (body.HeightTop <= ground || body.HeightTop > maxY) continue;
            if (!body.Region.Contains(x, z)) continue;
            ground = body.HeightTop;
        }
        return ground;
    }

    /// <summary>
    /// SPEC-0005 §2 统一路径: 水平运动积分（走位/击退/突进共用；禁止旁路）。
    /// velX/velZ: Q32.16 m/s；位移 = RHE(vel/60)。bounceEnabled: 击退/击飞 true，走位 false（§2 响应策略）。
    /// 垂直运动不在此路径（Launch 的 GroundStop 由 Sim 状态机处理）。
    /// </summary>
    public MoveResult IntegrateMove(long x, long z, long velX, long velZ, long radius, bool bounceEnabled)
    {
        long dispX = DeterministicMath.DivRoundHalfEven(velX, RuntimeConstants.TICK_RATE);
        long dispZ = DeterministicMath.DivRoundHalfEven(velZ, RuntimeConstants.TICK_RATE);
        long finalVelX = velX, finalVelZ = velZ;
        bool touchedWall = false, bounced = false;
        long cNx = 0, cNz = 0;

        const int MAX_ITER = 4;   // PA-5.2
        long remX = dispX, remZ = dispZ;
        long firstNx = 0, firstNz = 0; bool hasFirst = false;

        for (int iter = 0; iter < MAX_ITER; iter++)
        {
            if (remX == 0 && remZ == 0) break;
            // 候选地形（BroadPhase 保守查询——swept 包围盒）
            var sweptMinX = Math.Min(x, x + remX) - radius;
            var sweptMaxX = Math.Max(x, x + remX) + radius;
            var sweptMinZ = Math.Min(z, z + remZ) - radius;
            var sweptMaxZ = Math.Max(z, z + remZ) + radius;
            var candidates = _terrainBp.Query(sweptMinX, sweptMaxX, sweptMinZ, sweptMaxZ);

            // 最近接触（确定性总序: (toiRaw, terrainId)——SPEC-0005 §6）
            long bestToi = long.MaxValue; TerrainBody? bestBody = null;
            long bestNx = 0, bestNz = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var body = _terrain[candidates[i]];
                if (body.Action == TerrainAction.PassThrough) continue;   // 高地（QueryGround 域）非碰撞约束
                // mover 半径由 SweepRegion 即时并入（地形 region 只读共享）
                if (!SweepSolver.SweepRegion(body.Region, x, z, remX, remZ, radius, out long toi, out _, out long nx, out long nz))
                    continue;
                if (toi < bestToi || (toi == bestToi && bestBody is not null && body.Id < bestBody.Id))
                {
                    bestToi = toi; bestBody = body; bestNx = nx; bestNz = nz;
                }
            }

            if (bestBody is null)
            {
                x += remX; z += remZ;
                break;
            }

            // 接触
            touchedWall = true;
            if (!hasFirst)
            {
                firstNx = bestNx; firstNz = bestNz; hasFirst = true;
            }
            long contactX = x + DeterministicMath.MulShift(remX, bestToi);
            long contactZ = z + DeterministicMath.MulShift(remZ, bestToi);
            x = contactX; z = contactZ;
            cNx = bestNx; cNz = bestNz;

            // 剩余位移
            long remainScale = Fixed.ONE - bestToi;
            long remaX = DeterministicMath.MulShift(remX, remainScale);
            long remaZ = DeterministicMath.MulShift(remZ, remainScale);

            bool doBounce = bounceEnabled && bestBody.Action == TerrainAction.Bounce && !bounced;
            if (doBounce)
            {
                // 反射: v' = v − (1+e)(v·n)n，e=0.6（GDD §5.8 撞墙反弹 ×0.6——法向分量）
                (finalVelX, finalVelZ) = Reflect(finalVelX, finalVelZ, bestNx, bestNz, 6, 10);
                (remaX, remaZ) = Reflect(remaX, remaZ, bestNx, bestNz, 6, 10);
                bounced = true;
                remX = remaX; remZ = remaZ;
                continue;   // 剩余运动重积分（§6.3）；再接触 = clamp（本 Tick 不再反弹）
            }
            else
            {
                // Stop 或第二次接触: L1 clamp——法向剩余分量移除，切向保留
                long dn = DeterministicMath.MulShift(remaX, bestNx) + DeterministicMath.MulShift(remaZ, bestNz);
                remX = remaX - DeterministicMath.MulShift(dn, bestNx);
                remZ = remaZ - DeterministicMath.MulShift(dn, bestNz);
                // 速度法向分量清零（贴墙滑动）
                long vn = DeterministicMath.MulShift(finalVelX, bestNx) + DeterministicMath.MulShift(finalVelZ, bestNz);
                if (vn < 0)   // 仅当朝墙运动
                {
                    finalVelX -= DeterministicMath.MulShift(vn, bestNx);
                    finalVelZ -= DeterministicMath.MulShift(vn, bestNz);
                }
                continue;
            }
        }

        return new MoveResult(x, z, finalVelX, finalVelZ, touchedWall, bounced ? 1 : 0, cNx, cNz);
    }

    /// 反射: v' = v − (1+e)·(v·n)·n（n 指向 mover 一侧；v·n < 0 = 朝面运动）
    /// e = bounceNum/bounceDen（GDD §5.8: ×0.6 → 6/10）
    private static (long, long) Reflect(long vx, long vz, long nx, long nz, long bounceNum, long bounceDen)
    {
        long vn = DeterministicMath.MulShift(vx, nx) + DeterministicMath.MulShift(vz, nz);
        if (vn >= 0) return (vx, vz);
        // vn·(1+e) = RHE(vn × (den+num), den)
        long scale = DeterministicMath.DivRoundHalfEven(vn * (bounceDen + bounceNum), bounceDen);
        long jx = DeterministicMath.MulShift(scale, nx);
        long jz = DeterministicMath.MulShift(scale, nz);
        return (vx - jx, vz - jz);
    }

    /// L2 SoftPush 几何（成对对称分离——仅击退/浮空位移状态调用；走位重叠合法，GDD §2.1.2）
    /// 返回双方位移增量（各自加上即可）。
    public static (long pushAx, long pushAz, long pushBx, long pushBz) SoftPushPair(
        long ax, long az, long bx, long bz, long radius)
    {
        long dx = bx - ax, dz = bz - az;
        long distSq = dx * dx + dz * dz;
        long minDist = radius * 2;
        if (distSq >= minDist * minDist) return (0, 0, 0, 0);
        long dist = DeterministicMath.ISqrt(distSq);
        long overlap = minDist - dist;
        if (dist == 0)
        {
            // 同心: 构造确定方向 (1,0)
            return (-overlap / 2, 0, overlap / 2, 0);
        }
        long ux = DeterministicMath.DivRoundHalfEven(dx * Fixed.ONE, dist);
        long uz = DeterministicMath.DivRoundHalfEven(dz * Fixed.ONE, dist);
        long half = overlap / 2;
        return (DeterministicMath.MulShift(-ux, half),
                DeterministicMath.MulShift(-uz, half),
                DeterministicMath.MulShift(ux, half),
                DeterministicMath.MulShift(uz, half));
    }
}
