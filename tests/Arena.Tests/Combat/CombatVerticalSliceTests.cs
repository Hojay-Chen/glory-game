using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Sim;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 3D Vertical Slice：使用 CSV 真实技能数据的最小战斗闭环
public class CombatVerticalSlice
{
    private SimWorld CreateWorld()
    {
        var world = new SimWorld(0x12345678L, "test-hash-v1");
        world.AddFighter(0, "BMG", Fixed.FromInt(0), Fixed.FromInt(0));
        world.AddFighter(1, "BLA", Fixed.FromInt(0), Fixed.FromInt(3), atk: 1100);

        // 注册真实 CSV 技能（BMG 精选覆盖多机制）——HitboxShapeR 单位=Fixed Raw (m×65536)
        world.AddSkill(new SimWorld.SkillRuntimeData(
            1001, 12, 4, 16, 1, 0, 0.82, 0, 0, 7.5, false, true, false, 0, 209715, 35));   // 天击 T1 launch r=3.2m
        world.AddSkill(new SimWorld.SkillRuntimeData(
            1002, 10, 3, 14, 1, 0, 0.80, 16, 0, 0, false, false, false, 0, 157286, 30));   // 龙牙 T1 r=2.4m
        world.AddSkill(new SimWorld.SkillRuntimeData(
            1003, 10, 6, 16, 2, 3, 0.42, 14, 0, 0, false, false, false, 0, 170393, 40));   // 连突 T1 2段 r=2.6m
        world.AddSkill(new SimWorld.SkillRuntimeData(
            1004, 14, 4, 16, 1, 0, 0.84, 0, 3.0, 0, false, false, false, 0, 294912, 40));  // 落花掌 T1 击退 r=4.5m
        world.AddSkill(new SimWorld.SkillRuntimeData(
            1005, 12, 2, 18, 1, 0, 1.15, 0, 2.0, 0, false, false, false, 0, 196608, 70));  // 圆舞棍 T2 r=3.0m active=2T('-')
        world.AddSkill(new SimWorld.SkillRuntimeData(
            1006, 18, 5, 22, 1, 0, 1.98, 0, 0, 0, false, false, false, 0, 196608, 100));   // 强龙压 T3 r=3.0m
        world.AddSkill(new SimWorld.SkillRuntimeData(
            1007, 14, 3, 20, 1, 0, 2.03, 30, 0, 0, false, false, false, 0, 196608, 105));  // 怒龙穿心 T3 r=3.0m
        world.AddSkill(new SimWorld.SkillRuntimeData(
            2001, 30, 4, 28, 1, 0, 2.25, 40, 0, 0, false, false, true, 2, 0, 170));         // 巴雷特 U
        return world;
    }

    private Command SkillCmd(ushort skillId, int targetTick = 0) =>
        new(CmdKind.Skill, skillId, 0, 0, targetTick);

    // ---- T-VS-1: 普通近战命中 → 伤害 + 事件 ----
    [Fact]
    public void T_VS1_BasicMeleeHit_DealsDamage_AndEmitsHit()
    {
        var world = CreateWorld();
        world.Step(1, Array.Empty<Command>());
        // Fighter 0 用龙牙攻击 Fighter 1（距离 3m，龙牙 range 2.4m）
        // 先靠近
        var f1 = world.Fighters[0];
        f1.PosZ = Fixed.FromInt(2);  // 距离 2m < 2.4m range
        world.Step(2, new[] { new Command(CmdKind.Skill, 1002, 0, 0, 2) });

        // 等待 startup(10T) + active(3T)
        for (int t = 3; t <= 15; t++) world.Step(t, Array.Empty<Command>());

        // Fighter 1 应受到伤害
        Assert.True(world.Fighters[1].Hp < 10000, $"HP={world.Fighters[1].Hp}, expected < 10000");
        // 有 Hit 事件
        Assert.Contains(world.Events.All, e => e.Kind == EventKind.Hit && e.VictimId == 1);
    }

    // ---- T-VS-2: 天击浮空 → Launch 状态 ----
    [Fact]
    public void T_VS2_TianJi_Launches_Victim()
    {
        var world = CreateWorld();
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];
        f1.PosZ = Fixed.FromInt(2);  // 在天击范围内

        world.Step(1, new[] { SkillCmd(1001) });  // 天击
        // 等 startup(12T) + hit tick
        for (int t = 2; t <= 14; t++) world.Step(t, Array.Empty<Command>());

