using Silk.NET.OpenGL;

namespace SDL_Demo.Graphics;

/// <summary>
/// Compiles/links a vertex+fragment shader pair into a GL program. Throws
/// <see cref="ShaderCompileException"/> with the GL info log on any compile/link failure —
/// it never leaves a partially-created program bound or silently swallows the error.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }

    private ShaderProgram(GL gl, uint handle)
    {
        _gl = gl;
        Handle = handle;
    }

    public static ShaderProgram Create(GL gl, string vertexSource, string fragmentSource)
    {
        uint vertexShader = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        uint fragmentShader = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkStatus);

        // Shaders are no longer needed standalone once linked into the program.
        gl.DetachShader(program, vertexShader);
        gl.DetachShader(program, fragmentShader);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        if (linkStatus == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new ShaderCompileException($"Program link failed: {log}");
        }

        return new ShaderProgram(gl, program);
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);
        if (compileStatus == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new ShaderCompileException($"{type} compile failed: {log}");
        }

        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    public void SetUniform1f(string name, float value)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    public void SetUniform1i(string name, int value)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    public void SetUniform2f(string name, float x, float y)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform2(location, x, y);
        }
    }

    public void SetUniform3f(string name, float x, float y, float z)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform3(location, x, y, z);
        }
    }

    public void SetUniform4f(string name, float x, float y, float z, float w)
    {
        int location = _gl.GetUniformLocation(Handle, name);
        if (location >= 0)
        {
            _gl.Uniform4(location, x, y, z, w);
        }
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            _gl.DeleteProgram(Handle);
            Handle = 0;
        }
    }
}
