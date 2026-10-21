#version 330 core

// Reuses FullscreenQuad's [-1,1] vertices, squeezed into u_rect (NDC: x0, y0, x1, y1) — used by
// Graphics/TextOverlay.cs to place the diagnostics panel.

layout (location = 0) in vec2 a_position;
layout (location = 1) in vec2 a_uv;

uniform vec4 u_rect;

out vec2 v_uv;

void main()
{
    vec2 t = a_position * 0.5 + 0.5;
    // Texture row 0 is the top line of text, so flip v.
    v_uv = vec2(a_uv.x, 1.0 - a_uv.y);
    gl_Position = vec4(mix(u_rect.xy, u_rect.zw, t), 0.0, 1.0);
}
