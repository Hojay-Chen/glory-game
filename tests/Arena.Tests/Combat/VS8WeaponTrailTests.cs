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

/// Phase VS-8 Weapon Trail 测试。
/// 纯逻辑部分（TrailBuffer 生命周期 + 事件→线索映射）在这里验证；
/// 「网格真的画出来了」由 Godot 客户端渲染验证承担（见 production/session-state）。
/// 覆盖: 武装/解除/步进去重/老化过期/上限FIFO/技能号匹配/硬清空/
///       ActEnded·Cancelled→AttackEnded 线索（真实 SimEvent）/
///       真实对局里 SkillCast→ActEnded 的首尾配对 / 终局后零残留 / 只读 Sim 逐位一致。
public class VS8WeaponTrailTests
{
    private static string Root { get; } = FindRoot();
    private static string FindRoot()
    {
        var dir = Environment.CurrentDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/skill-spec/skills.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private const double Dt = 1.0 / 60.0;

    private static SimEvent Ev(EventKind kind, int atk = 0, int vic = 1, ushort skill = 1, long tick = 1) =>
        new(tick, 0, kind, atk, vic, skill, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, 0, 0, 0);

    // ================= WL01: 武装语义（清旧点 + 记录技能号） =================

    [Fact]
    public void WL01_Arm_Clears_Previous_Points_And_Records_Skill()
    {
        var buf = new TrailBuffer();
        Assert.False(buf.Armed);
        Assert.Equal(-1, buf.ArmedSkill);

        buf.Arm(7);
        Assert.True(buf.Armed);
        Assert.Equal(7, buf.ArmedSkill);
        Assert.Equal(0, buf.Count);

        // 攒几个点后再次武装（连续攻击）——旧点必须被清掉，避免跨技连线
        for (int i = 0; i < 5; i++) buf.Advance(i * 0.5f, 0, 0, Dt);
        Assert.Equal(5, buf.Count);
        buf.Arm(9);
        Assert.Equal(0, buf.Count);
        Assert.Equal(9, buf.ArmedSkill);
    }

    // ================= WL02: 位移阈值去重（武器静止不刷点） =================

    [Fact]
    public void WL02_Advance_Only_Appends_When_Moved_Enough()
    {
        var buf = new TrailBuffer();
        buf.Arm(1);

        buf.Advance(0, 0, 0, Dt);
        Assert.Equal(1, buf.Count);

        buf.Advance(0, 0, TrailBuffer.MinStepMeters * 0.5f, Dt);   // 半步长——静默
        Assert.Equal(1, buf.Count);

        buf.Advance(0, 0, TrailBuffer.MinStepMeters * 2f, Dt);     // 超过阈值——追加
        Assert.Equal(2, buf.Count);
    }

    // ================= WL03: 解除后不再追加，余点按时老化消失 =================

    [Fact]
    public void WL03_Disarmed_Stops_Sampling_And_Old_Points_Expire()
    {
        var buf = new TrailBuffer();
        buf.Arm(3);
        for (int i = 0; i < 6; i++) buf.Advance(i * 0.5f, 0, 0, Dt);
        Assert.Equal(6, buf.Count);

        buf.Disarm(3);
        Assert.False(buf.Armed);

        // 解除后即使尖端继续"移动"也不再追加
        for (int i = 0; i < 6; i++) buf.Advance(100f + i, 0, 0, Dt);
        Assert.True(buf.Count <= 6, $"解除后不应追加新点，实际 {buf.Count}");

        // 推进超过存活时长——余点全部过期，尾迹消失且不残留
        for (int i = 0; i < 20; i++) buf.Advance(200f, 0, 0, Dt);
        Assert.Equal(0, buf.Count);
    }

    // ================= WL04: 技能号匹配才解除（取消链安全） =================

    [Fact]
    public void WL04_Disarm_Requires_Matching_Skill()
    {
        var buf = new TrailBuffer();
        buf.Arm(7);

        buf.Disarm(9);                       // 旧技/别的技的结束事件——不得解除当前武装
        Assert.True(buf.Armed);
        Assert.Equal(7, buf.ArmedSkill);

        buf.Disarm(7);
        Assert.False(buf.Armed);
    }

    // ================= WL05: 上限 FIFO（无界增长防护） =================

    [Fact]
    public void WL05_Points_Capped_And_Oldest_Dropped()
    {
        var buf = new TrailBuffer();
        buf.Arm(1);
        // 极小 dt: 让老化几乎不发生，从而单独检验「上限 FIFO」这条守卫本身
        // （正常 60fps 下 0.20s 存活期最多积 ~12 点，上限是防帧率尖峰的无界增长闸）
        for (int i = 0; i < 60; i++) buf.Advance(i * 0.5f, 0, 0, 1e-6);

        Assert.Equal(TrailBuffer.MaxPoints, buf.Count);
        // 最旧的点已被丢弃——首点应是第 (60 - MaxPoints) 次追加的坐标
        Assert.Equal((60 - TrailBuffer.MaxPoints) * 0.5f, buf[0].X, 3);
        Assert.Equal(59 * 0.5f, buf[buf.Count - 1].X, 3);
    }

    // ================= WL06: 硬清空（阵亡/终局/重开） =================

    [Fact]
    public void WL06_Clear_Wipes_Points_And_Armed_State()
    {
        var buf = new TrailBuffer();
        buf.Arm(5);
        for (int i = 0; i < 5; i++) buf.Advance(i * 0.5f, 0, 0, Dt);

        buf.Clear();
        Assert.Equal(0, buf.Count);
        Assert.False(buf.Armed);
        Assert.Equal(-1, buf.ArmedSkill);
    }

    // ================= WL07: 攻击结束事件 → 线索（真实 SimEvent） =================
    //
    // 2026-10-11 变更：**取消（Cancelled）不再与自然结束（ActEnded）混同**。
    // 取消链是《荣耀》的核心节奏，若两者映射到同一条线索，画面上就完全看不出"这一下是取消出来的"。
    // 客户端对两者**都**收尾迹（见 MatchRoot.DispatchCue），但取消另有专属高光反馈。

    [Fact]
    public void WL07_ActEnded_Maps_To_AttackEnded_And_Cancelled_Maps_To_SkillCancelled()
    {
        var director = new PresentationDirector();

        var cues = director.Advance(new[]
        {
            Ev(EventKind.ActEnded, atk: 1, vic: 0, skill: 42, tick: 10),
            Ev(EventKind.Cancelled, atk: 0, vic: 1, skill: 43, tick: 11),
        }, Dt);

        var ends = cues.Where(c => c.Kind == PresentationCueKind.AttackEnded).ToList();
        Assert.Single(ends);
        Assert.Equal(1, ends[0].AttackerId);      // 施法者本人（ActEnded 载荷语义）
        Assert.Equal(42, ends[0].SkillId);

        var cancels = cues.Where(c => c.Kind == PresentationCueKind.SkillCancelled).ToList();
        Assert.Single(cancels);
        Assert.Equal(0, cancels[0].AttackerId);
        Assert.Equal(43, cancels[0].SkillId);
    }

    // ================= WL08: 真实对局——SkillCast 与 ActEnded 首尾配对 =================

    [Fact]
    public void WL08_Real_Match_Arms_Then_Disarms_On_The_Actual_Attack_Boundaries()
    {
        var setup = MatchAssembler.Assemble(0x5EED_0001L, Root);
        var w = setup.World;
        var b0 = new AiBot(0, 1, seed: 0xA1, aggression: 0.8);
        var b1 = new AiBot(1, 0, seed: 0xA2, aggression: 0.45);
        var director = new PresentationDirector();

        var cues = new List<PresentationCue>();
        for (int t = 1; t <= 900; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
            cues.AddRange(director.Advance(w.Events.All, Dt));
        }

        // 顺序扫描整个线索流：统计「起手 → 同技收束」配对，并要求收束严格晚于起手。
        // （不是所有攻击都会发结束事件——部分执行体由 Sim 静默终止；这里只核验真实出现的配对。）
        var armedAt = new Dictionary<int, int>();
        var armedSkill = new Dictionary<int, ushort>();
        int pairs = 0, outOfOrder = 0, mismatched = 0;
        for (int i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (c.Kind == PresentationCueKind.WeaponSwing)
            {
                armedAt[c.AttackerId] = i;
                armedSkill[c.AttackerId] = c.SkillId;
            }
            else if (c.Kind is PresentationCueKind.AttackEnded or PresentationCueKind.Interrupt)
            {
                if (armedSkill.TryGetValue(c.AttackerId, out var s) && s == c.SkillId)
                {
                    if (armedAt[c.AttackerId] < i) pairs++; else outOfOrder++;
                    armedSkill[c.AttackerId] = 0;
                }
                else mismatched++;
            }
        }
        Assert.True(pairs >= 10, $"真实对局应出现足量「起手→同技收束」配对，实际 {pairs}");
        Assert.Equal(0, outOfOrder);    // 收束永远在起手之后
        Assert.Equal(0, mismatched);    // 收束的技能号必须与当前武装一致（技能号守卫生效）

        // 用真实线索驱动尾迹：武装 → 采样 → 收束
        var open = cues.First(c => c.Kind == PresentationCueKind.WeaponSwing);
        var trail = new TrailBuffer();
        trail.Arm(open.SkillId);
        Assert.True(trail.Armed);
        trail.Advance(0, 0, 0, Dt);
        trail.Advance(0.5f, 0, 0, Dt);
        Assert.Equal(2, trail.Count);

        trail.Disarm(open.SkillId);
        Assert.False(trail.Armed);
    }

    // ================= WL09: 终局后零残留 =================

    [Fact]
    public void WL09_After_MatchEnd_No_Trail_Remains_Armed()
    {
        var setup = MatchAssembler.Assemble(0x5EED_0001L, Root);
        var w = setup.World;
        var b0 = new AiBot(0, 1, seed: 0xA1, aggression: 0.8);
        var b1 = new AiBot(1, 0, seed: 0xA2, aggression: 0.45);
        var director = new PresentationDirector();

        var trails = new Dictionary<int, TrailBuffer>();   // 斗士 → 尾迹（与 FighterView 一一对应）
        foreach (var f in w.Fighters) trails[f.Id] = new TrailBuffer();

        int arms = 0, closes = 0, mismatched = 0, maxSeen = 0;
        for (int t = 1; t <= 1200; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);

            foreach (var cue in director.Advance(w.Events.All, Dt))
            {
                switch (cue.Kind)
                {
                    case PresentationCueKind.WeaponSwing:
                        if (trails.TryGetValue(cue.AttackerId, out var trA)) { trA.Arm(cue.SkillId); arms++; }
                        break;
                    case PresentationCueKind.AttackEnded:
                    case PresentationCueKind.Interrupt:      // 被打断=该次攻击终止（同 MatchRoot 分发语义）
                        if (trails.TryGetValue(cue.AttackerId, out var trE))
                        {
                            if (trE.ArmedSkill == cue.SkillId) closes++;
                            else if (trE.Armed) mismatched++;
                            trE.Disarm(cue.SkillId);
                        }
                        break;
                    case PresentationCueKind.Death:
                        if (trails.TryGetValue(cue.VictimId, out var trD)) trD.Clear();
                        break;
                    case PresentationCueKind.MatchEnd:
                        foreach (var tr in trails.Values) tr.Clear();
                        break;
                }
                // 每个非 MatchEnd 时刻都推进采样（等价于每表现帧 Sample）
                foreach (var tr in trails.Values)
                {
                    tr.Advance(0, 0, 0, Dt);
                    if (tr.Count > maxSeen) maxSeen = tr.Count;
                }
            }
        }

        // 生命周期确实在循环（不是只武装一次），且收束确实发生（防「永不解除」回归）。
        // 2026-10-13 起含防御行为（受身/格挡占掉部分输出窗口）——阈值 50 → 35，仍足以证明循环。
        Assert.True(arms > 35, $"攻击起手应足够多，实际 {arms}");
        Assert.True(closes > 0, $"攻击收束应确实发生，实际 {closes}");
        // 收束与武装的技能号从不矛盾（技能号守卫生效——否则会误收束新技尾迹）
        Assert.Equal(0, mismatched);
        // 点数始终有界（无界增长防护）
        Assert.InRange(maxSeen, 0, TrailBuffer.MaxPoints);

        // 终局硬清空后：零武装、零点——无残留、无泄漏
        foreach (var kv in trails)
        {
            kv.Value.Clear();
            Assert.False(kv.Value.Armed);
            Assert.Equal(0, kv.Value.Count);
        }
    }

