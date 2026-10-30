#version 330 core

// Julia-set escape-time fractal with smooth (continuous) iteration coloring and a
// derivative-based distance estimate for a crisp, anti-aliased-looking boundary.
//
// z0 varies per-pixel (mapped from screen space via u_zoom/u_center), c is a constant per frame
// (in Tier 1, driven by audio via Mapping/AudioToFractalMapper.cs — see u_c).

in vec2 v_uv;
out vec4 FragColor;

uniform vec2 u_resolution;
uniform vec2 u_c;
uniform float u_zoom;
uniform vec2 u_center;
uniform int u_maxIter;
uniform float u_paletteOffset;
uniform sampler2D u_palette;     // atlas: one 256-wide row per palette (Graphics/PaletteTexture.cs).
uniform int u_paletteFrom;       // atlas row being faded out
uniform int u_paletteTo;         // atlas row being faded in
uniform float u_paletteBlend;    // 0 = all "from", 1 = all "to"

const float BAILOUT2 = 4.0; // |z|^2 > 4.0 standard escape-time bailout.

// Interior (orbit-trap) colouring, tune-by-eye — edit the copy in bin/.../shaders while running.
const float INTERIOR_SCALE = 1.6;       // palette cycles per unit of closest approach to the origin.
const float INTERIOR_SHIFT = 0.5;       // half a palette away from the exterior, for contrast.
const float INTERIOR_BRIGHTNESS = 0.65; // below the exterior, so the silhouette and trails still read.

vec2 cmul(vec2 a, vec2 b)
{
    return vec2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x);
}

vec3 paletteRow(int row, float t)
{
    // Sample the row's vertical centre so linear filtering never bleeds in a neighbouring row.
    float rows = float(textureSize(u_palette, 0).y);
    return texture(u_palette, vec2(t, (float(row) + 0.5) / rows)).rgb;
}

void main()
{
    vec2 uv = (v_uv - 0.5) * 2.0;
    uv.x *= u_resolution.x / max(u_resolution.y, 1.0);

    vec2 z = uv / u_zoom + u_center;
    vec2 dz = vec2(1.0, 0.0); // d(z)/d(z0), seeded at 1 since z0 is the varying pixel coordinate.

    bool escaped = false;
    float smoothIter = float(u_maxIter);
    vec2 finalZ = z;
    vec2 finalDz = dz;
    float trap2 = dot(z, z); // closest squared approach of the orbit to the origin.

    for (int i = 0; i < u_maxIter; i++)
    {
        vec2 nextDz = 2.0 * cmul(z, dz);
        vec2 nextZ = cmul(z, z) + u_c;
        z = nextZ;
        dz = nextDz;

        float mag2 = dot(z, z);
        trap2 = min(trap2, mag2);
        if (mag2 > BAILOUT2)
        {
            // Smooth iteration count: n - log2(log2(|z|)) correction, standard escape-time coloring.
            float logZn = 0.5 * log(mag2);
            float nu = log(logZn / log(2.0)) / log(2.0);
            smoothIter = float(i) + 1.0 - nu;
            finalZ = z;
            finalDz = dz;
            escaped = true;
            break;
        }
    }

    vec3 color;
    if (!escaped)
    {
        // Interior of the filled Julia set: an orbit trap — how close the orbit came to the
        // origin, through the same (audio-rotated, crossfading) palette, shifted and dimmed so
        // the boundary still reads. Gives smooth bands that follow the shape of the set.
        float t = fract(sqrt(trap2) * INTERIOR_SCALE + u_paletteOffset + INTERIOR_SHIFT);
        color = mix(paletteRow(u_paletteFrom, t), paletteRow(u_paletteTo, t), u_paletteBlend);
        color *= INTERIOR_BRIGHTNESS;
    }
    else
    {
        float t = fract(smoothIter * 0.015 + u_paletteOffset);
        color = mix(paletteRow(u_paletteFrom, t), paletteRow(u_paletteTo, t), u_paletteBlend);

        // Distance estimate (in z0/pixel-space units) for boundary crispness.
        float zMag = length(finalZ);
        float dzMag = max(length(finalDz), 1e-6);
        float de = 0.5 * zMag * log(zMag) / dzMag;
        float pixelSize = 1.0 / (u_zoom * max(u_resolution.y, 1.0));
        float edge = clamp(de / pixelSize, 0.0, 1.0);
        color *= mix(0.35, 1.0, edge);
    }

    FragColor = vec4(color, 1.0);
}
