using System;
using System.Collections.Generic;
using Arena.Core.Calc;
using Arena.Core.Collision;
// PRODUCTION - Arena.Core
// GDD §4.5 投射物规范 + SPEC-0005 §2 Projectile 运动行: ProjectileSystem。
// 运动 = IntegrateMove 统一路径（Point mover + 半径并入命中谓词；禁止旁路）。
// 高度模型: 直线弹恒高（aimHeight）；lob 抛物线（velY 逐 Tick 量化递减——§3 公理 Tick 内线性）。
// 命中后处理时序（PA-H3）: Destroy 停止处理 / Pierce 扣次数 / 同 Tick 多目标允许。
namespace Arena.Core.Sim;

public sealed class ProjectileState
{
    public int Uid { get; set; }
    public int OwnerId { get; set; }
    public ushort SkillRuntimeId { get; set; }
    public long PosX { get; set; }      // Q32.16 raw
    public long PosY { get; set; }
    public long PosZ { get; set; }
    public long DispX { get; set; }     // 每 Tick 位移（m/Tick Q32.16——Compiler 量化语义）
    public long DispY { get; set; }
    public long DispZ { get; set; }
    public long Radius { get; set; }
    public int SpawnTick { get; set; }
    public int ExpireTick { get; set; }
    public int PierceRemaining { get; set; }
    public bool IsLob { get; set; }
    public bool Expired { get; set; }
    public readonly HashSet<int> HitVictims = new();   // 每 victim 一次（多段投技语义除外）

    public SkillRuntimeData? Def;       // 运行时引用（Catalog 恢复）
}

public static class ProjectileSystem
{
    /// 发射（cast 时调用）。aim: SPEC-0001 heading quantum；返回 Uid（0 = 超上限被拒）。
    public static int Spawn(SimWorld w, FighterStateData owner, SkillRuntimeData def, long headingQuantum, int tick)
    {
        // 同屏上限（GDD §4.5: 每玩家 8 个，超出移除最旧）
        int count = 0; ProjectileState? oldest = null;
        foreach (var p in w.Projectiles)
        {
            if (p.OwnerId != owner.Id || p.Expired) continue;
            count++;
            if (oldest is null || p.Uid < oldest.Uid) oldest = p;
        }
        if (count >= RuntimeConstants.MAX_PROJECTILES_PER_FIGHTER)
        {
            if (oldest is not null) Destroy(w, oldest, ProjectileEndReason.Cap);
        }

        // 方向（CORDIC 量化朝向）
        DeterministicMath.CordicCosSin(headingQuantum, out var fx, out var fz);
        // 每 Tick 位移 = RHE(speed_q16 / 60)——ProjSpeedQ 已是 Q32.16 m/s，直接除以 Tick 率
        long dispPerTick = DeterministicMath.DivRoundHalfEven(def.ProjSpeedQ, RuntimeConstants.TICK_RATE);
        var proj = new ProjectileState
        {
            Uid = w.NextProjectileUid(),
            OwnerId = owner.Id,
            SkillRuntimeId = def.RuntimeId,
            Def = def,
            PosX = owner.PosX.Raw,
            PosY = owner.PosY.Raw + def.AimHeightQ,
            PosZ = owner.PosZ.Raw,
            DispX = DeterministicMath.MulShift(fx, dispPerTick),
            DispZ = DeterministicMath.MulShift(fz, dispPerTick),
            DispY = def.IsLob ? DeterministicMath.DivRoundHalfEven(def.LaunchVelQ, RuntimeConstants.TICK_RATE) : 0,
            Radius = def.ProjRadius,
            SpawnTick = tick,
            ExpireTick = tick + def.ProjRangeTicks,
            PierceRemaining = def.Sweep ? 99 : 0,   // 【穿透】标签复用 sweep 列（Compiler 登记）
            IsLob = def.IsLob,
        };
        w.Projectiles.Add(proj);
        w.Events.Emit(new SimEvent
        {
            Kind = EventKind.ProjectileSpawned, AttackerId = owner.Id,
            SkillId = def.RuntimeId, ValueRaw = proj.Uid,
        });
        return proj.Uid;
    }

    public enum ProjectileEndReason : byte { Terrain = 0, Lifetime = 1, Cap = 2, Hit = 3 }

    public static void Destroy(SimWorld w, ProjectileState p, ProjectileEndReason reason)
    {
        if (p.Expired) return;
        p.Expired = true;
        w.Events.Emit(new SimEvent
        {
            Kind = EventKind.ProjectileDestroyed, AttackerId = p.OwnerId,
            SkillId = p.SkillRuntimeId, ReasonByte = (byte)reason, ValueRaw = p.Uid,
        });
    }

