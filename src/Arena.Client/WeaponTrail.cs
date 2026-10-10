using System;
using Arena.Infra.Presentation;
using Godot;

// PRODUCTION - Arena.Client
// VS-8 武器尾迹: 逐帧采样武器尖端的世界坐标，铺成一条带渐隐的带状网格。
// 分工: 生命周期/老化/上限/去重全部在 Arena.Infra.Presentation.TrailBuffer（纯逻辑，可测）；
//   本节点只负责「点集合 → ImmediateMesh」的网格翻译——不含任何判定。
// 顶点已是世界坐标 ⇒ TopLevel = true（脱离 FighterView 变换）；节点挂在 FighterView 下，
//   重开对局时随视图一起 QueueFree，不会残留。
namespace Arena.Client;

public sealed partial class WeaponTrail : MeshInstance3D
{
    private readonly TrailBuffer _buffer = new();
    private readonly ImmediateMesh _mesh = new();
    private readonly Node3D _tipPivot;
    private readonly float _tipLength;
    private readonly float _halfWidth;
    private readonly Color _color;

    public WeaponTrail(Node3D tipPivot, float tipLength, float halfWidth, Color color)
    {
        _tipPivot = tipPivot;
        _tipLength = tipLength;
        _halfWidth = halfWidth;
        _color = color;

        TopLevel = true;                     // 顶点即世界坐标——不受父节点变换影响
        Mesh = _mesh;
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = Colors.White,      // 反照率取顶点色（渐隐靠顶点 alpha）
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    public TrailBuffer Buffer => _buffer;

    public void Arm(int skillId) => _buffer.Arm(skillId);
    public void Disarm(int skillId) => _buffer.Disarm(skillId);

    /// 阵亡 / 终局 / 重开——立即清空，不留淡出残留
    public void Clear()
    {
        _buffer.Clear();
        if (_mesh.GetSurfaceCount() > 0) _mesh.ClearSurfaces();
    }

    /// 每表现帧采样 + 重建网格（Hit Stop 冻结期由调用方跳过——武器不动，尾迹自然冻结）
    public void Sample(double deltaSeconds)
    {
        var tip = _tipPivot.GlobalTransform * new Godot.Vector3(0, _tipLength, 0);
        _buffer.Advance(tip.X, tip.Y, tip.Z, deltaSeconds);
        Rebuild();
    }

    private void Rebuild()
    {
        _mesh.ClearSurfaces();
        int n = _buffer.Count;
        if (n < 2) return;   // 单点构不成带

        _mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, MaterialOverride);
        for (int i = 0; i < n; i++)
        {
            var p = _buffer[i];
            var pos = new Godot.Vector3(p.X, p.Y, p.Z);

            // 切线 = 邻点差分（首尾单侧）→ 与世界上方向叉乘得到带宽方向
            var a = _buffer[i > 0 ? i - 1 : i];
            var b = _buffer[i < n - 1 ? i + 1 : i];
            var tangent = new Godot.Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            if (tangent.LengthSquared() < 1e-8f) tangent = Godot.Vector3.Up;
            var side = tangent.Normalized().Cross(Godot.Vector3.Up);
            if (side.LengthSquared() < 1e-8f) side = Godot.Vector3.Right;

            float fade = _buffer.FadeAt(i);
            side = side.Normalized() * (_halfWidth * fade);

            var col = new Color(_color.R, _color.G, _color.B, fade * fade);   // 二次渐隐：尾端更快消失
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(pos + side);
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(pos - side);
        }
        _mesh.SurfaceEnd();
    }
}
