using System;
using System.Collections.Generic;
using Arena.Core.Calc;
using Arena.Core.Collision;
// PRODUCTION - Arena.Core
// Sim.Units——召唤兽/机械单位/分身实体（pre-adr §3-1 UnitSystem；GDD §14.12/§14.16）。
// 确定性纪律: UnitId 创建序递增（随 Snapshot）；同 Tick 处理按 Uid 升序；AI 目标选择
// 效用相同 → 按 FighterId 升序破平（pre-adr §3-1「效用分相同→按 UnitId 序」）。
// 单位攻击走 HitResolve（与技能命中同一裁决链）；移动走 IntegrateMove 统一路径。
namespace Arena.Core.Sim;

/// 单位出生规格（召唤技 RuntimeDef 派生——禁止按 skillId 分支：字段全部来自数据）
public sealed class UnitSpec
{
    public required string Label { get; init; }          // 诊断名（哥布林/雷精灵…）
    public required long Hp { get; init; }               // 特殊文本 HP900/HP1200 或基线
    public required long MoveSpeedMps { get; init; }     // 移速（WB 基线 4.5，飞行 5.5）
    public required long AttackRange { get; init; }      // 攻击距离（近战 1.2 / 投掷 8）
    public required int AttackCdTicks { get; init; }     // 攻击间隔（WB: 2s 基线）
    public required int LifetimeTicks { get; init; }     // 存在期（存在90s/60s 数据化）
    public required bool Flying { get; init; }           // 飞行（unit:飞行）
    public required bool Decoy { get; init; }            // 嘲讽/假身单位（不攻击）
    public required bool Stationary { get; init; }       // 不可移动（魔界之花）
    public required SkillRuntimeData AttackDef { get; init; }  // 攻击 = 召唤技自身 def（伤害/硬直/状态）
}

public sealed class UnitState
{
    public int Uid { get; set; }
    public int OwnerFighterId { get; set; }
    public byte Team { get; set; }
    public Fixed PosX { get; set; } = Fixed.Zero;
    public Fixed PosZ { get; set; } = Fixed.Zero;
    public long HeadingQuantum { get; set; }
    public long Hp { get; set; }
    public long HpMax { get; set; }
    public int AttackCdRemaining { get; set; }
    public int LifetimeRemaining { get; set; }
    public bool Expired { get; set; }
    public UnitSpec Spec { get; set; } = null!;
}

public static class UnitSystem
{
    /// 召唤（owner 前方 1.5m；召唤位容量由 ResourceSlots 消费）
    public static int Spawn(SimWorld w, FighterStateData owner, UnitSpec spec, int tick)
    {
        DeterministicMath.CordicCosSin(owner.HeadingQuantum, out var fx, out var fz);
        var unit = new UnitState
        {
            Uid = w.NextUnitUid(),
            OwnerFighterId = owner.Id,
            Team = owner.Team,
            PosX = Fixed.FromRaw(owner.PosX.Raw + DeterministicMath.MulShift(fx, FixedM(1.5m))),
            PosZ = Fixed.FromRaw(owner.PosZ.Raw + DeterministicMath.MulShift(fz, FixedM(1.5m))),
            HeadingQuantum = owner.HeadingQuantum,
            Hp = spec.Hp,
            HpMax = spec.Hp,
            LifetimeRemaining = spec.LifetimeTicks,
            Spec = spec,
        };
        w.Units.Add(unit);
        w.Events.Emit(new SimEvent { Kind = EventKind.UnitSpawned, AttackerId = owner.Id, ValueRaw = unit.Uid });
        return unit.Uid;
    }

    public static void Destroy(SimWorld w, UnitState u, UnitEndReason reason)
    {
        if (u.Expired) return;
        u.Expired = true;
        u.Hp = 0;
        w.Events.Emit(new SimEvent
        {
            Kind = EventKind.UnitDied, AttackerId = u.OwnerFighterId, VictimId = u.Uid,
            ReasonByte = (byte)reason, ValueRaw = u.Uid,
        });
    }

    public enum UnitEndReason : byte { Lifetime = 0, Killed = 1, Recall = 2, Cap = 3 }

