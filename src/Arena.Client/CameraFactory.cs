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
        ComputeFollow(player, enemy, out var want, out var target);
        if (!_init) { cam.Position = want; _lookAt = target; _init = true; }
        else
        {
            cam.Position = cam.Position.Lerp(want, PosLerp);
            _lookAt = _lookAt.Lerp(target, LookLerp);
        }
        cam.LookAt(_lookAt, Godot.Vector3.Up);
    }

    /// 鼠标视角轨道机位（2026-10-13 手感重做）：
    ///   yaw   = 视线方位角（与 Sim HeadingQuantum 同约定，0=+Z 顺时针，弧度）
    ///   pitch = 相机相对注视点的仰角（弧度，0.05..1.15）
    ///   dist  = 视距（3.2..9m）
    /// 机位在玩家身后沿视线反方向，注视点 = 玩家前方 + 25% 敌人偏置（软锁画面感）。
    /// 平滑系数比 Follow 更高（鼠标每帧都在动，惯性太大反而晕）。
    public static void Orbit(Camera3D cam, Godot.Vector3 player, Godot.Vector3 enemy,
        float yaw, float pitch, float dist)
    {
        float fx = Mathf.Sin(yaw), fz = Mathf.Cos(yaw);          // 视线前方（XZ）
        var fwd = new Godot.Vector3(fx, 0, fz);

        float cp = Mathf.Cos(pitch), sp = Mathf.Sin(pitch);
        var want = player - fwd * (dist * cp) + new Godot.Vector3(0, 0.9f + dist * sp, 0);
        if (want.Y < 0.7f) want.Y = 0.7f;

        // 注视点: 玩家身前 2.2m 处胸口高度，向敌人偏 25%（两人同框、玩家不占正中）
        var look = new Godot.Vector3(
            player.X + fwd.X * 2.2f + (enemy.X - player.X) * 0.25f,
            1.25f,
            player.Z + fwd.Z * 2.2f + (enemy.Z - player.Z) * 0.25f);

        if (!_init) { cam.Position = want; _lookAt = look; _init = true; }
        else
        {
            cam.Position = cam.Position.Lerp(want, 0.35f);
            _lookAt = _lookAt.Lerp(look, 0.45f);
        }
        cam.LookAt(_lookAt, Godot.Vector3.Up);
    }

    /// 目标机位与视线落点（Follow 与 Intro 共用——保证入场演出**平滑收进**战斗位，
    /// 而不是演出结束瞬间硬切）。
    private static void ComputeFollow(Godot.Vector3 player, Godot.Vector3 enemy,
        out Godot.Vector3 pos, out Godot.Vector3 look)
    {
        var flat = new Godot.Vector3(enemy.X - player.X, 0, enemy.Z - player.Z);
        float dist = flat.Length();
        if (dist < 0.01f) dist = 0.01f;
        var dir = flat / dist;

        // 距离: 越肩基准 4.0m（GDD）+ 随间距缓增；上限 8m（保持"玩家动作机位"体感，
        // 不因两人拉开就退成转播机——远端的敌人本就该小，靠场景/角色可读性补，不靠拉远镜头）
        float back = Mathf.Clamp(3.6f + dist * 0.20f, 3.4f, 7.0f);

        // 机位: 玩家背后**偏右肩**，并抬高成俯视角。
        // 关键：两人恒在同一轴线（敌人就在玩家正前方），若机位不侧移，玩家会**整块挡住敌人**。
        // 侧移(0.66) + 抬高(2.15) + 视线落点也侧移/抬高 三者合力把敌人从玩家身后"挪出来"，
        // 形成经典越肩射击式构图（玩家在左下、敌人落在中上）。
        var right = new Godot.Vector3(dir.Z, 0, -dir.X);   // 水平右向量
        var focus = new Godot.Vector3(player.X, player.Y, player.Z);
        var want = focus - dir * back + right * 0.80f + new Godot.Vector3(0, 2.00f + dist * 0.05f, 0);
        if (want.Y < 1.0f) want.Y = 1.0f;

        // 视线落点: 偏敌人侧、略高于胸——玩家被推到画面左下，敌人落在中上，双方都读得到
        var target = new Godot.Vector3(
            player.X + (enemy.X - player.X) * 0.45f + right.X * 0.22f,
            1.30f,
            player.Z + (enemy.Z - player.Z) * 0.45f + right.Z * 0.22f);

        pos = want;
        look = target;
    }

    /// 入场演出: 从高远处缓降压到战斗机位（§1.7 镜头拉升）。
    /// 末段直接收在 ComputeFollow 的目标位姿上，交棒 Follow 时**无跳变**。
    public static void Intro(Camera3D cam, Godot.Vector3 player, Godot.Vector3 enemy, float t01)
    {
        ComputeFollow(player, enemy, out var fpos, out var flook);
        float e = 1f - MathF.Pow(1f - Math.Clamp(t01, 0f, 1f), 3f);
        var mid = (player + enemy) * 0.5f;
        // 起手：中点上空 11m、后拉 15m 的鸟瞰 → 收到战斗越肩位
        var apos = new Godot.Vector3(mid.X, 11f, mid.Z + 15f);
        var alook = new Godot.Vector3(mid.X, 0.5f, mid.Z);
        cam.Position = apos.Lerp(fpos, e);
        cam.LookAt(alook.Lerp(flook, e), Godot.Vector3.Up);
        _init = false;   // 演出结束交棒时 Follow 直接落到目标位姿（此处已收敛到该位姿）
    }

    /// 终局特写：绕胜者慢速环绕 + 推近（t01 0→1 用约 6s）。
    /// 与 Follow/Intro 一样是**纯表现**，只读已同步的世界坐标。
    public static void Finisher(Camera3D cam, Godot.Vector3 focus, float t01)
    {
        float t = Mathf.Clamp(t01, 0f, 1f);
        float ang = -0.95f + t * 1.75f;                       // 缓慢环绕
        float r = Mathf.Lerp(6.0f, 3.2f, 1f - MathF.Pow(1f - t, 2f));   // 缓推近
        var off = new Godot.Vector3(MathF.Sin(ang), 0, MathF.Cos(ang)) * r;
        cam.Position = focus + off + new Godot.Vector3(0, Mathf.Lerp(2.4f, 1.7f, t), 0);
        cam.LookAt(focus + new Godot.Vector3(0, 1.15f, 0), Godot.Vector3.Up);
    }
}
