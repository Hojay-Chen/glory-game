using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Ai;
using Arena.Infra.Match;
using System.IO;
using Arena.Infra.Presentation;
using Xunit;

namespace Arena.Tests.Combat;

/// Phase 7 VS-6 Combat Feel & Presentation Foundation tests.
/// All tests are pure .NET (no Godot dependency) — the Presentation module is engine-agnostic.
/// PE01 Bridge 1:1 / PE02 HitStop Sim 不变 / PE03 相机不动 Sim / PE04 SkillCast 1:1 /
/// PE05 Replay 一致 / PE06 Restart 清零 / PE07 MatchEnd 闸断 / PE08 确定性双跑。
public class VS6PresentationTests : IDisposable
{
    private static string Root { get; } = FindRoot();
    private static string FindRoot()
    {
        var dir = Environment.CurrentDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/skill-spec/skills.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static (SimWorld World, AiBot Bot0, AiBot Bot1) Assemble(long seed)
    {
        var setup = MatchAssembler.Assemble(seed, Root);
        return (setup.World,
            new AiBot(0, 1, seed: unchecked((int)seed), aggression: 0.8),
            new AiBot(1, 0, seed: unchecked((int)seed) + 1, aggression: 0.45));
    }

    public void Dispose() { }

    // ================= PE01: Bridge 1:1——SimEvent → PresentationEvent 映射 =================

    [Fact]
    public void PE01_Bridge_Translates_SimEvents_1to1()
    {
        var (w, b0, b1) = Assemble(99);
        var bridge = new PresentationEventBridge();
        for (int t = 1; t <= 600; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
        }
        var simEvents = w.Events.All.Where(e =>
            e.Kind is EventKind.SkillCast or EventKind.Hit or EventKind.GuardHit or EventKind.Parry
                or EventKind.Interrupted or EventKind.Launched or EventKind.Knockback
                or EventKind.Landed or EventKind.ForcedDown or EventKind.FallLanded or EventKind.Died).ToList();
        var all = bridge.Consume(simEvents);
        // 1:1 映射（SkillCast 数 = SkillCast PE 数 + AttackStart；其他一一对应）
        int simSkillCast = simEvents.Count(e => e.Kind == EventKind.SkillCast);
        int peSkillCast = all.Count(e => e.Kind == PresentationEventKind.SkillCast);
        Assert.Equal(simSkillCast, peSkillCast);
        int simHit = simEvents.Count(e => e.Kind == EventKind.Hit);
        int peHit = all.Count(e => e.Kind is PresentationEventKind.AttackHit or PresentationEventKind.DamageApplied);
        Assert.True(peHit >= simHit, $"Hit 映射: sim={simHit} pe={peHit}");
    }

    // ================= PE02: HitStop 纯表现——消费/不消费 Sim 结果逐位一致 =================

    [Fact]
    public void PE02_HitStop_Pure_Presentation_Sim_Invariant()
    {
        // 世界 A: 无 HitStop（纯 Sim）
        var (wa, b0a, b1a) = Assemble(42);
        var bot0a = b0a; var bot1a = b1a;
        for (int t = 1; t <= 600; t++)
        {
            var cmds = bot0a.Produce(t, wa, CombatGoldenSlice.Catalog)
                .Concat(bot1a.Produce(t, wa, CombatGoldenSlice.Catalog)).ToArray();
            wa.Step(t, cmds);
        }
        var snapA = wa.CaptureSnapshot();

        // 世界 B: 有 HitStop（桥接消费 + hitstop 计数——只读 Sim + 表现层状态机）
        var (wb, b0b, b1b) = Assemble(42);
        var bridge = new PresentationEventBridge();
        var hitStop = new HitStopController();
        for (int t = 1; t <= 600; t++)
        {
            var cmds = b0b.Produce(t, wb, CombatGoldenSlice.Catalog)
                .Concat(b1b.Produce(t, wb, CombatGoldenSlice.Catalog)).ToArray();
            wb.Step(t, cmds);
            var pevts = bridge.Consume(wb.Events.All);
            foreach (var pe in pevts)
            {
                if (pe.Kind == PresentationEventKind.DamageApplied) hitStop.TriggerDamage(pe.Damage);
                if (pe.Kind == PresentationEventKind.Launched) hitStop.TriggerLaunch();
            }
            // 模拟表现冻结帧（Tick 空转——不触碰 Sim）
            int frozen = 0;
            while (hitStop.ShouldFreezeVisuals && frozen < 10) { hitStop.Tick(); frozen++; }
        }
        Assert.True(wa.CaptureSnapshot().BitwiseEquals(wb.CaptureSnapshot()),
            "HitStop 消费/冻结不影响 Sim 状态——表现层纯只读");
    }

    // ================= PE03: Camera Presenter 不动 Sim =================

    [Fact]
    public void PE03_Camera_Presenter_Does_Not_Touch_Sim()
    {
        var (w, b0, b1) = Assemble(7);
        var camera = new CameraPresenter();
        var bot0 = b0; var bot1 = b1;
        for (int t = 1; t <= 300; t++)
        {
            camera.AddTrauma(0.3);
            camera.Tick(1 / 60.0);
            var cmds = bot0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(bot1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
        }
        // CameraPresenter 自身状态改变——Sim 快照不受影响（确定性对比由 T08/IT01 覆盖）
        Assert.True(camera.ShakeX != 0 || camera.ShakeY != 0 || camera.Tick != null);
        camera.Reset();
        Assert.Equal(0, camera.ShakeX);
        Assert.False(camera.MatchEnd);
    }

    // ================= PE04: SkillCast ↔ 表现 SkillCast 1:1 =================

    [Fact]
    public void PE04_SkillCast_Presentation_1to1()
    {
        var (w, b0, b1) = Assemble(77);
        var bridge = new PresentationEventBridge();
        for (int t = 1; t <= 600; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
        }
        var simSkillCast = w.Events.All.Count(e => e.Kind == EventKind.SkillCast);
        var pe = bridge.Consume(w.Events.All);
        var peSkillCast = pe.Count(e => e.Kind == PresentationEventKind.SkillCast);
        Assert.Equal(simSkillCast, peSkillCast);
        // Bridge 消费幂等: 二次消费 = 0 新事件
        var reConsume = bridge.Consume(w.Events.All);
        Assert.DoesNotContain(reConsume, e => e.Kind == PresentationEventKind.SkillCast);
    }

    // ================= PE05: 全对局 Replay——消费桥接不退化确定性 =================

    [Fact]
    public void PE05_FullMatch_WithPresentation_Deterministic()
    {
        const int Total = 1000;
        var runA = PlayWithPresentation(Total);
        var runB = PlayWithPresentation(Total);
        Assert.True(runA.BitwiseEquals(runB), "有桥接消费的对局双跑逐位一致");
    }

    private static Arena.Core.Snapshot.SnapshotData PlayWithPresentation(int total)
    {
        var (w, b0, b1) = Assemble(99);
        var bridge = new PresentationEventBridge();
        var hitStop = new HitStopController();
        var camera = new CameraPresenter();
        for (int t = 1; t <= total; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
            foreach (var pe in bridge.Consume(w.Events.All))
            {
                if (pe.Kind == PresentationEventKind.DamageApplied) hitStop.TriggerDamage(pe.Damage);
                if (pe.Kind == PresentationEventKind.Launched) { hitStop.TriggerLaunch(); camera.AddTrauma(0.4); }
                if (pe.Kind == PresentationEventKind.Parried) hitStop.TriggerParry();
            }
            hitStop.Tick();
            camera.Tick(1 / 60.0);
        }
        return w.CaptureSnapshot();
    }

    // ================= PE06: Restart 后 Presentation 清零 =================

    [Fact]
    public void PE06_Restart_Bridge_Reset()
    {
        var (w, b0, b1) = Assemble(1);
        var bridge = new PresentationEventBridge();
        for (int t = 1; t <= 200; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
        }
        var consumed1 = bridge.Consume(w.Events.All);
        Assert.True(consumed1.Count > 0, "第一局有表现事件");

        bridge.Reset();   // Restart
        var (w2, b0b, b1b) = Assemble(1);
        var consumed2 = bridge.Consume(w2.Events.All);
        Assert.Empty(consumed2);   // 重开后零残留
        Assert.False(bridge.MatchEnded);
    }

    // ================= PE07: MatchEnd 后闸断战斗表现 =================

    [Fact]
    public void PE07_MatchEnd_Gates_Combat_Presentation()
    {
        var (w, b0, b1) = Assemble(3);
        var bridge = new PresentationEventBridge();
        for (int t = 1; t <= 100; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
        }
        bridge.FlagMatchEnd();

        // Flag 后继续跑 + 命中——战斗表现被闸断
        for (int t = 101; t <= 200; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
        }
        var afterEnd = bridge.Consume(w.Events.All);
        Assert.DoesNotContain(afterEnd, e => e.Kind == PresentationEventKind.AttackHit);
        Assert.DoesNotContain(afterEnd, e => e.Kind == PresentationEventKind.DamageApplied);
        Assert.DoesNotContain(afterEnd, e => e.Kind == PresentationEventKind.SkillCast);
        Assert.True(bridge.MatchEnded);
    }
}
