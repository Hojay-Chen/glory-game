using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Ai;
using Arena.Infra.Match;
using Arena.Infra.Presentation;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase VS-7 Presentation Director 测试（纯 .NET——Director 零引擎依赖）。
/// 覆盖: 事件→线索映射 / Hit 伤害分流 / 只读 Sim（逐位不变）/ MatchEnd 闸断 /
///        Reset 清零 / 相机 trauma 衰减 / 消费游标不泄漏。
public class VS7PresentationDirectorTests
{
    private static string Root { get; } = FindRoot();
    private static string FindRoot()
    {
        var dir = Environment.CurrentDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/skill-spec/skills.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static SimEvent Ev(EventKind kind, int atk = 0, int vic = 1, ushort skill = 1,
        long dmg = 0, long hitX = 0, long hitY = 0, long hitZ = 0, long tick = 1) =>
        new(tick, 0, kind, atk, vic, skill, 0, dmg, 1, 0,
            hitX, hitY, hitZ, 0, 0, 0, 0, false, false, 0, 0, 0, 0);

    // ================= PD01: SimEventKind → PresentationCueKind 映射 =================

    [Theory]
    [InlineData(EventKind.SkillCast, PresentationCueKind.WeaponSwing)]
    [InlineData(EventKind.Hit, PresentationCueKind.HitFlash)]          // dmg>0 → 伤害命中
    [InlineData(EventKind.GuardHit, PresentationCueKind.GuardSpark)]
    [InlineData(EventKind.Parry, PresentationCueKind.ParrySpark)]
    [InlineData(EventKind.Interrupted, PresentationCueKind.Interrupt)]
    [InlineData(EventKind.Launched, PresentationCueKind.Launch)]
    [InlineData(EventKind.Knockback, PresentationCueKind.Knockback)]
    [InlineData(EventKind.Landed, PresentationCueKind.Down)]
    [InlineData(EventKind.ForcedDown, PresentationCueKind.Down)]
    [InlineData(EventKind.FallLanded, PresentationCueKind.Down)]
    [InlineData(EventKind.Died, PresentationCueKind.Death)]
    public void PD01_Cue_Mapping(EventKind sim, PresentationCueKind expected)
    {
        var director = new PresentationDirector();
        var cues = director.Advance(new[] { Ev(sim, dmg: sim == EventKind.Hit ? 500 : 0) }, 1 / 60.0);
        Assert.Contains(cues, c => c.Kind == expected);
    }

    // ================= PD02: Hit 伤害分流（DamageApplied vs AttackHit） =================

    [Fact]
    public void PD02_Hit_With_Damage_Is_DamageApplied_And_Arms_HitStop()
    {
        var director = new PresentationDirector();
        var cues = director.Advance(new[] { Ev(EventKind.Hit, dmg: 9000) }, 1 / 60.0);
        Assert.Single(cues);
        Assert.Equal(PresentationCueKind.HitFlash, cues[0].Kind);
        Assert.True(director.HitStop.ShouldFreezeVisuals, "有伤害命中 → Hit Stop 起效");
    }

    [Fact]
    public void PD02_Hit_Without_Damage_Is_AttackHit_And_Does_Not_Arm_HitStop()
    {
        var director = new PresentationDirector();
        var cues = director.Advance(new[] { Ev(EventKind.Hit, dmg: 0) }, 1 / 60.0);
        Assert.Single(cues);
        Assert.Equal(PresentationCueKind.HitSpark, cues[0].Kind);
        Assert.False(director.HitStop.ShouldFreezeVisuals, "无伤害接触 → 不触发 Hit Stop");
    }

    // ================= PD03: Director 纯表现——消费不改变 Sim（逐位一致） =================

    [Fact]
    public void PD03_Director_Is_Pure_Presentation_Sim_Bitwise_Invariant()
    {
        var plain = Play(0x5EED_0001L, 600, withDirector: false);
        var withDirector = Play(0x5EED_0001L, 600, withDirector: true);
        Assert.True(plain.CaptureSnapshot().BitwiseEquals(withDirector.CaptureSnapshot()),
            "经 Director 消费表现事件后 Sim 快照逐位一致——表现层只读");
    }

    private static SimWorld Play(long seed, int total, bool withDirector)
    {
        var setup = MatchAssembler.Assemble(seed, Root);
        var w = setup.World;
        var b0 = new AiBot(0, 1, seed: 0xA1, aggression: 0.8);
        var b1 = new AiBot(1, 0, seed: 0xA2, aggression: 0.45);
        var director = withDirector ? new PresentationDirector() : null;
        for (int t = 1; t <= total; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
            director?.Advance(w.Events.All, 1 / 60.0);
        }
        return w;
    }

    // ================= PD04: Died → Death + MatchEnd（仅一次）+ 之后闸断 =================

    [Fact]
    public void PD04_Died_Emits_MatchEnd_Once_Then_Gates_Combat_Cues()
    {
        var director = new PresentationDirector();
        var dying = director.Advance(new[] { Ev(EventKind.Died, tick: 10) }, 1 / 60.0);
        Assert.Contains(dying, c => c.Kind == PresentationCueKind.Death);
        Assert.Equal(1, dying.Count(c => c.Kind == PresentationCueKind.MatchEnd));
        Assert.True(director.MatchEnded);
        Assert.True(director.Camera.MatchEnd);

        // 终局后战斗事件被闸断——零线索
        var after = director.Advance(new[]
        {
            Ev(EventKind.Hit, dmg: 300, tick: 11),
            Ev(EventKind.SkillCast, tick: 11),
            Ev(EventKind.Died, vic: 0, tick: 12),
        }, 1 / 60.0);
        Assert.Empty(after);
    }

    // ================= PD05: Reset 清零（含消费游标） =================

    [Fact]
    public void PD05_Reset_Clears_HitStop_Camera_Cursor_And_MatchEnd()
    {
        var director = new PresentationDirector();
        director.Advance(new[] { Ev(EventKind.Hit, dmg: 9000), Ev(EventKind.Died) }, 1 / 60.0);
        Assert.True(director.HitStop.ShouldFreezeVisuals);
        Assert.True(director.MatchEnded);

        director.Reset();
        Assert.False(director.HitStop.ShouldFreezeVisuals);
        Assert.Equal(0.0, director.Camera.ShakeX);
        Assert.Equal(0.0, director.Camera.ShakeY);
        Assert.False(director.Camera.MatchEnd);
        Assert.False(director.MatchEnded);

        // 游标归零 → 同一事件流重新消费可再次产出线索
        var again = director.Advance(new[] { Ev(EventKind.SkillCast) }, 1 / 60.0);
        Assert.Contains(again, c => c.Kind == PresentationCueKind.WeaponSwing);
    }

    // ================= PD06: 相机 trauma 累积 → 衰减归零 =================

    [Fact]
    public void PD06_Camera_Trauma_Decays_To_Zero()
    {
        var director = new PresentationDirector();
        director.Advance(new[] { Ev(EventKind.Launched) }, 1 / 60.0);
        Assert.True(director.Camera.ShakeX != 0 || director.Camera.ShakeY != 0, "浮空 → 相机震动可见");

        for (int i = 0; i < 90; i++)   // 1.5s > trauma 0.30 / 1.8
            director.Advance(Array.Empty<SimEvent>(), 1 / 60.0);
        Assert.Equal(0.0, director.Camera.ShakeX);
        Assert.Equal(0.0, director.Camera.ShakeY);
    }

    // ================= PD07: 消费游标不泄漏（同事件流二次消费 = 空） =================

    [Fact]
    public void PD07_Advance_Does_Not_Leak_Previous_Cues()
    {
        var director = new PresentationDirector();
        var events = new[] { Ev(EventKind.SkillCast) };
        Assert.Single(director.Advance(events, 1 / 60.0));
        Assert.Empty(director.Advance(events, 1 / 60.0));
    }
}
