using System;
using System.Collections.Generic;

// PRODUCTION - Arena.Infra.Data
// SPEC-0004 强类型 Kind Registry: arena.csv 的 kind 列的**唯一**解析/语义映射点。
// Sim 侧 BuildTerrain 与 Client 侧 ArenaBuilder 都消费本表——禁止再散落字符串 switch。
namespace Arena.Infra.Data;

public enum ArenaObjectKind
{
    Boundary,     // 结界墙（外场半空间×4，弹体/实体 Bounce）
    Platform,     // 高台（QueryGround 高地，不阻挡水平）
    Ramp,         // 坡道（高地过渡）
    CoverWall,    // 可破坏掩体墙（Stop + 弹体摧毁）
    Pillar,       // 石柱（Stop + 弹体摧毁；特殊技能可碎）
    PropWood,     // 木箱（Stop + 弹体摧毁）
    PropPot,      // 陶罐（拾取 MP——不入碰撞世界）
    PropRock,     // 边界乱石（Stop + 弹体摧毁）
    Spawn,        // 出生点（标记，不入 Sim/视觉主体）
    Unknown,
}

/// kind 的集中语义表（ArenaDefParser/MatchRoot/ArenaBuilder 共同消费）
public static class ArenaKindRegistry
{
    private static readonly Dictionary<string, ArenaObjectKind> ByName = new(StringComparer.Ordinal)
    {
        ["boundary"] = ArenaObjectKind.Boundary,
        ["platform"] = ArenaObjectKind.Platform,
        ["ramp"] = ArenaObjectKind.Ramp,
        ["cover_wall"] = ArenaObjectKind.CoverWall,
        ["pillar"] = ArenaObjectKind.Pillar,
        ["prop_wood"] = ArenaObjectKind.PropWood,
        ["prop_pot"] = ArenaObjectKind.PropPot,
        ["prop_rock"] = ArenaObjectKind.PropRock,
        ["spawn"] = ArenaObjectKind.Spawn,
    };

    public static ArenaObjectKind Parse(string kind) =>
        ByName.TryGetValue(kind, out var k) ? k : ArenaObjectKind.Unknown;

    /// Sim 侧 TerrainAction（SPEC-0004 §1 语义——ArenaDefParser.BuildTerrain 消费）
    public static TerrainActionKind Action(ArenaObjectKind kind) => kind switch
    {
        ArenaObjectKind.Boundary => TerrainActionKind.Bounce,
        ArenaObjectKind.CoverWall or ArenaObjectKind.Pillar
            or ArenaObjectKind.PropWood or ArenaObjectKind.PropRock => TerrainActionKind.DestroyProjectile,
        _ => TerrainActionKind.PassThrough,   // platform/ramp/spawn/pot: 高地/标记（不阻挡）
    };

    /// 是否入 Sim 碰撞/高地世界
    public static bool EntersSim(ArenaObjectKind kind) => kind is not (ArenaObjectKind.Spawn or ArenaObjectKind.PropPot);

    /// 是否需要视觉网格（Client ArenaBuilder 消费）
    public static bool NeedsVisual(ArenaObjectKind kind) => kind is not (ArenaObjectKind.Spawn or ArenaObjectKind.Unknown);
}

/// Registry 输出的语义动作（与 Arena.Core.TerrainAction 值域一一对应——避免 Infra 侧重复枚举歧义）
public enum TerrainActionKind { PassThrough, Bounce, Stop, DestroyProjectile }
