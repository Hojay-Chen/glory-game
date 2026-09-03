using System;
using System.Collections.Generic;
using Arena.Core.Calc;
using Arena.Core.Collision;
// PRODUCTION - Arena.Core
// Sim.Units——召唤兽/机械单位/分身/部署物实体（pre-adr §3-1 UnitSystem）。
// 确定性纪律: UnitId 创建序递增（随 Snapshot）；同 Tick 处理按 Uid 升序；AI 目标选择
// 效用相同 → 按 FighterId 升序破平。单位攻击走 HitResolve（同一裁决链）；移动走 IntegrateMove。
namespace Arena.Core.Sim;

/// 单位出生规格（召唤/部署技 RuntimeDef 派生——禁止按 skillId 分支：字段全部来自数据）
public sealed class UnitSpec
{
    public required string Label { get; init; }
    public required long Hp { get; init; }
    public required long MoveSpeedMps { get; init; }
    public required long AttackRange { get; init; }
    public required int AttackCdTicks { get; init; }
    public required int LifetimeTicks { get; init; }
    public required bool Flying { get; init; }
    public required bool Decoy { get; init; }
    public required bool Stationary { get; init; }
    public required SkillRuntimeData AttackDef { get; init; }
    // ---- Deploy 载荷（Phase 7 Batch 2——统一实体语义） ----
    public DeployKind DeployKind { get; init; }
    public long TriggerRadius { get; init; }
    public long AuraRadius { get; init; }
    public int AuraPulseIntervalTicks { get; init; }
    public long ZoneFireWeaknessQ { get; init; }   // Zone 受火伤 +P%（魔界之花类——数据驱动元素弱点）
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
    public int AuraPulseTimer { get; set; }
    public bool Triggered { get; set; }
    public UnitSpec Spec { get; set; } = null!;
}

