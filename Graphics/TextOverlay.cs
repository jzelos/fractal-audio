using System.Runtime.InteropServices;
using SDL2;
using Silk.NET.OpenGL;

namespace FractalAudio.Graphics;

/// <summary>
/// Draws lines of text in a translucent panel at the top-left of the screen. Each line is
/// rendered by SDL_ttf to a surface, converted to ABGR8888 (byte order R,G,B,A — matches a GL
/// RGBA/UNSIGNED_BYTE upload), alpha-composited on the CPU over the panel background, and
/// uploaded as one texture, drawn 1:1 in pixels on <see cref="FullscreenQuad"/>'s vertices via
/// shaders/overlay.*. Re-uploads only when <see cref="SetLines"/> is given different text.
/// </summary>
public sealed class TextOverlay : IDisposable
{
    public readonly record struct Line(string Text, byte R = 220, byte G = 220, byte B = 220);

    private const int Padding = 8;
    private const int MarginPx = 10;
    private const byte PanelAlpha = 170;

    private readonly GL _gl;
    private readonly IntPtr _font;
    private readonly ShaderProgram _program;
    private readonly uint _texture;
    private readonly int _lineSkip;

    private byte[] _pixels = Array.Empty<byte>();
    private int _width;
    private int _height;
    private string _lastKey = string.Empty;

    private TextOverlay(GL gl, IntPtr font, ShaderProgram program)
    {
        _gl = gl;
        _font = font;
        _program = program;
        _lineSkip = SDL_ttf.TTF_FontLineSkip(font);

        _texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // Nearest: the panel is drawn exactly 1:1 in pixels, so this keeps glyphs crisp.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
    }

    /// <summary>Returns null (and logs why) if the font or overlay shaders can't be loaded — the
    /// overlay is a diagnostic, so its absence must never stop the visualizer.</summary>
    public static TextOverlay? TryCreate(GL gl, string fontPath, int pointSize, string shaderDirectory)
    {
        if (SDL_ttf.TTF_Init() != 0)
        {
            Console.WriteLine($"[TextOverlay] TTF_Init failed, overlay disabled: {SDL.SDL_GetError()}");
            return null;
        }

        IntPtr font = SDL_ttf.TTF_OpenFont(fontPath, pointSize);
        if (font == IntPtr.Zero)
        {
            Console.WriteLine($"[TextOverlay] could not open font '{fontPath}', overlay disabled: {SDL.SDL_GetError()}");
            return null;
        }

        try
        {
            ShaderProgram program = ShaderProgram.Create(
                gl,
                File.ReadAllText(Path.Combine(shaderDirectory, "overlay.vert")),
                File.ReadAllText(Path.Combine(shaderDirectory, "overlay.frag")));
            return new TextOverlay(gl, font, program);
        }
        catch (Exception ex) when (ex is ShaderCompileException or IOException)
        {
            Console.WriteLine($"[TextOverlay] overlay shaders failed, overlay disabled: {ex.Message}");
            SDL_ttf.TTF_CloseFont(font);
            return null;
        }
    }

    public void SetLines(IReadOnlyList<Line> lines)
    {
        string key = string.Join('\n', lines.Select(l => $"{l.R},{l.G},{l.B}|{l.Text}"));
        if (key == _lastKey)
        {
            return;
        }
        _lastKey = key;

        int textWidth = 0;
        foreach (Line line in lines)
        {
            if (line.Text.Length > 0 && SDL_ttf.TTF_SizeUTF8(_font, line.Text, out int w, out _) == 0)
            {
                textWidth = Math.Max(textWidth, w);
            }
        }

        _width = textWidth + (2 * Padding);
        _height = (lines.Count * _lineSkip) + (2 * Padding);
        int size = _width * _height * 4;
        if (_pixels.Length < size)
        {
            _pixels = new byte[size];
        }

        for (int i = 0; i < size; i += 4)
        {
            _pixels[i] = 0;
            _pixels[i + 1] = 0;
            _pixels[i + 2] = 0;
            _pixels[i + 3] = PanelAlpha;
        }

        for (int n = 0; n < lines.Count; n++)
        {
            BlitLine(lines[n], Padding, Padding + (n * _lineSkip));
        }

        Upload();
    }

    private void BlitLine(Line line, int x0, int y0)
    {
        if (line.Text.Length == 0)
        {
            return;
        }

        var color = new SDL.SDL_Color { r = line.R, g = line.G, b = line.B, a = 255 };
        IntPtr rendered = SDL_ttf.TTF_RenderUTF8_Blended(_font, line.Text, color);
        if (rendered == IntPtr.Zero)
        {
            return;
        }

        IntPtr converted = SDL.SDL_ConvertSurfaceFormat(rendered, SDL.SDL_PIXELFORMAT_ABGR8888, 0);
        SDL.SDL_FreeSurface(rendered);
        if (converted == IntPtr.Zero)
        {
            return;
        }

        var surface = Marshal.PtrToStructure<SDL.SDL_Surface>(converted);
        byte[] row = new byte[surface.w * 4];
        int rows = Math.Min(surface.h, _height - y0);
        int cols = Math.Min(surface.w, _width - x0);

        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(surface.pixels + (y * surface.pitch), row, 0, row.Length);
            int dst = (((y0 + y) * _width) + x0) * 4;
            for (int x = 0; x < cols; x++)
            {
                int s = x * 4;
                int d = dst + s;
                float a = row[s + 3] / 255f;
                // Source-over onto the panel: colour blended by glyph coverage, alpha accumulates.
                _pixels[d] = (byte)((row[s] * a) + (_pixels[d] * (1f - a)));
                _pixels[d + 1] = (byte)((row[s + 1] * a) + (_pixels[d + 1] * (1f - a)));
                _pixels[d + 2] = (byte)((row[s + 2] * a) + (_pixels[d + 2] * (1f - a)));
                _pixels[d + 3] = (byte)Math.Max(_pixels[d + 3], row[s + 3]);
            }
        }

        SDL.SDL_FreeSurface(converted);
    }

    private unsafe void Upload()
    {
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        fixed (byte* p = _pixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)_width, (uint)_height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
    }

    /// <summary>Draws the panel over whatever is already in the framebuffer.</summary>
    public void Draw(FullscreenQuad quad, int screenWidth, int screenHeight)
    {
        if (_width == 0 || screenWidth <= 0 || screenHeight <= 0)
        {
            return;
        }

        float x0 = -1f + (2f * MarginPx / screenWidth);
        float y1 = 1f - (2f * MarginPx / screenHeight);
        float x1 = x0 + (2f * _width / screenWidth);
        float y0 = y1 - (2f * _height / screenHeight);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _program.Use();
        _program.SetUniform4f("u_rect", x0, y0, x1, y1);
        _program.SetUniform1i("u_text", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        quad.Draw();

        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_texture);
        _program.Dispose();
        SDL_ttf.TTF_CloseFont(_font);
        SDL_ttf.TTF_Quit();
    }
}
