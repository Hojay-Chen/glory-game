using System;
using Arena.Core;
using Arena.Core.Sim;
using Godot;

// PRODUCTION - Arena.Client
// Fighter 表现: 胶囊占位 + 队伍色 + 状态色反馈（命中闪白/倒地暗化/浮空抬高）。
// 全部姿态/动作数据来自 Sim 权威状态（P3 表现仿真分离——表现层不预测）。
namespace Arena.Client;

public sealed partial class FighterView : Node3D
{
    private readonly int _id;
    private readonly MeshInstance3D _capsule;
    private readonly MeshInstance3D _headingArrow;
    private readonly StandardMaterial3D _mat;
    private readonly Label3D _nameTag;

    public FighterView(int id, byte team)
    {
        _id = id;
        _mat = new StandardMaterial3D
        {
            AlbedoColor = team == 0 ? new Color(0.25f, 0.5f, 1f) : new Color(1f, 0.35f, 0.3f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.05f, 0.05f),
        };
        _capsule = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.6f },
            MaterialOverride = _mat,
            Position = new Godot.Vector3(0, 0.8f, 0),
        };
        AddChild(_capsule);

        // 朝向箭（+Z 局部——heading 0）
        _headingArrow = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Godot.Vector3(0.12f, 0.12f, 0.5f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.9f, 0.2f) },
            Position = new Godot.Vector3(0, 0.9f, 0.45f),
        };
        AddChild(_headingArrow);

        _nameTag = new Label3D
        {
            Text = $"F{id}",
            FontSize = 40,
            Position = new Godot.Vector3(0, 2.1f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        AddChild(_nameTag);
    }

    public void Sync(FighterStateData f)
    {
        Position = new Godot.Vector3(
            (float)(f.PosX.Raw / 65536.0),
            (float)(f.PosY.Raw / 65536.0),
            (float)(f.PosZ.Raw / 65536.0));

        // heading: Sim 0=+Z 顺时针 → Godot rotation.y（逆时针正）
        Rotation = new Godot.Vector3(0, -(float)(f.HeadingQuantum / 65536.0 * Math.PI * 2), 0);

        // 状态反馈色
        var col = f.Team == 0 ? new Color(0.25f, 0.5f, 1f) : new Color(1f, 0.35f, 0.3f);
        switch (f.State)
        {
            case FighterState.Launch: col = col.Lightened(0.35f); break;
            case FighterState.Down: col = col.Darkened(0.5f); break;
            case FighterState.Hitstun: col = new Color(1f, 0.75f, 0.4f); break;
            case FighterState.Act: col = col.Lightened(0.2f); break;
            case FighterState.Dead: col = new Color(0.15f, 0.15f, 0.15f); break;
            case FighterState.Grabbed: col = new Color(0.8f, 0.2f, 0.8f); break;
        }
        if (f.Hidden) col = new Color(0.35f, 0.35f, 0.45f);
        _mat.AlbedoColor = col;

        _capsule.RotationDegrees = f.State == FighterState.Down ? new Godot.Vector3(-90, 0, 0) : Godot.Vector3.Zero;
        _capsule.Position = f.State == FighterState.Down ? new Godot.Vector3(0, 0.4f, 0) : new Godot.Vector3(0, 0.8f, 0);
        _nameTag.Text = f.State == FighterState.Dead ? "KO" : f.Team == 0 ? $"P1 {f.Hp / 100}" : $"BOT {f.Hp / 100}";
    }
}