    /// 每 Tick 推进（ADR-0001 §3.2 ② Sim 主动推进，Uid 升序）。
    /// 收集命中接触进 w.PendingContacts（③ 统一结算）；地形接触即时 Destroy。
    public static void Advance(SimWorld w, ProjectileState p, int tick, IReadOnlyList<FighterStateData> fighters)
    {
        if (p.Expired) return;

        // 可控弹跟随（念龙波类: 弹体方向 = 施法者当前朝向）
        if (p.Def is { } pd && pd.FollowHeading)
        {
            var caster = w.GetFighter(p.OwnerId);
            if (caster is not null && !caster.State.Equals(FighterState.Dead))
            {
                long speed = DeterministicMath.DivRoundHalfEven(p.Def.ProjSpeedQ, RuntimeConstants.TICK_RATE);
                DeterministicMath.CordicCosSin(caster.HeadingQuantum, out var fhx, out var fhz);
                p.DispX = DeterministicMath.MulShift(fhx, speed);
                p.DispZ = DeterministicMath.MulShift(fhz, speed);
            }
        }

        // lob 重力（Tick 边界量化增量——SPEC-0005 §3 公理）
        // DispY 为位移域（m/Tick raw）：重力引起每 Tick 位移增量 = g·ONE/3600
        if (p.IsLob)
        {
            p.DispY -= DeterministicMath.DivRoundHalfEven(RuntimeConstants.GRAVITY_MPS2 * Fixed.ONE,
                RuntimeConstants.TICK_RATE * RuntimeConstants.TICK_RATE);
        }

        // 地形扫掠（Projectile×Terrain = Destroy，SPEC-0005 §7）——确定性 (toi, terrainId) 最近接触
        var terrain = w.Collision.Terrain;
        long destroyedToi = -1; int destroyTerrainId = -1;
        for (int i = 0; i < terrain.Count; i++)
        {
            var body = terrain[i];
            if (body.Action != TerrainAction.DestroyProjectile && body.Action != TerrainAction.Stop &&
                body.Action != TerrainAction.Bounce) continue;
            if (body.Action == TerrainAction.PassThrough) continue;
            if (!SweepSolver.SweepRegion(body.Region, p.PosX, p.PosZ, p.DispX, p.DispZ, p.Radius,
                    out long toi, out _, out _, out _))
                continue;
            if (destroyedToi < 0 || toi < destroyedToi || (toi == destroyedToi && body.Id < destroyTerrainId))
            {
                destroyedToi = toi; destroyTerrainId = body.Id;
            }
        }
        if (destroyedToi >= 0)
        {
            p.PosX += DeterministicMath.MulShift(p.DispX, destroyedToi);
            p.PosZ += DeterministicMath.MulShift(p.DispZ, destroyedToi);
            p.PosY += DeterministicMath.MulShift(p.DispY, destroyedToi);
            Destroy(w, p, ProjectileEndReason.Terrain);
            return;
        }

        // Fighter 命中（相对扫掠，PA-7: 弹体为 mover，fighter 速度贡献取反）
        foreach (var f in fighters)
        {
            if (f.Id == p.OwnerId || f.State == FighterState.Dead) continue;
            if (w.SameTeam(p.OwnerId, f.Id)) continue;
            if (f.GrabbedBy >= 0) continue;   // 被抓取目标不再被其他来源命中（GDD §2.4.4）
            if (f.Hidden && f.Id != p.OwnerId) continue;   // Visibility: 潜行对敌方弹体不可见
            long relX = p.DispX - DeterministicMath.DivRoundHalfEven(f.VelX.Raw, RuntimeConstants.TICK_RATE);
            long relZ = p.DispZ - DeterministicMath.DivRoundHalfEven(f.VelZ.Raw, RuntimeConstants.TICK_RATE);
            long relY = p.DispY;

            // 头部（PA-H1.2 真 3D 球）: r = 头部半径 + 弹体半径
            long headCy = HurtboxModel.HeadCenterY(f.PosY.Raw);
            bool headHit = SweepSolver.SweepPointVsSphere3D(
                p.PosX, p.PosY, p.PosZ, relX, relY, relZ,
                f.PosX.Raw, headCy, f.PosZ.Raw,
                RuntimeConstants.HEAD_RADIUS + p.Radius,
                out long headToi, out _, out long hnx, out long hnz);

            // 躯干（SPEC-0005 §5.1 Point→AABB: 4 半平面 + mover 半径膨胀 + 高度带）——两区域都测（PA-H2 priority 选取）
            bool torsoHit = false; long torsoToi = -1, tnx = 0, tnz = 0;
            var torso = HurtboxModel.TorsoRegion(f.PosX.Raw, f.PosZ.Raw, f.HeadingQuantum);
            if (SweepSolver.SweepRegion(torso, p.PosX, p.PosZ, relX, relZ, p.Radius,
                    out long toi, out _, out long nx, out long nz))
            {
                // 高度带: 弹体高度（TOI 处）∈ [posY, posY+1.6]（双端 inclusive，PA-H1.1）
                long yAtToi = p.PosY + DeterministicMath.MulShift(relY, toi);
                if (yAtToi >= f.PosY.Raw && yAtToi <= f.PosY.Raw + RuntimeConstants.TORSO_TOP)
                {
                    torsoHit = true; torsoToi = toi; tnx = nx; tnz = nz;
                }
            }

            if (!headHit && !torsoHit) continue;
            if (p.HitVictims.Contains(f.Id)) continue;

            // 法术反射（KNI/WRK: 窗口内 magic 弹体反弹向施法者——OwnerId 转移+反向）
            if (f.ReflectTicks > 0 && p.Def is { } rdef && rdef.DamageType != "phys")
            {
                p.OwnerId = f.Id;
                p.DispX = -p.DispX;
                p.DispZ = -p.DispZ;
                p.HitVictims.Clear();
                p.HitVictims.Add(f.Id);   // 反射者不被自己的反弹立即命中
                w.Events.Emit(new SimEvent { Kind = EventKind.Reflected, AttackerId = f.Id, VictimId = p.OwnerId, SkillId = p.SkillRuntimeId, ValueRaw = p.Uid });
                return;   // 本 Tick 弹体折返——剩余交互归下一 Tick（确定性: 每 Tick 至多一次反射）
            }

            // PA-H2: 区域选取 = priority 最大（Head=20 > Torso=10）；HitPoint/Normal 取所选区域接触
            bool useHead = headHit;
            long toiSel = useHead ? headToi : torsoToi;
            long nxSel = useHead ? hnx : tnx, nzSel = useHead ? hnz : tnz;
            long px = p.PosX + DeterministicMath.MulShift(relX, toiSel);
            long py = useHead
                ? headCy
                : p.PosY + DeterministicMath.MulShift(relY, toiSel);
            long pz = p.PosZ + DeterministicMath.MulShift(relZ, toiSel);

            w.PendingContacts.Add(new PendingContact
            {
                ToiRaw = toiSel,
                LayerRank = 2,
                AttackerId = p.OwnerId,
                DefenderId = f.Id,
                HitboxUid = p.Uid,
                Region = useHead ? (byte)HitRegion.Head : (byte)HitRegion.Torso,
                Kind = (byte)ContactKind.CombatHit,
                SkillRuntimeId = p.SkillRuntimeId,
                SegmentIndex = 0,
                HitPointX = px, HitPointY = py, HitPointZ = pz,
                NormalX = nxSel, NormalZ = nzSel,
                FromProjectileUid = p.Uid,
            });
            p.HitVictims.Add(f.Id);
        }

        // 终态积分
        p.PosX += p.DispX;
        p.PosY += p.DispY;
        p.PosZ += p.DispZ;

        // 落地（lob: y ≤ 0 → 落点区域命中由 cast 侧 hitbox 表达；弹体消亡）
        if (p.IsLob && p.PosY <= 0)
        {
            p.PosY = 0;
            Destroy(w, p, ProjectileEndReason.Hit);
            return;
        }

        if (tick >= p.ExpireTick) Destroy(w, p, ProjectileEndReason.Lifetime);
    }

}

/// 待结算接触（③ 命中结算统一输入；排序键 = SPEC-0005 §6 总序）
public struct PendingContact
{
    public long ToiRaw;
    public byte LayerRank;
    public int AttackerId;
    public int DefenderId;
    public int HitboxUid;           // hitbox Uid / projectile Uid
    public byte Region;             // HitRegion
    public byte Kind;               // ContactKind
    public ushort SkillRuntimeId;
    public byte SegmentIndex;
    public long HitPointX, HitPointY, HitPointZ;
    public long NormalX, NormalZ;
    public int FromProjectileUid;   // 0 = 非投射物
    public int FromUnitUid;         // 0 = 非单位攻击
}
