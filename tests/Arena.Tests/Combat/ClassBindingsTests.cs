using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Arena.Infra.Input;
using Xunit;

namespace Arena.Tests.Combat;

/// VS-9 职业通用键位（`ClassBindings`）。
/// 此前键位把剑客的 4 个 skill_id 硬编码在 Godot 客户端里——换职业后按键全失效、HUD 提示也对不上。
/// 这里断言的是**逐职业的真实映射关系**与跨职业不变式，不是「方法存在」。
public class ClassBindingsTests
{
    private static RuntimeCatalog Catalog => CombatGoldenSlice.Catalog;
    private static SkillRuntimeData Def(ushort uid) => Catalog.Skills.First(s => s.RuntimeId == uid);

    private static IEnumerable<string> AllClasses =>
        Catalog.Skills.Select(s => s.ClassId).Distinct().OrderBy(x => x, StringComparer.Ordinal);

    // ================= CB01: 剑客具体映射（回归锚点） =================

    [Fact]
    public void CB01_Blade_Maps_Each_Tier_And_Guard()
    {
        var b = ClassBindings.For(Catalog, "BLA");
        ushort Skill(string role) => b.First(x => x.role == role).skillUid;

        Assert.Equal((int)InputMapper.KeyIds.K, b.First(x => x.role == "skill").key);   // 首档技绑 K
        Assert.Equal(1, Def(Skill("skill")).Tier);                                      // K = T1

        // U = 格挡，且必须是该职业真正的 hold 姿态技（BLA_T1_002）
        var guard = b.First(x => x.role == "guard");
        Assert.Equal((int)InputMapper.KeyIds.U, guard.key);
        Assert.True(Def(guard.skillUid).IsHold);
        Assert.Equal("BLA_T1_002", Def(guard.skillUid).SkillId);
    }

    // ================= CB02: 跨职业不变式（真正防回归的一条） =================

    [Fact]
    public void CB02_Every_Class_Binds_Only_Its_Own_Skills_Without_Key_Collisions()
    {
        foreach (var classId in AllClasses)
        {
            var b = ClassBindings.For(Catalog, classId);

            // 每个绑定的技能都必须属于该职业——防止串到别的职业去
            foreach (var (_, role, uid) in b)
                if (uid != 0)
                    Assert.True(Def(uid).ClassId == classId,
                        $"{classId} 绑到了 {Def(uid).SkillId}（属 {Def(uid).ClassId}）");

            // 键位不得重复
            var keys = b.Select(x => x.key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());

            // 基础三键恒在
            Assert.Contains(b, x => x.role == "jump");
            Assert.Contains(b, x => x.role == "roll");
            Assert.Contains(b, x => x.role == "basic");
        }
    }

    // ================= CB03: 每档至多一键，且档位正确 =================

    [Fact]
    public void CB03_Tier_Slots_Map_To_Actual_Tiers()
    {
        var slotTier = new Dictionary<int, byte>
        {
            [InputMapper.KeyIds.K] = 1, [InputMapper.KeyIds.L] = 2, [InputMapper.KeyIds.I] = 3,
            [InputMapper.KeyIds.O] = 4, [InputMapper.KeyIds.P] = 5,
        };

        foreach (var classId in AllClasses)
        {
            foreach (var (key, role, uid) in ClassBindings.For(Catalog, classId))
            {
                if (role != "skill") continue;
                Assert.True(slotTier.ContainsKey(key), $"{classId} 在非档位键 {key} 上绑了技能");
                Assert.Equal(slotTier[key], Def(uid).Tier);
                Assert.False(Def(uid).IsHold);            // hold 只走格挡键
                Assert.NotEqual("passive", Def(uid).Type);
            }
        }
    }

    // ================= CB04: 格挡键与 hold 技一一对应 =================

    [Fact]
    public void CB04_Guard_Key_Present_Exactly_When_Class_Has_Guard_Or_Hold_Skill()
    {
        foreach (var classId in AllClasses)
        {
            bool hasGuard = Catalog.Skills.Any(s => s.ClassId == classId && (s.Guard is not null || s.IsHold));
            var guard = ClassBindings.For(Catalog, classId).Where(x => x.role == "guard").ToList();

            Assert.Equal(hasGuard ? 1 : 0, guard.Count);
            if (hasGuard)
            {
                var def = Def(guard[0].skillUid);
                Assert.True(def.Guard is not null || def.IsHold, $"{classId} 的格挡键绑到了非格挡技");
                Assert.Equal(InputMapper.KeyIds.U, guard[0].key);
            }
        }

        // 内容缺口（登记，不代填）: 格挡是 GDD §6.2 的核心通用机制，但全表只有 BLA 有盾值技。
        // 这条断言把现状固化下来——哪天数据补齐，它会失败并提醒更新。
        int classesWithGuard = AllClasses.Count(c =>
            Catalog.Skills.Any(s => s.ClassId == c && s.Guard is not null));
        Assert.Equal(1, classesWithGuard);
    }

    // ================= CB05: HUD 提示用的是真实技能名 =================

    [Fact]
    public void CB05_Hints_Carry_Real_Skill_Names()
    {
        var hints = ClassBindings.Hints(Catalog, "BLA");
        Assert.Contains(hints, h => h.Key == "K" && h.Label == "上挑");
        Assert.Contains(hints, h => h.Key == "U" && h.Label == "格挡");
        Assert.All(hints, h => Assert.False(string.IsNullOrWhiteSpace(h.Label)));

        // 换一个职业，提示必须跟着变（而不是仍显示剑客那一套）
        var bmg = ClassBindings.Hints(Catalog, "BMG");
        Assert.All(bmg.Where(h => h.Key is "K" or "L" or "I" or "O" or "P"),
                   h => Assert.False(string.IsNullOrWhiteSpace(h.Label)));
        var bmgK = bmg.FirstOrDefault(h => h.Key == "K");
        Assert.True(bmgK != default, "战斗法师应能绑到 K");
        Assert.NotEqual(hints.First(h => h.Key == "K").Label, bmgK.Label);   // 两个职业的 K 不是同一招
    }

    // ================= CB06: 确定性 + 可用性下限 =================

    [Fact]
    public void CB06_Deterministic_And_Every_Class_Playable()
    {
        foreach (var classId in AllClasses)
        {
            var a = ClassBindings.For(Catalog, classId);
            var b = ClassBindings.For(Catalog, classId);
            Assert.Equal(a, b);                        // 两次调用完全一致（确定性）

            // 可用性下限：至少 2 个档位键有技可放，否则该职业"没得玩"
            int skillKeys = a.Count(x => x.role == "skill");
            Assert.True(skillKeys >= 2, $"{classId} 只绑到 {skillKeys} 个技能键");
        }
    }
}
