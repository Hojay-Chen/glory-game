using System;
using System.Collections.Generic;
using System.Linq;

namespace Arena.Core.Sim;

// ---- ADR-0001 §3/ADR-0003: 稳定 ID 与状态枚举 ----

public enum FighterState : byte
{
    Normal = 0, Act = 1, Hitstun = 2, Launch = 3, Down = 4,
    Getup = 5, Break = 6, Grabbed = 7, Dead = 8
}

// ADR-0001 §7.1 优先级: Dead > Break > Grabbed > Down > Launch > Hitstun > Act > Normal
public static class FighterStatePriority
{
    private static readonly byte[] Rank = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
    public static bool CanOverride(FighterState current, FighterState next) => Rank[(int)next] >= Rank[(int)current];
}

// ---- Command (ADR-0001/0010/SPEC-0001) ----

public enum CmdKind : byte { Move = 0, Jump = 1, Roll = 2, Skill = 3, Basic = 4, ForceCancel = 5, Steer = 6 }

public readonly record struct Command(
    CmdKind Kind,
    ushort SkillId,
    ushort AimQuantum,
    byte DirIndex,
    int TargetTick)
{
    public static readonly Command None = default;
}

// ---- SimEvent (ADR-0003 封闭枚举，v1 压缩到战斗核心) ----

public enum EventKind : byte
{
    SkillCast = 0, Hit = 1, Launched = 2, Landed = 3, ForcedDown = 4,
    Ukemi = 5, WallBounced = 6, BreakTriggered = 7, Died = 8,
    FloatProtect = 9, WhiffDown = 10, Cancelled = 11, ActEnded = 12
}

public readonly record struct SimEvent(
    long Tick, ushort SeqInTick, EventKind Kind,
    int AttackerId, int VictimId,
    ushort SkillId, byte SegmentIndex,
    long DamageRaw, int HitNumber,
    byte HitRegion,
    long HitPointX, long HitPointY, long HitPointZ,
    long HitNormalX, long HitNormalZ,
    byte VictimStateBefore, long PosY,
    bool SweepFlag, bool AirMod)
{
    public long EventId => Tick << 16 | SeqInTick;
}

// ---- Fighter (ADR-0001 §8.2 Fighter 域) ----

public sealed class FighterStateData
{
    public int Id { get; set; }
    public string ClassId { get; set; } = "";
    public Fixed PosX { get; set; } = Fixed.Zero;
    public Fixed PosY { get; set; } = Fixed.Zero;
    public Fixed PosZ { get; set; } = Fixed.Zero;
    public Fixed VelX { get; set; } = Fixed.Zero;
    public Fixed VelY { get; set; } = Fixed.Zero;
    public Fixed VelZ { get; set; } = Fixed.Zero;
    public long HeadingQuantum { get; set; }  // SPEC-0001 AimQuantum
    public FighterState State { get; set; } = FighterState.Normal;
    public int StateTicksRemaining { get; set; }
    public long Hp { get; set; } = 10000;
    public long Mp { get; set; } = 1000;
    public long Stamina { get; set; } = 100;
    public long ControlValue { get; set; }
    public long Atk { get; set; } = 1100;

    // 连段纪元（ADR-0001 §8 Combo）
    public int HitstunCount { get; set; }
    public int LaunchCount { get; set; }
    public long AirTime { get; set; }  // Fixed
    public bool ForcedFall { get; set; }
    public bool NoUkemi { get; set; }
    public long DownTicks { get; set; }
    public long ProtectTicks { get; set; }

    // Act（技能执行中）
    public string ActiveSkillId { get; set; } = "";
    public int ActivePhaseTick { get; set; }  // 0-based from cast
    public byte ActivePhase { get; set; }     // 0=startup 1=active 2=recovery
    public bool HitConfirmed { get; set; }
    public int ChainN { get; set; }

    // CD 表（skill_id hash → 剩余 tick）
    public Dictionary<long, long> Cooldowns { get; } = new();

    // 命中过的 (victimId, segmentIdx)——multi-hit 去重
    public HashSet<long> HitTargets { get; } = new();

    public FighterStateData Clone()
    {
        var c = new FighterStateData
        {
            Id = Id, ClassId = ClassId,
            PosX = PosX, PosY = PosY, PosZ = PosZ,
            VelX = VelX, VelY = VelY, VelZ = VelZ,
            HeadingQuantum = HeadingQuantum,
            State = State, StateTicksRemaining = StateTicksRemaining,
            Hp = Hp, Mp = Mp, Stamina = Stamina, ControlValue = ControlValue, Atk = Atk,
            HitstunCount = HitstunCount, LaunchCount = LaunchCount,
            AirTime = AirTime, ForcedFall = ForcedFall, NoUkemi = NoUkemi,
            DownTicks = DownTicks, ProtectTicks = ProtectTicks,
            ActiveSkillId = ActiveSkillId, ActivePhaseTick = ActivePhaseTick,
            ActivePhase = ActivePhase, HitConfirmed = HitConfirmed, ChainN = ChainN,
        };
        foreach (var kv in Cooldowns) c.Cooldowns[kv.Key] = kv.Value;
        foreach (var h in HitTargets) c.HitTargets.Add(h);
        return c;
    }
}
