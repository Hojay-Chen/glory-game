using System;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Data;

// PRODUCTION - Arena.Headless
// AI Bot（ADR-0007 同权契约）: 只读 Sim 公共状态 → 产 Command（与玩家同一条 CmdStream）。
// v1 二态 bot: 追身→攻击轮换；HP<30% 后撤。决策间隔 12T（反应时间），内部 RNG 固定种子（确定性）。
// 感知边界（ADR-0007 §2 ISelfView 精神）: 只读自己+可见敌方位置——不读对方指令/意图。
namespace Arena.Headless;

public sealed class AiBot
{
    private readonly int _selfId;
    private readonly int _enemyId;
    private readonly Random _rng;
    private int _nextDecisionTick;
    private int _plan;          // 0=接近 1=普攻 2=上挑 3=三段斩 4=后撤
    private const int AttackRangeQ = (int)(2.2f * 65536);

    public AiBot(int selfId, int enemyId, int seed) { _selfId = selfId; _enemyId = enemyId; _rng = new Random(seed); }

    public System.Collections.Generic.IEnumerable<Command> Produce(int tick, SimWorld world, RuntimeCatalog catalog)
    {
        var self = world.GetFighter(_selfId);
        var enemy = world.GetFighter(_enemyId);
        if (self is null || enemy is null || self.State == FighterState.Dead || enemy.State == FighterState.Dead)
            yield break;

        if (tick < _nextDecisionTick) yield break;
        _nextDecisionTick = tick + 12;

        long dx = enemy.PosX.Raw - self.PosX.Raw;
        long dz = enemy.PosZ.Raw - self.PosZ.Raw;
        long distSq = dx * dx + dz * dz;
        byte toward = DirQuantized(dx, dz);
        byte away = DirQuantized(-dx, -dz);

        // 低血后撤
        if (self.Hp * 100 < self.HpMax * 30 && _rng.Next(100) < 60)
        {
            _plan = 4;
            yield return new Command(_selfId, CmdKind.Move, 0, 0, away, 0);
            yield break;
        }

        if (distSq > (long)AttackRangeQ * AttackRangeQ)
        {
            _plan = 0;
            yield return new Command(_selfId, CmdKind.Move, 0, 0, toward, 0);
            yield break;
        }

        // 近身轮换（普通链为主 + 上挑/三段斩 插入）
        _plan = _rng.Next(100) switch
        {
            < 45 => 1,
            < 70 => 2,
            < 90 => 3,
            _ => 1,
        };
        switch (_plan)
        {
            case 2:
                if (CdReady(self, catalog, "BLA_T1_001"))
                    yield return new Command(_selfId, CmdKind.Skill, catalog.IdMap["BLA_T1_001"], 0, 0, 0);
                else
                    yield return new Command(_selfId, CmdKind.Basic, 0, 0, 0, 0);
                break;
            case 3:
                if (CdReady(self, catalog, "BLA_T2_001"))
                    yield return new Command(_selfId, CmdKind.Skill, catalog.IdMap["BLA_T2_001"], 0, 0, 0);
                else
                    yield return new Command(_selfId, CmdKind.Basic, 0, 0, 0, 0);
                break;
            default:
                yield return new Command(_selfId, CmdKind.Basic, 0, 0, 0, 0);
                break;
        }
    }

    private static byte DirQuantized(long dx, long dz)
    {
        // 8 向量化（角度 atan2 → 45° 步进；Sim 0=+Z 顺时针）
        double ang = Math.Atan2(dx, dz);                      // −π..π，0=+Z 逆时针正
        if (ang < 0) ang += Math.PI * 2;
        int idx = (int)Math.Round(ang / (Math.PI / 4)) % 8;
        return (byte)idx;
    }

    private static bool CdReady(FighterStateData f, RuntimeCatalog catalog, string skillId) =>
        !catalog.IdMap.TryGetValue(skillId, out var uid) || !f.Cooldowns.TryGetValue(uid, out var cd) || cd <= 0;
}