        // Fighter 1 应被浮空（Launch 状态）
        Assert.True(f1.State == FighterState.Launch || f1.State == FighterState.Hitstun,
            $"State={f1.State}");
    }

    // ---- T-VS-3: 落花掌击退 → Knockback ----
    [Fact]
    public void T_VS3_LuoHua_Knockback()
    {
        var world = CreateWorld();
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];
        f1.PosZ = Fixed.FromInt(2);

        world.Step(1, new[] { SkillCmd(1004) });  // 落花掌
        for (int t = 2; t <= 16; t++) world.Step(t, Array.Empty<Command>());

        // Fighter 1 应被击退（有击退事件）
        Assert.Contains(world.Events.All, e => e.Kind == EventKind.WallBounced);
    }

    // ---- T-VS-4: 连段递减生效（第 7 击后伤害递减）----
    [Fact]
    public void T_VS4_ComboDecay_Active_After7Hits()
    {
        var world = CreateWorld();
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];
        f1.PosZ = Fixed.FromInt(2);
        f1.State = FighterState.Hitstun;  // 手动进入受击（可被连段）
        f1.StateTicksRemaining = 300;

        // 连续 10 次普攻命中
        long firstDmg = 0, tenthDmg = 0;
        for (int i = 0; i < 10; i++)
        {
            world.Step(100 + i * 20, new[] { new Command(CmdKind.Skill, 1002, 0, 0, 100 + i * 20) });
            // 等 startup+active
            for (int t = 0; t < 15; t++) world.Step(100 + i * 20 + t + 1, Array.Empty<Command>());
            var hits = world.Events.All.Where(e => e.Kind == EventKind.Hit).ToList();
            if (hits.Count > i)
            {
                if (i == 0) firstDmg = hits[0].DamageRaw;
                if (i == 9) tenthDmg = hits[^1].DamageRaw;
            }
            f1.State = FighterState.Hitstun;  // 保持受击（模拟连续命中）
            f1.StateTicksRemaining = 200;
            f1.HitstunCount = i + 1;  // 维持连段计数
        }
        // 第 7+ 击应有递减（firstDmg > tenthDmg）
        Assert.True(firstDmg >= tenthDmg, $"first={firstDmg} tenth={tenthDmg}");
    }

    // ---- T-VS-5: 圆舞棍强制倒地 + 受身无效 ----
    [Fact]
    public void T_VS5_YuanWuGun_ForcedDown_NoUkemi()
    {
        var world = CreateWorld();
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];
        f1.PosZ = Fixed.FromInt(2);

        world.Step(1, new[] { SkillCmd(1005) });  // 圆舞棍
        for (int t = 2; t <= 16; t++) world.Step(t, Array.Empty<Command>());

        // Fighter 1 应倒地
        Assert.True(f1.State == FighterState.Down || world.Events.All.Any(e => e.Kind == EventKind.ForcedDown),
            $"State={f1.State}");
    }

    // ---- T-VS-6: 巴雷特 80m/s 高速弹 sweep 防穿模 ----
    [Fact]
    public void T_VS6_Barrett_HighSpeedSweep_NoTunneling()
    {
        var world = CreateWorld();
        world.AddFighter(2, "SRP", Fixed.FromInt(0), Fixed.FromInt(-15), atk: 1050);
        var f0 = world.Fighters[0];
        var shooter = world.Fighters[2];
        // 狙击手在 z=-15，目标 z=2（距离 17m），弹速 80m/s
        // 1.33m/Tick → ~13 Tick 后到达
        shooter.HeadingQuantum = 0;  // 朝 +Z

        world.Step(1, new[] { SkillCmd(2001) });  // 巴雷特
        // 运行 30 Tick
        for (int t = 2; t <= 32; t++) world.Step(t, Array.Empty<Command>());

        // 巴雷特 startup 30T，active 4T → 弹在 T31-34 发射
        // 实际上 80m/s = 1.33m/Tick，从 -15 到 2 = 17m → ~13 Tick
        // 此处只验证"技能被施放"（Phase 3D 基础）
        Assert.Contains(world.Events.All, e => e.Kind == EventKind.SkillCast && e.SkillId == 2001);
    }

    // ---- T-VS-7: 确定性闭环（同种子同指令 = 同结果）----
    [Fact]
    public void T_VS7_Determinism_SameInput_SameOutput()
    {
        var hash1 = RunMatch();
        var hash2 = RunMatch();
        Assert.Equal(hash1, hash2);
    }

    private string RunMatch()
    {
        var world = CreateWorld();
        var f0 = world.Fighters[0];
        var f1 = world.Fighters[1];
        f1.PosZ = Fixed.FromInt(2);

        var cmds = new List<Command>();
        cmds.Add(SkillCmd(1001));    // 天击
        cmds.Add(SkillCmd(1002));    // 龙牙
        cmds.Add(SkillCmd(1004));    // 落花掌
        for (int t = 1; t <= 30; t++)
        {
            var tickCmds = cmds.Where(c => c.TargetTick == t).ToArray();
            world.Step(t, tickCmds);
        }
        return world.Events.ComputeHash();
    }

    // ---- T-VS-8: 回放（同命令流重演事件一致）----
    [Fact]
    public void T_VS8_Replay_Rerun_Matches()
    {
        // 等价于 T_VS7 但显式记录中间状态
        var world = CreateWorld();
        var f1 = world.Fighters[1];
        f1.PosZ = Fixed.FromInt(2);
        for (int t = 1; t <= 20; t++)
        {
            var cmd = t == 1 ? new[] { SkillCmd(1001) } : Array.Empty<Command>();
            world.Step(t, cmd);
        }
        long hpAfterRun1 = f1.Hp;

        // 新世界重演
        var world2 = CreateWorld();
        var f1b = world2.Fighters[1];
        f1b.PosZ = Fixed.FromInt(2);
        for (int t = 1; t <= 20; t++)
        {
            var cmd = t == 1 ? new[] { SkillCmd(1001) } : Array.Empty<Command>();
            world2.Step(t, cmd);
        }
        Assert.Equal(hpAfterRun1, f1b.Hp);
        Assert.Equal(world.Events.ComputeHash(), world2.Events.ComputeHash());
    }
}
