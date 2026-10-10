using System;
using Arena.Core;
using Arena.Core.Sim;
using Godot;

// PRODUCTION - Arena.Client
// 战斗相机（GDD §21.3: 第三人称越肩动作机位，非转播俯瞰）。
//   · 越肩：贴在中点后上方，俯角 ~20°，让角色在画面里"有身体"，而不是一枚棋子。
//   · 自适应帧内：间距越大拉得越远，但**上限收紧**（旧实现 9–26m/FOV38 把角色压到屏高 11%，
//     读起来像 demo）。FOV 60 + 距离上限 13m，保证任何时候角色都占屏高 ≥22%。
//   · 镜头碰撞：贴地/穿墙时抬高到地板之上，避免钻进几何。
// 与判定无关（纯视觉）——只读 Sim 已同步的坐标，不预测。
namespace Arena.Client;

public sealed class CameraFactory
{
    // 平滑跟随速度（防抖；Sim 60Hz 采样仍留一点惯性让运镜有重量感）
    private const float PosLerp = 0.22f;
    private const float LookLerp = 0.28f;

    private static Godot.Vector3 _lookAt = new(0, 1.2f, 0);
    private static bool _init;

    public static Camera3D CreateFollowCamera()
    {
        var cam = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Perspective,
            Fov = 60,                 // GDD §21.3
            Near = 0.05f,
            Far = 400f,
        };
        return cam;
    }

    /// 让相机看向场景中央（入场/结算用）。
    public static void ResetLook(Godot.Vector3 at) { _lookAt = at; _init = false; }

    /// 动作机位: 中点后上方，俯角 ~20°，随间距自适应（上限 13m）。
    public static void Follow(Camera3D cam, Godot.Vector3 midpoint, float spread)
    {
        // 距离: 基础 4.2m（越肩基准 GDD）+ 随间距 0.5×，夹在 [5.2, 13]。
        // 上限比旧实现低一半——宁可让远距的一方略微出框，也不把双方压成小棋子。
        float back = Math.Clamp(4.2f + spread * 0.50f, 5.2f, 13f);

        // 俯角 ~20°：高度 = 水平后距 × tan(20°) ≈ 0.36，再抬到视线高度之上
        var target = new Godot.Vector3(midpoint.X, 1.15f, midpoint.Z);
        var want = target + new Godot.Vector3(0, back * 0.36f + 0.9f, -back);

        // 镜头碰撞：不钻到地板以下（gl_compatibility 无合适遮挡查询，做高度下限即可）
        if (want.Y < 0.6f) want.Y = 0.6f;

        if (!_init) { cam.Position = want; _lookAt = target; _init = true; }
        else
        {
            cam.Position = cam.Position.Lerp(want, PosLerp);
            _lookAt = _lookAt.Lerp(target, LookLerp);
        }
        cam.LookAt(_lookAt, Godot.Vector3.Up);
    }

    /// 入场演出: 从高远处缓降压到战斗机位（§1.7 镜头拉升——反向：开局先俯瞰再压近）。
    public static void Intro(Camera3D cam, Godot.Vector3 midpoint, float spread, float t01)
    {
        float e = 1f - MathF.Pow(1f - Math.Clamp(t01, 0f, 1f), 3f);   // ease-out cubic
        float back = Mathf.Lerp(18f, Math.Clamp(4.2f + spread * 0.50f, 5.2f, 13f), e);
        float height = Mathf.Lerp(11f, 0.36f * back + 0.9f, e);
        var target = new Godot.Vector3(midpoint.X, Mathf.Lerp(0.4f, 1.15f, e), midpoint.Z);
        cam.Position = target + new Godot.Vector3(0, height, -back);
        cam.LookAt(target, Godot.Vector3.Up);
    }
}