    private static long FixedM(decimal m) => (long)Math.Round(m * 65536m, MidpointRounding.ToEven);

    /// 每 Tick 推进（ADR-0001 §3.2 ② 单位 AI，Uid 升序）：寿命 → 攻击冷却 → 目标选择 → 移动/攻击
    public static void Advance(SimWorld w, UnitState u, int tick, IReadOnlyList<FighterStateData> fighters)
    {
        if (u.Expired) return;

        // 寿命（存在期数据化）
        if (--u.LifetimeRemaining <= 0)
        {
            Destroy(w, u, UnitEndReason.Lifetime);
            return;
        }

        var spec = u.Spec;
        if (spec.Decoy) return;   // 嘲讽/假身单位: 存在即语义，无 AI

        // 目标选择: 最近可见敌方 Fighter（效用=距离；相同距离按 FighterId 升序破平——确定性）
        FighterStateData? target = null;
        long bestDistSq = long.MaxValue; int bestId = int.MaxValue;
        foreach (var f in fighters)
        {
            if (f.State == FighterState.Dead || f.GrabbedBy >= 0) continue;
            if (w.SameTeam(u.Team, f.Id)) continue;
            if (f.Hidden) continue;   // Visibility: 潜行不可被单位锁定
            long dx = f.PosX.Raw - u.PosX.Raw, dz = f.PosZ.Raw - u.PosZ.Raw;
            long d2 = dx * dx + dz * dz;
            if (d2 < bestDistSq || (d2 == bestDistSq && f.Id < bestId))
            {
                bestDistSq = d2; bestId = f.Id; target = f;
            }
        }
        if (target is null) return;

        long dist = DeterministicMath.ISqrt(bestDistSq);
        // 攻击（冷却就绪 + 射程内）: 走 HitResolve 同一裁决链
        if (u.AttackCdRemaining <= 0 && dist <= spec.AttackRange)
        {
            u.AttackCdRemaining = spec.AttackCdTicks;
            w.PendingContacts.Add(new PendingContact
            {
                ToiRaw = 0, LayerRank = 2,
                AttackerId = u.OwnerFighterId,       // 伤害归属召唤者面板（GDD 召唤兽面板挂主人）
                DefenderId = target.Id,
                HitboxUid = u.Uid, Region = (byte)HitRegion.Torso, Kind = (byte)ContactKind.CombatHit,
                SkillRuntimeId = spec.AttackDef.RuntimeId, SegmentIndex = 0,
                HitPointX = target.PosX.Raw, HitPointY = target.PosY.Raw + RuntimeConstants.TORSO_TOP / 2,
                HitPointZ = target.PosZ.Raw,
                NormalX = 0, NormalZ = 0,
                FromUnitUid = u.Uid,
            });
            return;
        }

        // 移动（Stationary 单位不动；IntegrateMove 统一路径——地形/边界一致）
        if (u.AttackCdRemaining > 0) u.AttackCdRemaining--;
        if (spec.Stationary || dist <= spec.AttackRange) return;
        DeterministicMath.Normalize(target.PosX.Raw - u.PosX.Raw, target.PosZ.Raw - u.PosZ.Raw, out var nx, out var nz);
        var move = w.Collision.IntegrateMove(u.PosX.Raw, u.PosZ.Raw,
            DeterministicMath.MulShift(nx, spec.MoveSpeedMps),
            DeterministicMath.MulShift(nz, spec.MoveSpeedMps),
            RuntimeConstants.FIGHTER_RADIUS, bounceEnabled: false);
        u.PosX = Fixed.FromRaw(move.FinalX);
        u.PosZ = Fixed.FromRaw(move.FinalZ);
        u.HeadingQuantum = HeadingFromDirection(nx, nz);
    }

    /// 单位朝向 = 移动方向（DirIndex 量化——受身判定同源粒度）
    public static long HeadingFromDirection(long nx, long nz)
    {
        // 归一化向量的方向量子（SPEC-0001: 0=+Z 顺时针）——纯整数 atan2 量化（64 向，1.4° 粒度）
        long h = HitResolve.DirIndexFromVel(nx, nz);
        return h * (65536 / 8);
    }
}
