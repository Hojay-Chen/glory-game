using System.Collections.Generic;
using Arena.Core;
using Arena.Core.Sim;
using Arena.Infra.Data;
using Godot;

// PRODUCTION - Arena.Client
// 键位 → Command（ADR-0010 输入映射 v1 键鼠）:
//   WASD=8 向移动 | Space=跳 | Shift=翻滚 | J=普攻链 | K=上挑 | L=三段斩
//   U=格挡(hold) | I=仙人指路 | O=拔刀斩
// aim: v1 移动方向即朝向（Sim HeadingQuantum=DirIndex×45°）；鼠标 aim 后续接入。
namespace Arena.Client;

public static class InputTranslator
{
    public static IEnumerable<Command> Collect(int fighterId, SimWorld world, RuntimeCatalog catalog)
    {
        var cmds = new List<Command>(4);
        var self = world.GetFighter(fighterId);
        if (self is null) return cmds;

        // 8 向移动（Sim DirIndex: 0=+Z 顺时针 45°步进 → WASD 组合映射）
        int dx = 0, dz = 0;
        if (Input.IsKeyPressed(Key.W)) dz += 1;
        if (Input.IsKeyPressed(Key.S)) dz -= 1;
        if (Input.IsKeyPressed(Key.A)) dx -= 1;
        if (Input.IsKeyPressed(Key.D)) dx += 1;
        if (dx != 0 || dz != 0)
        {
            byte dir = DirIndexFromInput(dx, dz);
            cmds.Add(new Command(fighterId, CmdKind.Move, 0, 0, dir, 0));
        }

        if (Input.IsKeyPressed(Key.Space))
            cmds.Add(new Command(fighterId, CmdKind.Jump, 0, 0, 0, 0));

        if (Input.IsKeyPressed(Key.Shift))
        {
            byte dir = dx != 0 || dz != 0 ? DirIndexFromInput(dx, dz) : (byte)0;
            cmds.Add(new Command(fighterId, CmdKind.Roll, 0, 0, dir, 0));
        }

        if (Input.IsKeyPressed(Key.J))
            cmds.Add(new Command(fighterId, CmdKind.Basic, 0, 0, 0, 0));

        // 技能键（repeat 触发——CD/合法性由 Sim 裁决，缓冲窗承接）
        TrySkill(cmds, fighterId, Key.K, "BLA_T1_001", catalog);   // 上挑
        TrySkill(cmds, fighterId, Key.L, "BLA_T2_001", catalog);   // 三段斩
        TrySkill(cmds, fighterId, Key.U, "BLA_T1_002", catalog);   // 格挡 hold
        TrySkill(cmds, fighterId, Key.I, "BLA_T2_006", catalog);   // 仙人指路
        TrySkill(cmds, fighterId, Key.O, "BLA_T2_003", catalog);   // 拔刀斩

        return cmds;
    }

    private static void TrySkill(List<Command> cmds, int fid, Key key, string skillId, RuntimeCatalog catalog)
    {
        if (!Input.IsKeyPressed(key)) return;
        if (catalog is not null && catalog.IdMap.TryGetValue(skillId, out var uid))
            cmds.Add(new Command(fid, CmdKind.Skill, uid, 0, 0, 0));
    }

    /// 输入 (dx,dz) → Sim DirIndex（0=+Z 顺时针 45°）。
    /// 相机沿 +Z 看（北=−Z 上方）：W=朝上=−Z? —— Sim DirIndex 4=−Z。
    /// VS 相机取南向俯视（+Z 朝玩家），W = 远离相机 = −Z？ — Sim +Z 为北。
    /// W(上屏)=−Z(南) 由相机朝向决定；映射: W→S键语义取 dx/dz 组合直接换算:
    /// 屏幕上方 = −Z、下方 = +Z、左 = −X、右 = +X → dx/dz 已是 Sim 域 ✓
    private static byte DirIndexFromInput(int dx, int dz) => (dx, dz) switch
    {
        (0, 1) => 0,      // +Z
        (1, 1) => 1,      // +X+Z
        (1, 0) => 2,      // +X
        (1, -1) => 3,     // +X−Z
        (0, -1) => 4,     // −Z
        (-1, -1) => 5,    // −X−Z
        (-1, 0) => 6,     // −X
        (-1, 1) => 7,     // −X+Z
        _ => 0,
    };
}
