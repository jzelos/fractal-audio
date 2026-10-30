using Silk.NET.OpenGL;

namespace FractalAudio.Graphics;

/// <summary>
/// Palette atlas: every palette baked once, at startup, into one 256xN GL_TEXTURE_2D (one row of
/// 256 entries per palette), generated from Inigo Quilez's cosine-palette formula:
///   color(t) = a + b * cos(2*pi * (c*t + d))
/// evaluated per-channel (a/b/c/d are each an RGB triple). The shader picks and crossfades rows
/// via uniforms (u_paletteFrom/u_paletteTo/u_paletteBlend), so switching palettes never
/// re-uploads texture data and needs no second texture unit. Uses TEXTURE_2D rather than
/// TEXTURE_1D — the plan flagged 1D textures as a possible awkward fit under the loaded GL
/// profile, and a 2D atlas is what row selection needs anyway.
/// </summary>
public sealed class PaletteTexture : IDisposable
{
    public const int LutSize = 256;

    private readonly GL _gl;

    public uint Handle { get; }
    public int PaletteCount => Palettes.Length;

    // Each entry: (a, b, c, d), each a (r,g,b) triple — hand-picked IQ cosine palettes.
    private static readonly (float[] A, float[] B, float[] C, float[] D)[] Palettes =
    {
        // Warm sunset
        (new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f, 0.5f }, new[] { 1.0f, 1.0f, 1.0f }, new[] { 0.00f, 0.10f, 0.20f }),
        // Cool cyan/blue/purple
        (new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f, 0.5f }, new[] { 1.0f, 1.0f, 1.0f }, new[] { 0.30f, 0.20f, 0.20f }),
        // Rainbow-ish
        (new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f, 0.5f }, new[] { 1.0f, 1.0f, 1.0f }, new[] { 0.00f, 0.33f, 0.67f }),
        // Fire (red/orange/yellow)
        (new[] { 0.6f, 0.5f, 0.4f }, new[] { 0.6f, 0.5f, 0.4f }, new[] { 1.0f, 1.0f, 1.0f }, new[] { 0.00f, 0.05f, 0.10f }),
        // Neon green/magenta
        (new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f, 0.5f }, new[] { 1.0f, 0.7f, 0.4f }, new[] { 0.50f, 0.20f, 0.90f }),
        // Deep ocean
        (new[] { 0.3f, 0.4f, 0.5f }, new[] { 0.3f, 0.3f, 0.3f }, new[] { 1.0f, 1.0f, 1.0f }, new[] { 0.60f, 0.50f, 0.40f }),
        // Pastel
        (new[] { 0.6f, 0.6f, 0.6f }, new[] { 0.4f, 0.4f, 0.4f }, new[] { 0.7f, 0.8f, 1.0f }, new[] { 0.10f, 0.40f, 0.70f }),
        // Monochrome amber (idle/calm palette)
        (new[] { 0.45f, 0.35f, 0.2f }, new[] { 0.3f, 0.2f, 0.1f }, new[] { 0.6f, 0.5f, 0.4f }, new[] { 0.0f, 0.1f, 0.2f }),
    };

    public PaletteTexture(GL gl)
    {
        _gl = gl;
        Handle = _gl.GenTexture();

        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        Upload();
    }

    private unsafe void Upload()
    {
        byte[] pixels = new byte[LutSize * Palettes.Length * 4];

        for (int row = 0; row < Palettes.Length; row++)
        {
            (float[] a, float[] b, float[] c, float[] d) = Palettes[row];
            int rowOffset = row * LutSize * 4;

            for (int i = 0; i < LutSize; i++)
            {
                float t = i / (float)(LutSize - 1);
                for (int ch = 0; ch < 3; ch++)
                {
                    float v = a[ch] + (b[ch] * MathF.Cos(2f * MathF.PI * ((c[ch] * t) + d[ch])));
                    pixels[rowOffset + (i * 4) + ch] = (byte)(Math.Clamp(v, 0f, 1f) * 255f);
                }
                pixels[rowOffset + (i * 4) + 3] = 255;
            }
        }

        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        fixed (byte* p = pixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                LutSize,
                (uint)Palettes.Length,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                p);
        }
    }

    public void Bind(TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(Handle);
    }
}