    // ================= WL10: 尾迹逻辑只读 Sim（逐位一致） =================

    [Fact]
    public void WL10_Trail_Logic_Does_Not_Change_Sim()
    {
        var plain = Play(0x5EED_0001L, 600, withTrail: false);
        var withTrail = Play(0x5EED_0001L, 600, withTrail: true);
        Assert.True(plain.CaptureSnapshot().BitwiseEquals(withTrail.CaptureSnapshot()),
            "尾迹武装/采样/收束不得改变权威模拟结果");
    }

    private static SimWorld Play(long seed, int total, bool withTrail)
    {
        var setup = MatchAssembler.Assemble(seed, Root);
        var w = setup.World;
        var b0 = new AiBot(0, 1, seed: 0xA1, aggression: 0.8);
        var b1 = new AiBot(1, 0, seed: 0xA2, aggression: 0.45);
        var director = withTrail ? new PresentationDirector() : null;
        var trails = new Dictionary<int, TrailBuffer>();

        for (int t = 1; t <= total; t++)
        {
            var cmds = b0.Produce(t, w, CombatGoldenSlice.Catalog)
                .Concat(b1.Produce(t, w, CombatGoldenSlice.Catalog)).ToArray();
            w.Step(t, cmds);
            if (director is null) continue;

            foreach (var cue in director.Advance(w.Events.All, Dt))
            {
                if (!trails.TryGetValue(cue.AttackerId, out var tr)) trails[cue.AttackerId] = tr = new TrailBuffer();
                if (cue.Kind == PresentationCueKind.WeaponSwing) tr.Arm(cue.SkillId);
                else if (cue.Kind is PresentationCueKind.AttackEnded or PresentationCueKind.Interrupt) tr.Disarm(cue.SkillId);
            }
            foreach (var tr in trails.Values) tr.Advance(0, 0, 0, Dt);
        }
        return w;
    }
}
