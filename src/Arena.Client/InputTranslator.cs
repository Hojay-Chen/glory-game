using System.Collections.Generic;
using Arena.Infra.Data;
using Arena.Infra.Input;
using Godot;

// PRODUCTION - Arena.Client
// Godot 侧输入适配（薄层）:
//   WASD=移动(Held) | Space=跳 | Shift=翻滚 | J=普攻链 | K/L/I/O/P=各档技 | U=格挡 | R=重开 | Esc=换职业
// **键位→技能号的生成规则在 Arena.Infra.Input.ClassBindings**（纯逻辑、可测、按职业通用），
// 本层只做 Input 轮询适配——Godot 层不承载任何战斗/映射规则。
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
        new(new GodotKeyPoller(), _ => 0, fighterId);

    public static List<(int key, string role, ushort skillUid)> Bindings(RuntimeCatalog catalog, string classId) =>
        ClassBindings.For(catalog, classId);

    public static List<(string Key, string Label)> KeyHints(RuntimeCatalog catalog, string classId) =>
        ClassBindings.Hints(catalog, classId);
}
