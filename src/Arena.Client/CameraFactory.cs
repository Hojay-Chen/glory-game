using System;
using Arena.Core;
using Arena.Core.Sim;
using Godot;

// PRODUCTION - Arena.Client
// 跟随相机: 两战斗员中点上方俯视（GDD §19 竞技场观战角——距离随间距轻度缩放）。
namespace Arena.Client;

public sealed class CameraFactory
{
    public static Camera3D CreateFollowCamera()
    {
        var cam = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Perspective,
            Fov = 50,
        };
        return cam;
    }

    public static void Follow(Camera3D cam, Godot.Vector3 midpoint, float spread)
    {
        var back = Math.Clamp(9f + spread * 0.45f, 10f, 20f);
        cam.Position = new Godot.Vector3(midpoint.X, midpoint.Y + back * 0.85f, midpoint.Z - back * 0.65f);
        cam.LookAt(new Godot.Vector3(midpoint.X, 1f, midpoint.Z), Godot.Vector3.Up);
    }
}
