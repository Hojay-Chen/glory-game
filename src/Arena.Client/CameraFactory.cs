using System;
using Arena.Core;
using Arena.Core.Sim;
using Godot;

// PRODUCTION - Arena.Client
// 战斗相机（GDD §21.3: 第三人称越肩动作机位，非转播俯瞰）。
//   · **玩家相对**：机位在本地玩家(P0)背后、沿 P0→敌人方向朝前看——这是动作游戏的可读性根，
//     旧实现把相机钉在中点固定的 −Z 侧（转播机），两人靠近时**互相遮挡**，读起来像围棋回放。
//   · 越肩：距离基础 4.2m + 随间距 0.4×，夹 [4, 11]；FOV 60（GDD）。
//   · 锁定感：视线落点偏向敌人，玩家在画面下三分之一，敌人落在上三分之二。
//   · 平滑：位置/朝向各留惯性，运镜有重量但不飘。
// 与判定无关（纯视觉）——只读 Sim 已同步的坐标，不预测。
namespace Arena.Client;

public sealed class CameraFactory
{
    private const float PosLerp = 0.18f;
    private const float LookLerp = 0.24f;

    private static Godot.Vector3 _lookAt = new(0, 1.3f, 0);
    private static bool _init;

    public static Camera3D CreateFollowCamera() => new()
    {
        Projection = Camera3D.ProjectionType.Perspective,
        Fov = 60,                 // GDD §21.3
        Near = 0.05f,
        Far = 500f,
    };

    public static void ResetLook(Godot.Vector3 at) { _lookAt = at; _init = false; }

    /// 越肩锁定机位：在玩家背后、朝敌人看；两人重叠时自动侧移错开。
    public static void Follow(Camera3D cam, Godot.Vector3 player, Godot.Vector3 enemy)
    {
        var flat = new Godot.Vector3(enemy.X - player.X, 0, enemy.Z - player.Z);
        float dist = flat.Length();
        if (dist < 0.01f) dist = 0.01f;
        var dir = flat / dist;

        // 距离: 越肩基准 4.2m（GDD）+ 随间距 0.4×；上限 11m（再远就把角色压成棋子）
        float back = Mathf.Clamp(4.2f + dist * 0.40f, 4.2f, 11f);

        // 机位: 玩家背后，略微抬高并侧移一点（越肩构图，避免完全挡住玩家自己的角色）
        var right = new Godot.Vector3(dir.Z, 0, -dir.X);   // 水平右向量
        var focus = new Godot.Vector3(player.X, player.Y, player.Z);
        var want = focus - dir * back + right * 0.55f + new Godot.Vector3(0, 2.0f + dist * 0.06f, 0);
        if (want.Y < 0.8f) want.Y = 0.8f;

        // 视线落点: 偏向敌人（不只是玩家），让敌人处于画面上部、双方都读得到
        var target = new Godot.Vector3(
            player.X + (enemy.X - player.X) * 0.62f,
            1.25f,
            player.Z + (enemy.Z - player.Z) * 0.62f);

        if (!_init) { cam.Position = want; _lookAt = target; _init = true; }
        else
        {
            cam.Position = cam.Position.Lerp(want, PosLerp);
            _lookAt = _lookAt.Lerp(target, LookLerp);
        }
        cam.LookAt(_lookAt, Godot.Vector3.Up);
    }

    /// 入场演出: 从高远处缓降压到战斗机位（§1.7 镜头拉升）。
    public static void Intro(Camera3D cam, Godot.Vector3 player, Godot.Vector3 enemy, float t01)
    {
        float e = 1f - MathF.Pow(1f - Math.Clamp(t01, 0f, 1f), 3f);
        var mid = (player + enemy) * 0.5f;
        float dist = (enemy - player).Length();
        float back = Mathf.Lerp(16f, Mathf.Clamp(4.2f + dist * 0.40f, 4.2f, 11f), e);
        float h = Mathf.Lerp(10f, 2.0f + dist * 0.06f, e);
        var target = new Godot.Vector3(mid.X, Mathf.Lerp(0.4f, 1.25f, e), mid.Z);
        cam.Position = new Godot.Vector3(mid.X, h, mid.Z + back);
        cam.LookAt(target, Godot.Vector3.Up);
    }
}
