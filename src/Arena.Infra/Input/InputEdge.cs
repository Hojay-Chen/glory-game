using System;
using System.Collections.Generic;
using Arena.Core;
using Arena.Core.Sim;


// PRODUCTION - Arena.Infra
// 输入边沿语义（Batch 7 Part 3 裁定）: Press=按下沿/Held=持续/Release=抬起沿。
// 纯逻辑可测（IKeyPoller 抽象——Godot 适配在 Arena.Client）。
// Sim 侧合法性/CD/缓冲仍是最终裁决——本层只做「产生 Command 的节奏语义」。
//
// 2026-10-13 鼠标视角改造:
//   移动/翻滚方向由 **相机相对** 回调换算（MoveCtx.ToWorld）——W 永远 = 视角前方；
//   模拟量朝向经 AimQuantum 直达 Sim（DirIndex=255 标记），不再吸附 45° 扇区；
//   普攻/技能带施放朝向（软锁定敌人或相机前方）；
//   鼠标键用 1000+ 哨兵 id（Godot 适配层翻译成 IsMouseButtonPressed）。
namespace Arena.Infra.Input;

public interface IKeyPoller
{
    bool IsDown(int key);
}

/// 相机相对换算上下文（MatchRoot 每局构造；测试传 null 走传统 8 向）
public sealed class MoveCtx
{
    /// (screenDx, screenDz) → (DirIndex, AimQuantum)。
    /// AimQuantum != 0 时 Sim 走模拟量路径（Command 须带 DirIndex=255）；
    /// 返回 dir 供翻滚等 8 向指令使用（屏幕方向的世界 8 向）。
    public required Func<int, int, (byte Dir, ushort Aim)> ToWorld { get; init; }

    /// 普攻/技能施放朝向（软锁定敌人 / 相机前方；0 = 不指定）——每 tick 由客户端刷新
    public ushort AimQuantum { get; set; }

    /// 最近一次移动方向的世界 8 向（翻滚/受身取向用；Mapper 内写入）
    public byte LastDir { get; set; }
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

    /// keys: 移动(扫描 WASD 组合)/跳/翻滚/普攻/技能键/格挡键。ctx 为空时维持传统 8 向语义。
    public List<Command> Collect(IReadOnlyList<(int key, string role, ushort skillUid)> bindings, MoveCtx? ctx = null)
    {
        var cmds = new List<Command>(4);
        var nowDown = new HashSet<int>();

        // 移动（Held——每 tick 扫描组合键）
        int dx = 0, dz = 0;
        foreach (var (key, ddx, ddz) in MoveKeys)
            if (_poller.IsDown(key)) { nowDown.Add(key); dx += ddx; dz += ddz; }
        if (dx != 0 || dz != 0)
        {
            if (ctx is not null)
            {
                var (dir, aim) = ctx.ToWorld(dx, dz);
                cmds.Add(aim != 0
                    ? new Command(_fighterId, CmdKind.Move, 0, aim, 255, 0)   // 模拟量
                    : new Command(_fighterId, CmdKind.Move, 0, 0, dir, 0));
                ctx.LastDir = dir;
            }
            else
            {
                cmds.Add(new Command(_fighterId, CmdKind.Move, 0, 0, DirIndex(dx, dz), 0));
            }
        }

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
                        // 方向 = 当前移动方向（相机相对 8 向）——受身（Down 态）同样走这条指令
                        cmds.Add(new Command(_fighterId, CmdKind.Roll, 0, 0, ctx?.LastDir ?? 0, 0));
                        break;
                    case "basic":
                        cmds.Add(new Command(_fighterId, CmdKind.Basic, 0, ctx?.AimQuantum ?? 0, 0, 0));
                        break;
                    case "skill":
                        if (skillUid != 0) cmds.Add(new Command(_fighterId, CmdKind.Skill, skillUid, ctx?.AimQuantum ?? 0, 0, 0));
                        break;
                    case "guard":
                        // 格挡=按住型姿态技（带软锁朝向——盾面必须对着敌人才吸伤）
                        if (skillUid != 0) cmds.Add(new Command(_fighterId, CmdKind.Skill, skillUid, ctx?.AimQuantum ?? 0, 0, 0));
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

    /// 上一 tick 移动方向的世界 8 向（翻滚/受身方向用——移动中按翻滚 = 朝移动方向翻）
    private static readonly (int key, int dx, int dz)[] MoveKeys =
    {
        ((int)KeyIds.W, 0, -1), ((int)KeyIds.S, 0, 1), ((int)KeyIds.A, -1, 0), ((int)KeyIds.D, 1, 0),
    };

    /// 键位常量（与 Godot Key 枚举数值一致——避免 Infra 依赖 Godot）
    public static class KeyIds
    {
        public const int W = 87, A = 65, S = 83, D = 68;
        public const int Space = 32, Shift = 4194325, J = 74, K = 75, L = 76, U = 85, I = 73, O = 79, P = 80;
        // 鼠标哨兵（Godot 适配层翻译成 IsMouseButtonPressed；避开 Key 枚举值域）
        public const int MouseLeft = 1000, MouseRight = 1001;
    }

    private static byte DirIndex(int dx, int dz) => (dx, dz) switch
    {
        (0, 1) => 0, (1, 1) => 1, (1, 0) => 2, (1, -1) => 3,
        (0, -1) => 4, (-1, -1) => 5, (-1, 0) => 6, (-1, 1) => 7,
        _ => 0,
    };
}
