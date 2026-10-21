using SDL2;
using Silk.NET.OpenGL;

namespace SDL_Demo.Graphics;

/// <summary>
/// One-shot spike/shim: builds a Silk.NET <see cref="GL"/> instance whose function pointers are
/// resolved via SDL's <see cref="SDL.SDL_GL_GetProcAddress"/>.
///
/// Risk resolution (see PLAN.md "Risks"): Silk.NET.OpenGL 2.23.0's <c>GL.GetApi</c> has a direct
/// <c>Func&lt;string, IntPtr&gt;</c> overload — confirmed via reflection probe against the loaded
/// assembly — so it binds cleanly to SDL's proc-address source with no custom IBindingsContext
/// plumbing needed. The hand-written-DllImport fallback was not required.
/// </summary>
public static class GlLoader
{
    public static GL LoadFromSdl()
    {
        return GL.GetApi(GetProcAddress);
    }

    private static IntPtr GetProcAddress(string proc) => SDL.SDL_GL_GetProcAddress(proc);
}
