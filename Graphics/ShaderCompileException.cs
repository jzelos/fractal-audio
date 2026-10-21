namespace SDL_Demo.Graphics;

/// <summary>
/// Thrown when a shader fails to compile or a program fails to link. Carries the raw GL info
/// log. Callers (e.g. <see cref="ShaderHotReloader"/>) decide the fallback — this exception never
/// takes the process down by itself.
/// </summary>
public sealed class ShaderCompileException : Exception
{
    public ShaderCompileException(string message) : base(message)
    {
    }
}
