using System;
using System.Collections.Generic;
using Arena.Core;
using Arena.Core.Sim;


// PRODUCTION - Arena.Infra
// 输入边沿语义（Batch 7 Part 3 裁定）: Press=按下沿/Held=持续/Release=抬起沿。
// 纯逻辑可测（IKeyPoller 抽象——Godot 适配在 Arena.Client）。
// Sim 侧合法性/CD/缓冲仍是最终裁决——本层只做「产生 Command 的节奏语义」。
namespace Arena.Infra.Input;

public interface IKeyPoller
{
    bool IsDown(int key);
}

/// 键位→Command 映射（Press/Held/Release 语义），状态自持、逐 tick 调用。
public sealed class InputMapper
{
    private readonly IKeyPoller _poller;
    private readonly Func<int, ushort> _skillIdOf;      // key → skillId（catalog 查询）
    private readonly int _fighterId;
    private readonly HashSet<int> _wasDown = new();

    public InputMapper(IKeyPoller poller, Func<int, ushort> skillIdOf, int fighterId)
    {
        _poller = poller;
        _skillIdOf = skillIdOf;
        _fighterId = fighterId;
    }

    /// keys: 移动(扫描 WASD 组合)/跳/翻滚/普攻/技能键/格挡键。dirOf: 8 向换算回调。
    public List<Command> Collect(IReadOnlyList<(int key, string role, ushort skillUid)> bindings)
    {
        var cmds = new List<Command>(4);
        var nowDown = new HashSet<int>();

        // 移动（Held——每 tick 扫描组合键）
        int dx = 0, dz = 0;
        foreach (var (key, ddx, ddz) in MoveKeys)
            if (_poller.IsDown(key)) { nowDown.Add(key); dx += ddx; dz += ddz; }
        if (dx != 0 || dz != 0)
            cmds.Add(new Command(_fighterId, CmdKind.Move, 0, 0, DirIndex(dx, dz), 0));

        foreach (var (key, role, skillUid) in bindings)
        {
            bool down = _poller.IsDown(key);
            bool was = _wasDown.Contains(key);
            if (down) nowDown.Add(key);

            if (down && !was)   // Press 沿
            {
                switch (role)
                {
                    case "jump":
                        cmds.Add(new Command(_fighterId, CmdKind.Jump, 0, 0, 0, 0));
                        break;
                    case "roll":
                        cmds.Add(new Command(_fighterId, CmdKind.Roll, 0, 0, 0, 0));
                        break;
                    case "basic":
                        cmds.Add(new Command(_fighterId, CmdKind.Basic, 0, 0, 0, 0));
                        break;
                    case "skill":
                        if (skillUid != 0) cmds.Add(new Command(_fighterId, CmdKind.Skill, skillUid, 0, 0, 0));
                        break;
                }
            }
            else if (!down && was)   // Release 沿
            {
                if (role == "guard")   // 格挡 Release → 普攻释放 hold（TryCastBasic 有 hold 释放分支）
                    cmds.Add(new Command(_fighterId, CmdKind.Basic, 0, 0, 0, 0));
            }
            // role Held 期间: move 已处理；guard hold 由 Sim 持续（无需重复指令）
        }

        _wasDown.Clear();
        foreach (var k in nowDown) _wasDown.Add(k);
        return cmds;
    }

    private static readonly (int key, int dx, int dz)[] MoveKeys =
    {
        ((int)KeyIds.W, 0, -1), ((int)KeyIds.S, 0, 1), ((int)KeyIds.A, -1, 0), ((int)KeyIds.D, 1, 0),
    };

    /// 键位常量（与 Godot Key 枚举数值一致——避免 Infra 依赖 Godot）
    public static class KeyIds
    {
        public const int W = 87, A = 65, S = 83, D = 68;
        public const int Space = 32, Shift = 4194325, J = 74, K = 75, L = 76, U = 85, I = 73, O = 79;
    }

    private static byte DirIndex(int dx, int dz) => (dx, dz) switch
    {
        (0, 1) => 0, (1, 1) => 1, (1, 0) => 2, (1, -1) => 3,
        (0, -1) => 4, (-1, -1) => 5, (-1, 0) => 6, (-1, 1) => 7,
        _ => 0,
    };
}
