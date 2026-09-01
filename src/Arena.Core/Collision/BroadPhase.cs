using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Core.Calc;
// PRODUCTION - Arena.Core
// SPEC-0005 PA-6: BroadPhase——均匀网格 8m cell，实体按 swept-AABB 入格。
// 保守性: 候选集 ⊇ 真接触集（swept 端点+半径并集包围盒，[minCell,maxCell] 闭区间覆盖）。
// 等价性契约（T55）: BroadPhase+NarrowPhase ≡ 全量 NarrowPhase（逐位）——CI 双路径比对。
// BroadPhase 结果不进事件/快照/hash——纯瞬时性能缓存。
namespace Arena.Core.Collision;

public sealed class BroadPhase
{
    private readonly long _cellSize;
    // 格桶: (cellX, cellZ) → 实体 Id 集（确定性遍历 = 查询端按 Id 排序输出）
    private readonly Dictionary<long, List<int>> _cells = new();
    private readonly List<int> _result = new();
    // 无界体（结界墙半空间）的包围盒钳制域（±128m——战场语义包络，仅影响 BP 分格，不影响几何）
    private const long CLAMP = 1L << 23;

    public BroadPhase(long cellSize) => _cellSize = cellSize;

    private static long CellKey(long cx, long cz) => (cx << 32) | (cz & 0xFFFFFFFFL);

    private static long ClampCoord(long v) => Math.Clamp(v, -CLAMP, CLAMP);

    public void Clear() => _cells.Clear();

    /// 实体按 swept-AABB 入格（from/to 端点与半径的并集包围盒，PA-6.1）
    public void Insert(int id, long fromX, long fromZ, long toX, long toZ, long radius)
    {
        long minX = ClampCoord(Math.Min(fromX, toX) - radius), maxX = ClampCoord(Math.Max(fromX, toX) + radius);
        long minZ = ClampCoord(Math.Min(fromZ, toZ) - radius), maxZ = ClampCoord(Math.Max(fromZ, toZ) + radius);
        long cx0 = DeterministicMath.DivFloor(minX, _cellSize), cx1 = DeterministicMath.DivFloor(maxX, _cellSize);
        long cz0 = DeterministicMath.DivFloor(minZ, _cellSize), cz1 = DeterministicMath.DivFloor(maxZ, _cellSize);
        for (long cx = cx0; cx <= cx1; cx++)
            for (long cz = cz0; cz <= cz1; cz++)
            {
                long key = CellKey(cx, cz);
                if (!_cells.TryGetValue(key, out var list))
                    _cells[key] = list = new List<int>();
                list.Add(id);
            }
    }

    /// 查询: 包围盒覆盖格的候选实体（升序去重）
    public List<int> Query(long minX, long maxX, long minZ, long maxZ)
    {
        _result.Clear();
        minX = ClampCoord(minX); maxX = ClampCoord(maxX);
        minZ = ClampCoord(minZ); maxZ = ClampCoord(maxZ);
        long cx0 = DeterministicMath.DivFloor(minX, _cellSize), cx1 = DeterministicMath.DivFloor(maxX, _cellSize);
        long cz0 = DeterministicMath.DivFloor(minZ, _cellSize), cz1 = DeterministicMath.DivFloor(maxZ, _cellSize);
        for (long cx = cx0; cx <= cx1; cx++)
            for (long cz = cz0; cz <= cz1; cz++)
            {
                if (!_cells.TryGetValue(CellKey(cx, cz), out var list)) continue;
                for (int i = 0; i < list.Count; i++)
                    if (!_result.Contains(list[i])) _result.Add(list[i]);
            }
        _result.Sort();
        return _result;
    }
}
