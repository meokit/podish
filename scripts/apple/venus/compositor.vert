#version 450
#extension GL_GOOGLE_include_directive : require
#include "compositor-uniforms.glsl"

layout(location = 0) out vec2 uv;
layout(location = 1) out vec2 localPosition;
layout(location = 2) flat out vec2 size;

void main()
{
    uv = vec2(float(gl_VertexIndex & 1), float(gl_VertexIndex >> 1));
    size = rect.zw;
    localPosition = uv * size;
    vec2 p = (rect.xy + localPosition) / originAndViewport.zw;
    gl_Position = vec4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
}
