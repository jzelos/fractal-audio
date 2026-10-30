#version 330 core

// Copies an offscreen image (Graphics/FeedbackBuffer.cs) to the screen.

in vec2 v_uv;
out vec4 FragColor;

uniform sampler2D u_image;

void main()
{
    FragColor = vec4(texture(u_image, v_uv).rgb, 1.0);
}
