using System.Collections.Generic;
using Arena.Core;
using Arena.Infra.Input;
using Arena.Infra.Data;
using Godot;

// PRODUCTION - Arena.Client
// 键位 → Command（ADR-0010 v1 键鼠；Batch 7 Part 3 裁定的 Press/Held/Release 语义）:
//   WASD=移动(Held) | Space=跳(Press) | Shift=翻滚(Press) | J=普攻链(Press)
//   K=上挑(Press) | L=三段斩(Press) | I=仙人指路(Press) | O=拔刀斩(Press)
//   U=格挡(Press 进入 hold，Release 发普攻释放——hold 释放走 TryCastBasic 分支)
// Sim 侧合法性/CD/缓冲仍是最终裁决——本层只产 Command。
namespace Arena.Client;

public static class InputTranslator
{
    /// Godot Input → IKeyPoller 适配（键位数值与 Godot.Key 枚举一致）
    public sealed class GodotKeyPoller : IKeyPoller
    {
        public bool IsDown(int key) => Input.IsKeyPressed((Key)key);
    }

    /// 每局重建的 mapper（Press/Held/Release 状态自持——Restart 时随 MatchRoot 重置）
    public static InputMapper Create(int fighterId, RuntimeCatalog catalog) =>
        new(new GodotKeyPoller(), key => catalog.IdMap.TryGetValue(SkillOf(key), out var uid) ? uid : (ushort)0, fighterId);

    /// key → 技能键位表（VS v1: BLA）
    public static string SkillOf(int key) => key switch
    {
        (int)Key.K => "BLA_T1_001",   // 上挑
        (int)Key.L => "BLA_T2_001",   // 三段斩
        (int)Key.I => "BLA_T2_006",   // 仙人指路
        (int)Key.O => "BLA_T2_003",   // 拔刀斩
        _ => "",
    };

    /// 绑定表（InputMapper.Collect 消费）
    public static List<(int key, string role, ushort skillUid)> Bindings(RuntimeCatalog catalog)
    {
        ushort Uid(string sid) => catalog.IdMap.TryGetValue(sid, out var u) ? u : (ushort)0;
        return new List<(int, string, ushort)>
        {
            ((int)Key.Space, "jump", 0),
            ((int)Key.Shift, "roll", 0),
            ((int)Key.J, "basic", 0),
            ((int)Key.K, "skill", Uid("BLA_T1_001")),
            ((int)Key.L, "skill", Uid("BLA_T2_001")),
            ((int)Key.I, "skill", Uid("BLA_T2_006")),
            ((int)Key.O, "skill", Uid("BLA_T2_003")),
            ((int)Key.U, "guard", 0),
        };
    }
}
