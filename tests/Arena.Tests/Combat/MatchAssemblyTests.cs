using System;
using System.IO;
using System.Linq;
using Arena.Core;
using Arena.Infra.Match;
using Xunit;

namespace Arena.Tests.Combat;

/// 装配层接线测试。
/// 背景: `class-base.csv` 的职业面板（atk/def）与 `weapons.csv` 的 atk_mod **长期未被应用到角色**
/// —— 25 个职业在实战里共用 1100/800 默认值、武器选了也等于没选。这里把接线锁死，防静默回退。
public class MatchAssemblyTests
{
    private static string Root { get; } = FindRoot();
    private static string FindRoot()
    {
        var dir = Environment.CurrentDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/skill-spec/skills.csv")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void MA01_Class_Panel_Applied_To_Fighters()
    {
        var setup = MatchAssembler.Assemble(0x5EED_0001L, Root, playerClass: "BMG", enemyClass: "ELE");
        var p0 = setup.World.Fighters.First(f => f.Id == 0);
        var p1 = setup.World.Fighters.First(f => f.Id == 1);

        // class-base.csv: BMG atk=1120 def=720 / ELE atk=1180 def=620（不是默认 1100/800）
        Assert.Equal(1120, p0.Atk); Assert.Equal(720, p0.Def);
        Assert.Equal(1180, p1.Atk); Assert.Equal(620, p1.Def);
        Assert.NotEqual(p0.Atk, p1.Atk);   // 两个职业的基准必须真的不同
    }

    [Fact]
    public void MA02_Equipped_Weapon_Changes_Attack()
    {
        var bare = MatchAssembler.Assemble(0x5EED_0001L, Root, "BMG", "ELE");
        var armed = MatchAssembler.Assemble(0x5EED_0001L, Root, "BMG", "ELE", playerWeapon: "W_BMG_003");

        long bareAtk = bare.World.Fighters.First(f => f.Id == 0).Atk;
        long armedAtk = armed.World.Fighters.First(f => f.Id == 0).Atk;

        // W_BMG_003 破魔重枪 atk_mod=0.03 → +3% 面板
        Assert.Equal(1120, bareAtk);
        Assert.True(armedAtk > bareAtk, $"装武器应提高攻击: {bareAtk} → {armedAtk}");
        Assert.InRange(armedAtk - bareAtk, 1, 60);
        // 且 WeaponId 真的落到 Fighter 域（规则级 trait 的消费前提）
        Assert.NotEqual((ushort)0, armed.World.Fighters.First(f => f.Id == 0).WeaponId);
    }

    [Fact]
    public void MA03_Unknown_Weapon_Id_Does_Not_Break_Assembly()
    {
        var setup = MatchAssembler.Assemble(0x5EED_0001L, Root, "BLA", "BLA", playerWeapon: "W_NOPE_999");
        Assert.Equal(1100, setup.World.Fighters.First(f => f.Id == 0).Atk);   // 回落职业面板值
    }
}
