#version 330 core

// Feedback composite (Graphics/FeedbackBuffer.cs): the previous output, zoomed out from the
// centre, rotated and faded, under the freshly rendered fractal — keeping the brighter of the two,
// so bright detail leaves trails over the dark interior and nothing can saturate.

in vec2 v_uv;
out vec4 FragColor;

uniform sampler2D u_fresh;
uniform sampler2D u_previous;
uniform vec2 u_resolution;
uniform float u_fade;      // multiplier on the previous frame, < 1.
uniform float u_zoom;      // > 1 pushes the previous frame outward.
uniform float u_rotation;  // radians this frame.

void main()
{
    // Warp in aspect-correct space around the screen centre, so rotation doesn't shear.
    float aspect = u_resolution.x / max(u_resolution.y, 1.0);
    vec2 p = (v_uv - 0.5) * vec2(aspect, 1.0);
    float s = sin(-u_rotation);
    float c = cos(-u_rotation);
    p = mat2(c, s, -s, c) * p / u_zoom;
    vec2 uv = p / vec2(aspect, 1.0) + 0.5;

    vec3 previous = vec3(0.0);
    if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0))))
    {
        previous = texture(u_previous, uv).rgb * u_fade;
    }

    vec3 fresh = texture(u_fresh, v_uv).rgb;
    FragColor = vec4(max(fresh, previous), 1.0);
}
