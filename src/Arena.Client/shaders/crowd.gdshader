shader_type spatial;

// PRODUCTION - Arena.Client
// 观众着色器：逐实例呼吸摆动（相位编码在实例色 R 通道）+ 衣色（G/B/A）。
// MultiMesh 单 draw call 渲染全场观众——gl_compatibility 时代不可想，Forward+ 基本功。
void vertex() {
    VERTEX.y += sin(TIME * 2.6 + COLOR.r * 6.2831) * 0.05;
}

void fragment() {
    ALBEDO = vec3(COLOR.g, COLOR.b, COLOR.a);
    ROUGHNESS = 0.95;
    SPECULAR = 0.15;
}
