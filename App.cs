using SDL2;
using SDL_Demo.Audio;
using SDL_Demo.Graphics;
using SDL_Demo.Mapping;
using Silk.NET.OpenGL;

namespace SDL_Demo;

/// <summary>
/// Owns the SDL window + GL context lifetime and the main loop (poll events, fixed-step update
/// via Clock, draw, swap). Rendering goes through a loaded Silk.NET GL instance against the
/// SDL_GL context, drawing a fullscreen quad with the audio-reactive fractal shader. Teardown is
/// orderly: GL resources, WasapiLoopbackCapture, SDL_GL_DeleteContext, SDL_DestroyWindow,
/// SDL_Quit (see <see cref="Teardown"/>).
/// </summary>
public sealed class App
{
    private readonly Clock _clock = new();
    private Window? _window;
    private IntPtr _glContext;
    private GL? _gl;
    private ShaderHotReloader? _shaderReloader;
    private FullscreenQuad? _quad;
    private PaletteTexture? _palette;
    private AudioCapture? _audioCapture;
    private AudioAnalyzer? _audioAnalyzer;
    private AudioFrameBus? _audioFrameBus;
    private AudioToFractalMapper? _mapper;
    private bool _running = true;
    private double _lastFrameTimeLogSeconds;

    private static readonly string ShaderDirectory = Path.Combine(AppContext.BaseDirectory, "shaders");

    public int Run()
    {
        SDL.SDL_SetHint(SDL.SDL_HINT_WINDOWS_DISABLE_THREAD_NAMING, "1");

        if (SDL.SDL_Init(SDL.SDL_INIT_VIDEO) < 0)
        {
            Console.WriteLine($"There was an issue initilizing SDL. {SDL.SDL_GetError()}");
        }

        _window = new Window("SDL .NET 8 Tutorial", 640, 480);

        _glContext = SDL.SDL_GL_CreateContext(_window.Handle);
        if (_glContext == IntPtr.Zero)
        {
            Console.WriteLine($"There was an issue creating the GL context. {SDL.SDL_GetError()}");
        }

        SDL.SDL_GL_MakeCurrent(_window.Handle, _glContext);

        // vsync: 1 = standard vsync, matches the old SDL_RENDERER_PRESENTVSYNC behavior.
        SDL.SDL_GL_SetSwapInterval(1);

        _gl = GlLoader.LoadFromSdl();

        string version = _gl.GetStringS(StringName.Version);
        Console.WriteLine($"[GL] GL_VERSION: {version}");

        if (SDL_image.IMG_Init(SDL_image.IMG_InitFlags.IMG_INIT_PNG) == 0)
        {
            Console.WriteLine($"There was an issue initilizing SDL2_Image {SDL_image.IMG_GetError()}");
        }

        _quad = new FullscreenQuad(_gl);
        _palette = new PaletteTexture(_gl);

        _shaderReloader = new ShaderHotReloader(
            _gl,
            Path.Combine(ShaderDirectory, "quad.vert"),
            Path.Combine(ShaderDirectory, "fractal.frag"));

        _gl.Viewport(0, 0, (uint)_window.Width, (uint)_window.Height);

        _audioFrameBus = new AudioFrameBus();
        _audioCapture = new AudioCapture();
        _audioAnalyzer = new AudioAnalyzer(_audioCapture, _audioFrameBus);
        _mapper = new AudioToFractalMapper(_palette.PaletteCount);

        _audioCapture.Start();
        _audioAnalyzer.Start();

        while (_running)
        {
            PollEvents();

            int steps = _clock.Tick();
            for (int i = 0; i < steps; i++)
            {
                Update(Clock.FixedStepSeconds);
            }

            Draw();

            MaybeLogFrameTime();
        }

        Teardown();
        return 0;
    }

    private void PollEvents()
    {
        while (SDL.SDL_PollEvent(out SDL.SDL_Event e) == 1)
        {
            switch (e.type)
            {
                case SDL.SDL_EventType.SDL_QUIT:
                    _running = false;
                    break;

                case SDL.SDL_EventType.SDL_WINDOWEVENT:
                    if (e.window.windowEvent == SDL.SDL_WindowEventID.SDL_WINDOWEVENT_RESIZED)
                    {
                        _window?.OnResized(e.window.data1, e.window.data2);
                        if (_window != null && _gl != null)
                        {
                            _gl.Viewport(0, 0, (uint)_window.Width, (uint)_window.Height);
                        }
                    }
                    break;
            }
        }
    }

    private void Update(double fixedDeltaSeconds)
    {
        // No simulation state yet in Phase 2 — placeholder for later phases.
    }

    private double _lastDrawSeconds;

    private void Draw()
    {
        if (_gl == null || _shaderReloader == null || _quad == null || _window == null ||
            _palette == null || _mapper == null || _audioFrameBus == null)
        {
            return;
        }

        // Hot-reload check happens here, once per frame, on the render thread — see the GL
        // thread-affinity note in ShaderHotReloader.
        _shaderReloader.Update();

        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        ShaderProgram program = _shaderReloader.Current;
        program.Use();

        double now = _clock.ElapsedSeconds;
        double dt = _lastDrawSeconds <= 0 ? 0 : now - _lastDrawSeconds;
        _lastDrawSeconds = now;

        // Mapping/AudioToFractalMapper is the only place that writes fractal uniforms/palette
        // state — see the class doc comment for why that's a deliberate design guard.
        AudioFrame frame = _audioFrameBus.Latest; // never null, lock-free read.
        _mapper.Apply(program, frame, now, dt, _window.Width, _window.Height);

        // Texture-unit wiring only (always unit 0, never audio-driven) — not part of the
        // continuous/discrete mapping rule the Mapper owns above.
        _palette.Bind(TextureUnit.Texture0);
        program.SetUniform1i("u_palette", 0);

        _quad.Draw();

        SDL.SDL_GL_SwapWindow(_window.Handle);
    }

    private void MaybeLogFrameTime()
    {
        if (_clock.ElapsedSeconds - _lastFrameTimeLogSeconds >= 1.0)
        {
            _lastFrameTimeLogSeconds = _clock.ElapsedSeconds;
            Console.WriteLine($"[Clock] avg frame time: {_clock.RollingAverageFrameTimeSeconds * 1000.0:F2} ms");
        }
    }

    private void Teardown()
    {
        // Order matters (Phase 8 robustness requirement): GL resources first (still has a valid
        // current context to free them against), then stop/dispose audio capture, then tear
        // down the GL context/window/SDL itself last.
        _quad?.Dispose();
        _palette?.Dispose();
        _shaderReloader?.Dispose();

        _audioAnalyzer?.Dispose();
        _audioCapture?.Dispose();

        if (_glContext != IntPtr.Zero)
        {
            SDL.SDL_GL_DeleteContext(_glContext);
        }

        _window?.Destroy();
        SDL.SDL_Quit();
    }
}
