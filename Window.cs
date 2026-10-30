using SDL2;

namespace FractalAudio;

/// <summary>
/// Wraps SDL window creation/lifetime. Sets the SDL_GL_* context attributes *before*
/// SDL_CreateWindow (SDL requires this ordering) and creates the window with the
/// SDL_WINDOW_OPENGL flag so a GL context can be created against it. The window is resizable and
/// maximisable; <see cref="Width"/>/<see cref="Height"/> are the GL drawable size in pixels.
/// </summary>
public sealed class Window
{
    public IntPtr Handle { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Window(string title, int width, int height)
    {
        Width = width;
        Height = height;

        SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
        SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
        SDL.SDL_GL_SetAttribute(
            SDL.SDL_GLattr.SDL_GL_CONTEXT_PROFILE_MASK,
            (int)SDL.SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_CORE);
        SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_DOUBLEBUFFER, 1);

        Handle = SDL.SDL_CreateWindow(
            title,
            SDL.SDL_WINDOWPOS_UNDEFINED,
            SDL.SDL_WINDOWPOS_UNDEFINED,
            width,
            height,
            SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL |
            SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE);

        if (Handle == IntPtr.Zero)
        {
            Console.WriteLine($"There was an issue creating the window. {SDL.SDL_GetError()}");
        }
    }

    /// <summary>Re-reads the drawable size; call on SDL_WINDOWEVENT_SIZE_CHANGED (user resize,
    /// maximise/restore, and programmatic changes all raise it). Returns true if it changed.</summary>
    public bool RefreshSize()
    {
        SDL.SDL_GL_GetDrawableSize(Handle, out int width, out int height);
        if (width <= 0 || height <= 0 || (width == Width && height == Height))
        {
            return false;
        }
        Width = width;
        Height = height;
        return true;
    }

    public void Destroy()
    {
        if (Handle != IntPtr.Zero)
        {
            SDL.SDL_DestroyWindow(Handle);
        }
    }
}