public static class UnitSystem
{
    /// Zone 受火伤弱点（数据: 受火伤+50%——魔界之花类元素弱点，数据驱动）
    private static long ParseZoneFireWeakness(string special)
    {
        var idx = special.IndexOf("受火伤+");
        if (idx < 0) return 0;
        var rest = special[(idx + 4)..];
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
        return end > 0 && decimal.TryParse(rest[..end], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (long)(v / 100m * 65536m) : 0;
    }

    /// 单位规格派生（单一事实源——cast 与 Snapshot 恢复共用；全字段确定性派生自 def）
    public static UnitSpec BuildFromSkillDef(SkillRuntimeData def)
    {
        bool isDeploy = def.DeployKind != DeployKind.None;
        return new UnitSpec
        {
            Label = def.SkillId,
            Hp = isDeploy && def.DeployHp > 0 ? def.DeployHp : def.SummonHp,
            MoveSpeedMps = FixedM(4.5m),
            AttackRange = FixedM(8m),      // 投掷基线（哥布林扔石头）
            AttackCdTicks = 2 * (int)RuntimeConstants.TICK_RATE,
            // Zone 生命周期 = act 借位效果持续（DDQ-B5-7 裁定: Zone = 独立 Deploy 实体生命周期）
            LifetimeTicks = def.DeployKind == DeployKind.Zone && def.EffectDurationTicks > 0
                ? def.EffectDurationTicks : def.SummonLifetimeTicks,
            Flying = def.SummonFlying,
            Decoy = def.IsDecoy,
            Stationary = isDeploy || def.SkillId.Contains("魔界之花"),
            AttackDef = def,
            DeployKind = def.DeployKind,
            TriggerRadius = def.TriggerRadius,
            AuraRadius = def.AuraRadius > 0 ? def.AuraRadius : FixedM(4m),
            AuraPulseIntervalTicks = def.AuraPulseIntervalTicks,
            ZoneFireWeaknessQ = ParseZoneFireWeakness(def.Special),
        };
    }

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

    /// 每 Tick 推进（ADR-0001 §3.2 ② 单位 AI，Uid 升序）
    public static void Advance(SimWorld w, UnitState u, int tick, IReadOnlyList<FighterStateData> fighters)
    {
        if (u.Expired) return;

        if (--u.LifetimeRemaining <= 0)
        {
            Destroy(w, u, UnitEndReason.Lifetime);
            return;
        }

        var spec = u.Spec;

        // Deploy 载荷推进（陷阱触发 / 光环脉冲 / 静置存在）
        if (spec.DeployKind != DeployKind.None)
        {
            AdvanceDeploy(w, u, spec, fighters);
            return;
        }

        if (spec.Decoy) return;

        // 目标选择: 最近可见敌方 Fighter（效用=距离；同距按 FighterId 升序破平）
        FighterStateData? target = null;
        long bestDistSq = long.MaxValue; int bestId = int.MaxValue;
        foreach (var f in fighters)
        {
            if (f.State == FighterState.Dead || f.GrabbedBy >= 0) continue;
            if (w.SameTeam(u.Team, f.Id)) continue;
            if (f.Hidden) continue;
            long dx = f.PosX.Raw - u.PosX.Raw, dz = f.PosZ.Raw - u.PosZ.Raw;
            long d2 = dx * dx + dz * dz;
            if (d2 < bestDistSq || (d2 == bestDistSq && f.Id < bestId))
            {
                bestDistSq = d2; bestId = f.Id; target = f;
            }
        }
        if (target is null) return;

        long dist = DeterministicMath.ISqrt(bestDistSq);
        if (u.AttackCdRemaining <= 0 && dist <= spec.AttackRange)
        {
            u.AttackCdRemaining = spec.AttackCdTicks;
            w.PendingContacts.Add(new PendingContact
            {
                ToiRaw = 0, LayerRank = 2,
                AttackerId = u.OwnerFighterId,
                DefenderId = target.Id,
                HitboxUid = u.Uid, Region = (byte)HitRegion.Torso, Kind = (byte)ContactKind.CombatHit,
                SkillRuntimeId = spec.AttackDef.RuntimeId, SegmentIndex = 0,
                HitPointX = target.PosX.Raw, HitPointY = target.PosY.Raw + RuntimeConstants.TORSO_TOP / 2,
                HitPointZ = target.PosZ.Raw,
                NormalX = 0, NormalZ = 0, FromUnitUid = u.Uid,
            });
            return;
        }

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

    /// Deploy 载荷推进（陷阱单次触发 / 光环周期脉冲 / 静置存在语义）
    private static void AdvanceDeploy(SimWorld w, UnitState u, UnitSpec spec, IReadOnlyList<FighterStateData> fighters)
    {
        // Zone（DDQ-B5-7 裁定: 独立 Deploy 实体——耐久可被摧毁 + 周期效果脉冲复用 Aura 语义）
        if (spec.DeployKind == DeployKind.Zone)
        {
            // 第一层: 耐久承伤——敌方判定体重叠 Zone 中心即命中（伤害 = mult × ownerAtk，v1 简化: 无防御/部位——DDQ 登记）
            foreach (var hb in w.Hitboxes)
            {
                if (w.Tick < hb.SpawnTick || w.Tick >= hb.ExpireTick) continue;
                var hOwner = w.GetFighter(hb.OwnerId);
                if (hOwner is null || hOwner.State == FighterState.Dead || w.SameTeam(hOwner.Id, u.Team)) continue;
                if (hb.Def is not { } hdef || hdef.DamageMultQ <= 0) continue;
                long dx = hb.AnchorX - u.PosX.Raw, dz = hb.AnchorZ - u.PosZ.Raw;
                long reach = spec.AuraRadius + 3 * Fixed.ONE;   // Zone 半径 + 判定体前伸余量
                if (dx * dx + dz * dz > reach * reach) continue;
                long dmg = DeterministicMath.MulShift(hdef.DamageMultQ, hOwner.Atk);
                if (hdef.DamageType == "fire" && spec.ZoneFireWeaknessQ > 0)
                    dmg = DeterministicMath.MulShift(dmg, Fixed.ONE + spec.ZoneFireWeaknessQ);   // 受火伤+50%（魔界之花类）
                u.Hp -= dmg;
                w.Events.Emit(new SimEvent { Kind = EventKind.UnitDied, AttackerId = hOwner.Id, VictimId = u.Uid, DamageRaw = dmg, ReasonByte = 9 });
            }
            if (u.Hp <= 0)
            {
                Destroy(w, u, UnitEndReason.Recall);
                return;
            }
            // 第二层: 效果脉冲（有伤害/状态 → 敌方脉冲；纯防御罩 → 静默存在，吸收语义 DDQ-B7-3）
            if (spec.AttackDef.DamageMultQ > 0 || spec.AttackDef.Statuses.Length > 0)
                ZonePulse(w, u, spec, fighters);
            return;
        }

        // 陷阱: 敌方进入触发半径 → 单次爆发 → 自毁
        if (spec.DeployKind == DeployKind.Trap)
        {
            if (u.Triggered) return;
            foreach (var f in fighters)
            {
                if (f.State == FighterState.Dead || f.Hidden || w.SameTeam(u.Team, f.Id) || f.GrabbedBy >= 0) continue;
                long dx = f.PosX.Raw - u.PosX.Raw, dz = f.PosZ.Raw - u.PosZ.Raw;
                long rr = spec.TriggerRadius + RuntimeConstants.FIGHTER_RADIUS;
                if (dx * dx + dz * dz > rr * rr) continue;
                u.Triggered = true;
                w.PendingContacts.Add(new PendingContact
                {
                    ToiRaw = 0, LayerRank = 2, AttackerId = u.OwnerFighterId, DefenderId = f.Id,
                    HitboxUid = u.Uid, Region = (byte)HitRegion.Torso, Kind = (byte)ContactKind.CombatHit,
                    SkillRuntimeId = spec.AttackDef.RuntimeId, SegmentIndex = 0,
                    HitPointX = f.PosX.Raw, HitPointY = f.PosY.Raw + RuntimeConstants.TORSO_TOP / 2, HitPointZ = f.PosZ.Raw,
                    NormalX = 0, NormalZ = 0, FromUnitUid = u.Uid,
                });
                Destroy(w, u, UnitEndReason.Recall);
                return;
            }
            return;
        }

        // 光环: 周期脉冲（敌伤/己益由 def 数据域决定: 无伤害无状态 = 己方增益阵）
        if (spec.DeployKind == DeployKind.Aura)
        {
            if (--u.AuraPulseTimer > 0) return;
            u.AuraPulseTimer = spec.AuraPulseIntervalTicks;
            bool buffAura = spec.AttackDef.DamageMultQ == 0 && spec.AttackDef.Statuses.Length == 0;
            foreach (var f in fighters)
            {
                if (f.State == FighterState.Dead || f.Hidden || f.GrabbedBy >= 0) continue;
                bool ally = w.SameTeam(u.Team, f.Id);
                if (buffAura != ally) continue;
                long dx = f.PosX.Raw - u.PosX.Raw, dz = f.PosZ.Raw - u.PosZ.Raw;
                long rr = spec.AuraRadius + RuntimeConstants.FIGHTER_RADIUS;
                if (dx * dx + dz * dz > rr * rr) continue;
                if (buffAura)
                {
                    f.BuffAtkPctTicks = u.LifetimeRemaining;
                    f.BuffAtkPctQ = FixedM(0.05m);   // 数据: 刀魂守护阵内 ATK+5%
                    w.Events.Emit(new SimEvent { Kind = EventKind.BuffApplied, VictimId = f.Id, SkillId = spec.AttackDef.RuntimeId, ValueRaw = f.BuffAtkPctQ });
                }
                else if (spec.AttackDef.DamageMultQ > 0 || spec.AttackDef.Statuses.Length > 0)
                {
                    w.PendingContacts.Add(new PendingContact
                    {
                        ToiRaw = 0, LayerRank = 2, AttackerId = u.OwnerFighterId, DefenderId = f.Id,
                        HitboxUid = u.Uid, Region = (byte)HitRegion.Torso, Kind = (byte)ContactKind.CombatHit,
                        SkillRuntimeId = spec.AttackDef.RuntimeId, SegmentIndex = 0,
                        HitPointX = f.PosX.Raw, HitPointY = f.PosY.Raw + RuntimeConstants.TORSO_TOP / 2, HitPointZ = f.PosZ.Raw,
                        NormalX = 0, NormalZ = 0, FromUnitUid = u.Uid,
                    });
                }
            }
            return;
        }

        // Wall/Scout/Mirror/Taunt: 静置存在语义（镜面反射由弹体 ReflectTicks 路径消费——登记）
    }

    /// 单位朝向 = 移动方向（8 向 DirIndex 量化）
    public static long HeadingFromDirection(long nx, long nz)
    {
        long h = HitResolve.DirIndexFromVel(nx, nz);
        return h * (65536 / 8);
    }

/// Zone 效果脉冲（第二层 Effect: 范围检测→目标过滤→Effect 应用——DDQ-B5-7 裁定分层）
private static void ZonePulse(SimWorld w, UnitState u, UnitSpec spec, IReadOnlyList<FighterStateData> fighters)
{
    if (--u.AuraPulseTimer > 0) return;
    u.AuraPulseTimer = spec.AuraPulseIntervalTicks;
    foreach (var f in fighters)
    {
        if (f.State == FighterState.Dead || f.Hidden || f.GrabbedBy >= 0) continue;
        if (w.SameTeam(u.Team, f.Id)) continue;   // Zone 效果脉冲: 敌方方向（念气罩类静默罩不进入此路径）
        long dx = f.PosX.Raw - u.PosX.Raw, dz = f.PosZ.Raw - u.PosZ.Raw;
        long rr = spec.AuraRadius + RuntimeConstants.FIGHTER_RADIUS;
        if (dx * dx + dz * dz > rr * rr) continue;
        w.PendingContacts.Add(new PendingContact
        {
            ToiRaw = 0, LayerRank = 2, AttackerId = u.OwnerFighterId, DefenderId = f.Id,
            HitboxUid = u.Uid, Region = (byte)HitRegion.Torso, Kind = (byte)ContactKind.CombatHit,
            SkillRuntimeId = spec.AttackDef.RuntimeId, SegmentIndex = 0,
            HitPointX = f.PosX.Raw, HitPointY = f.PosY.Raw + RuntimeConstants.TORSO_TOP / 2, HitPointZ = f.PosZ.Raw,
            NormalX = 0, NormalZ = 0, FromUnitUid = u.Uid,
        });
    }
}

}