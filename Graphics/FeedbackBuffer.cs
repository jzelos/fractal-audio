using Silk.NET.OpenGL;

namespace FractalAudio.Graphics;

/// <summary>
/// Milkdrop-style persistence (IDEAS.md Tier 2 "feedback / persistence buffer"): each frame the
/// fractal renders into an offscreen "fresh" target, then a composite pass warps the previous
/// output (a slight zoom out from the centre plus a rotation), fades it, and keeps the brighter of
/// it and the fresh frame. Bright boundary detail therefore leaves trails over the dark interior
/// as the shape morphs. The composite alternates between two targets (ping-pong), and the result
/// is drawn to the screen. The warp comes from Mapping/AudioToFractalMapper (the one place audio
/// becomes visual parameters).
///
/// The fade is a fixed per-second rate, never audio-driven, and the composite is a max rather
/// than a sum, so the image can't saturate to white. Targets are RGBA16F: with 8-bit channels a
/// repeated multiply by the fade stalls on a residue it can't round below, leaving permanent ghosts.
/// </summary>
public sealed class FeedbackBuffer : IDisposable
{
    // Tune-by-eye; per second so the look doesn't depend on frame rate.
    private const float TrailHalfLifeSeconds = 0.35f;

    private readonly GL _gl;
    private readonly ShaderHotReloader _composite;
    private readonly ShaderHotReloader _present;
    private readonly uint[] _framebuffers = new uint[3]; // 0 = fresh, 1/2 = ping-pong outputs.
    private readonly uint[] _textures = new uint[3];
    private int _output = 1; // the target holding the latest composite.
    private int _width;
    private int _height;

    public FeedbackBuffer(GL gl, string shaderDirectory)
    {
        _gl = gl;
        string vert = Path.Combine(shaderDirectory, "quad.vert");
        _composite = new ShaderHotReloader(gl, vert, Path.Combine(shaderDirectory, "feedback.frag"));
        _present = new ShaderHotReloader(gl, vert, Path.Combine(shaderDirectory, "present.frag"));
    }

    /// <summary>False draws the fresh frame straight to the screen (A/B toggle).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Binds the offscreen target the fractal should draw into this frame, sized to the
    /// window (re-created, and cleared, on resize). Call on the render thread.</summary>
    public void BeginFresh(int width, int height)
    {
        _composite.Update();
        _present.Update();
        EnsureSize(width, height);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[0]);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    /// <summary>Composites the fresh frame over the warped, faded previous output and draws the
    /// result to the default framebuffer. Leaves texture unit 0 bound to the result.</summary>
    public void CompositeAndPresent(FullscreenQuad quad, double dtSeconds, Mapping.FeedbackWarp warp)
    {
        int source = _output;
        if (Enabled)
        {
            int target = _output == 1 ? 2 : 1;
            float dt = (float)Math.Max(dtSeconds, 0.0);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[target]);
            ShaderProgram composite = _composite.Current;
            composite.Use();
            BindTexture(TextureUnit.Texture0, _textures[0]);
            BindTexture(TextureUnit.Texture1, _textures[_output]);
            composite.SetUniform1i("u_fresh", 0);
            composite.SetUniform1i("u_previous", 1);
            composite.SetUniform2f("u_resolution", _width, _height);
            composite.SetUniform1f("u_fade", MathF.Pow(0.5f, dt / TrailHalfLifeSeconds));
            composite.SetUniform1f("u_zoom", 1f + (warp.ZoomPerSecond * dt));
            composite.SetUniform1f("u_rotation", warp.RotationPerSecond * dt);
            quad.Draw();

            _output = target;
            source = target;
        }
        else
        {
            source = 0;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        ShaderProgram present = _present.Current;
        present.Use();
        BindTexture(TextureUnit.Texture0, _textures[source]);
        present.SetUniform1i("u_image", 0);
        quad.Draw();
    }

    private void BindTexture(TextureUnit unit, uint texture)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    private unsafe void EnsureSize(int width, int height)
    {
        if (width == _width && height == _height && _textures[0] != 0)
        {
            return;
        }

        DeleteTargets();
        _width = width;
        _height = height;

        for (int i = 0; i < _textures.Length; i++)
        {
            _textures[i] = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _textures[i]);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexImage2D(
                TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.Float, null);

            _framebuffers[i] = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[i]);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _textures[i], 0);

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException($"Feedback framebuffer {i} incomplete: {status}");
            }

            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void DeleteTargets()
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            if (_framebuffers[i] != 0)
            {
                _gl.DeleteFramebuffer(_framebuffers[i]);
                _framebuffers[i] = 0;
            }
            if (_textures[i] != 0)
            {
                _gl.DeleteTexture(_textures[i]);
                _textures[i] = 0;
            }
        }
    }

    public void Dispose()
    {
        DeleteTargets();
        _composite.Dispose();
        _present.Dispose();
    }
}
