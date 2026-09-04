using System;
using System.Collections.Generic;
using Arena.Core;
using Arena.Infra.Data;
using Arena.Core.Calc;

using Arena.Core.Sim;

// PRODUCTION - Arena.Infra
// AI Bot（ADR-0007 同权契约）: 只读 Sim 公共状态 → 产 Command（与玩家同一条 CmdStream，零特权）。
// v2 决策树（Part 3 裁定）: 追身→近身轮换（命中确认连段）→浮空追击→倒地压制→低血短暂拉开。
// 人格参数 Aggression 双 bot 产生节奏差（同权≠同行为）——KO 可达性的来源。
// 感知边界（ADR-0007 §2 ISelfView 精神）: 只读自己+可见敌方位置——不读对方指令/意图。
namespace Arena.Infra.Ai;

public sealed class AiBot
{
    private readonly int _selfId;
    private readonly int _enemyId;
    private readonly Random _rng;
    private readonly double _aggression;      // 0..1: 高=连段激进+不后撤
    private int _nextDecisionTick;
    private const long AttackRangeSq = (long)(2.2 * 65536) * (long)(2.2 * 65536);
    private const long CloseRangeSq = (long)(1.2 * 65536) * (long)(1.2 * 65536);

    public AiBot(int selfId, int enemyId, int seed, double aggression = 0.5)
    {
        _selfId = selfId;
        _enemyId = enemyId;
        _aggression = aggression;
        _rng = new Random(seed);
    }

    public IEnumerable<Command> Produce(int tick, SimWorld world, RuntimeCatalog catalog)
    {
        var self = world.GetFighter(_selfId);
        var enemy = world.GetFighter(_enemyId);
        if (self is null || enemy is null || self.State == FighterState.Dead || enemy.State == FighterState.Dead)
            yield break;

        // 敌人浮空/倒地/硬直 = 压制窗口（每 tick 追击指令——Sim 缓冲裁决节奏）
        if (enemy.State is FighterState.Launch or FighterState.Down or FighterState.Hitstun)
        {
            var chase = DirQuantized(enemy.PosX.Raw - self.PosX.Raw, enemy.PosZ.Raw - self.PosZ.Raw);
            yield return new Command(_selfId, CmdKind.Move, 0, 0, chase, 0);
            yield return new Command(_selfId, CmdKind.Basic, 0, 0, 0, 0);
            yield break;
        }

        if (tick < _nextDecisionTick) yield break;
        _nextDecisionTick = tick + 8 + _rng.Next(8);

        long dx = enemy.PosX.Raw - self.PosX.Raw;
        long dz = enemy.PosZ.Raw - self.PosZ.Raw;
        long distSq = dx * dx + dz * dz;
        byte toward = DirQuantized(dx, dz);
        ushort aim = (ushort)(toward * 8192);   // 施法/普攻朝向 = 指向敌人（SPEC-0001: 45°/8192）

        // 低血短暂拉开（非激进人格；窗口 20T）
        bool lowHp = self.Hp * 100 < self.HpMax * 25;
        if (lowHp && _rng.NextDouble() > _aggression && distSq < CloseRangeSq)
        {
            yield return new Command(_selfId, CmdKind.Move, 0, 0, DirQuantized(-dx * 2, -dz * 2), 0);
            _nextDecisionTick = tick + 20;
            yield break;
        }

        // 近身轮换: 普攻链为主，浮空确认后接上挑/三段斩（真实连段意识——非 spam）
        if (distSq > AttackRangeSq)
        {
            yield return new Command(_selfId, CmdKind.Move, 0, 0, toward, 0);
            yield break;
        }

        // 贴脸拉开一格（攻击距离边缘小步拉扯——真实格斗距离感）
        if (distSq < CloseRangeSq && _rng.NextDouble() > _aggression)
        {
            yield return new Command(_selfId, CmdKind.Move, 0, 0, DirQuantized(-dx, -dz), 0);
            yield break;
        }

        switch (_rng.Next(100))
        {
            case < 20 when CdReady(self, catalog, "BLA_T1_001"):    // 上挑（浮空启动）
                yield return new Command(_selfId, CmdKind.Skill, catalog.IdMap["BLA_T1_001"], aim, 0, 0);
                break;
            case < 32 when CdReady(self, catalog, "BLA_T2_001"):    // 三段斩
                yield return new Command(_selfId, CmdKind.Skill, catalog.IdMap["BLA_T2_001"], aim, 0, 0);
                break;
            default:
                yield return new Command(_selfId, CmdKind.Basic, 0, aim, 0, 0);
                break;
        }
    }

    private static byte DirQuantized(long dx, long dz)
    {
        // CordicAtan2 返回 heading quantum（0=+Z 顺时针）→ /8192 = 8 向 DirIndex
        long hq = DeterministicMath.CordicAtan2(dx, dz);
        return (byte)(hq / 8192);
    }

    private static bool CdReady(FighterStateData f, RuntimeCatalog catalog, string skillId) =>
        !catalog.IdMap.TryGetValue(skillId, out var uid) || !f.Cooldowns.TryGetValue(uid, out var cd) || cd <= 0;
}
