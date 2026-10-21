#version 330 core

in vec2 v_uv;
out vec4 FragColor;

uniform sampler2D u_text;

void main()
{
    FragColor = texture(u_text, v_uv);
}
