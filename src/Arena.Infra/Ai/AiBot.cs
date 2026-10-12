using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Infra.Data;
using Arena.Core.Calc;

using Arena.Core.Sim;

// PRODUCTION - Arena.Infra
// AI Bot（ADR-0007 同权契约）: 只读 Sim 公共状态 → 产 Command（与玩家同一条 CmdStream，零特权）。
//
// 2026-10-11 重写要点：
//  1) **按本职业取技**：原实现把 `BLA_T1_001/BLA_T2_001` 写死，非剑客职业的 bot 会去按别人的技能，
//     结果是"既不理解自己、也打不出本职业的连段"。
//  2) **会追浮空**：敌人一旦离地就每 tick 顶一发续空（实机由 Sim 的取消窗/缓冲决定何时真正出招），
//     这样《荣耀》的空中连打才会真的出现在对局里，而不是只活在测试里。
//  3) **会取消**：自己在 `Act` 后摇里、且判定窗已过时，主动接下一发（cancel_min_tier 由 Sim 裁决，
//     普攻档位太低取消不了 T1+，所以这里只按技能——按普攻等于白按）。
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
    private string _classId = "";
    private List<SkillRuntimeData> _offense = new();   // 本职业的进攻技（不含普攻），按"浮空优先、CD 短优先"排
    private ushort _guardUid;            // 本职业格挡（Guard 盾值/hold 姿态）技；0=无
    private int _guardHoldTicks;         // 举盾剩余 tick（到 0 发 Release）
    private const long AttackRangeSq = (long)(2.2 * 65536) * (long)(2.2 * 65536);
    private const long GuardRangeSq = (long)(18.0 * 65536) * (long)(18.0 * 65536);
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

        EnsureSkills(catalog, self.ClassId);

        long dx = enemy.PosX.Raw - self.PosX.Raw;
        long dz = enemy.PosZ.Raw - self.PosZ.Raw;
        long distSq = dx * dx + dz * dz;
        byte toward = DirQuantized(dx, dz);
        ushort aim = (ushort)(toward * 8192);   // 施法/普攻朝向 = 指向敌人（SPEC-0001: 45°/8192）

        // ---- 防御 1：受身（Down 态翻滚=受身，方向对齐 FallDirIndex 才有效——GDD §5.6）----
        // 此前 bot 倒地只会干躺，整场 Ukemi=0；玩家方的 bot（autopilot）同理。
        if (self.State == FighterState.Down && !self.UkemiIneffective)
        {
            yield return new Command(_selfId, CmdKind.Roll, 0, 0, self.FallDirIndex, 0);
            yield break;
        }

        // ---- 防御 2：格挡（敌人起手 → 概率举盾；hold 由 Sim 持续）----
        // 距离放宽到 18m：枪手在 15m 外开火，只盯近距会让「对枪不举盾」——盾对弹体同样有效
        // （弹体命中也走 HitResolve 的格挡门）。盾被打光 → GuardBroken，防御侧博弈才会出现。
        if (enemy.State == FighterState.Act && distSq < GuardRangeSq &&
            _guardUid != 0 && _rng.NextDouble() < 0.30 + _aggression * 0.35)
        {
            if (self.State is FighterState.Normal or FighterState.Act &&
                (!self.Cooldowns.TryGetValue(_guardUid, out var gcd) || gcd <= 0))
            {
                yield return new Command(_selfId, CmdKind.Skill, _guardUid, aim, 0, 0);
                _guardHoldTicks = 20 + _rng.Next(25);   // 举 0.3~0.75s 后松开
                yield break;
            }
        }
        if (_guardHoldTicks > 0)
        {
            _guardHoldTicks--;
            if (_guardHoldTicks == 0)
                yield return new Command(_selfId, CmdKind.Basic, 0, 0, 0, 0);   // Release → 解除 hold
            yield break;
        }

        // ---- 压制窗口：敌人浮空 / 硬直 / 倒地 ----
        if (enemy.State is FighterState.Launch or FighterState.Hitstun or FighterState.Down)
        {
            yield return new Command(_selfId, CmdKind.Move, 0, 0, toward, 0);

            // 自己在出招中：只按**能取消**的技能（普攻取消不了 T1+ 后摇，按了也白按）
            if (self.State == FighterState.Act)
            {
                if (TryPickOffense(self, tierAtLeast: 1, out var cancel))
                {
                    yield return cancel;
                    yield break;
                }
            }
            // 空中追击 / 倒地压制：每 tick 顶一发，实机由 Sim 的取消窗与输入缓冲裁决节奏
            else if (enemy.State == FighterState.Launch && distSq <= AttackRangeSq)
            {
                if (TryPickOffense(self, tierAtLeast: 1, out var air))
                {
                    yield return air;
                    yield break;
                }
                yield return new Command(_selfId, CmdKind.Basic, 0, aim, 0, 0);
            }
            else
            {
                yield return new Command(_selfId, CmdKind.Basic, 0, aim, 0, 0);
            }
            yield break;
        }

        if (tick < _nextDecisionTick) yield break;
        _nextDecisionTick = tick + 8 + _rng.Next(8);

        // 低血短暂拉开（非激进人格；窗口 20T）
        bool lowHp = self.Hp * 100 < self.HpMax * 25;
        if (lowHp && _rng.NextDouble() > _aggression && distSq < CloseRangeSq)
        {
            yield return new Command(_selfId, CmdKind.Move, 0, 0, DirQuantized(-dx * 2, -dz * 2), 0);
            _nextDecisionTick = tick + 20;
            yield break;
        }

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

        // 近身：**先起浮空，再续空**（原实现是纯随机轮换，连不起来）
        if (TryPickLaunch(self, out var launcher))
        {
            yield return launcher;
            yield break;
        }
        if (_rng.Next(100) < 55 && TryPickOffense(self, tierAtLeast: 0, out var off))
        {
            yield return off;
            yield break;
        }
        yield return new Command(_selfId, CmdKind.Basic, 0, aim, 0, 0);
    }

    private void EnsureSkills(RuntimeCatalog catalog, string classId)
    {
        if (_classId == classId && _offense.Count > 0) return;
        _classId = classId;
        _offense = catalog.Skills
            .Where(s => s.ClassId == classId && !s.Type.StartsWith("basic") && !s.IsHold && s.Guard is null)
            .OrderByDescending(s => s.LaunchVelQ > 0)   // 浮空技优先（连段的起手）
            .ThenBy(s => s.CooldownTicks)
            .ToList();
        _guardUid = catalog.Skills
            .Where(s => s.ClassId == classId && (s.Guard is not null || s.IsHold))
            .OrderByDescending(s => s.Guard is not null)
            .ThenBy(s => s.SkillId, StringComparer.Ordinal)
            .FirstOrDefault()?.RuntimeId ?? 0;
    }

    /// 取一发可用进攻技；tierAtLeast 用于"取消"场景（低档取消不了高档后摇）
    private bool TryPickOffense(FighterStateData self, byte tierAtLeast, out Command cmd)
    {
        foreach (var s in _offense)
        {
            if (s.Tier < tierAtLeast) continue;
            if (self.Mp < s.MpCost) continue;
            if (self.Cooldowns.TryGetValue(s.RuntimeId, out var cd) && cd > 0) continue;
            cmd = new Command(_selfId, CmdKind.Skill, s.RuntimeId, 0, 0, 0);
            return true;
        }
        cmd = default;
        return false;
    }

    private bool TryPickLaunch(FighterStateData self, out Command cmd)
    {
        foreach (var s in _offense)
        {
            if (s.LaunchVelQ <= 0) continue;
            if (self.Mp < s.MpCost) continue;
            if (self.Cooldowns.TryGetValue(s.RuntimeId, out var cd) && cd > 0) continue;
            cmd = new Command(_selfId, CmdKind.Skill, s.RuntimeId, 0, 0, 0);
            return true;
        }
        cmd = default;
        return false;
    }

    private static byte DirQuantized(long dx, long dz)
    {
        // CordicAtan2 返回 heading quantum（0=+Z 顺时针）→ /8192 = 8 向 DirIndex
        long hq = DeterministicMath.CordicAtan2(dx, dz);
        return (byte)(hq / 8192);
    }
}
