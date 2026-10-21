using Silk.NET.OpenGL;

namespace SDL_Demo.Graphics;

/// <summary>
/// VAO/VBO/EBO for a single screen-filling quad (2 triangles, NDC position + UV). No per-frame
/// allocation — buffers are created once and <see cref="Draw"/> just issues the draw call.
/// </summary>
public sealed class FullscreenQuad : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    // Position (x, y) + UV (u, v) per vertex.
    private static readonly float[] Vertices =
    {
        // pos          uv
        -1f, -1f,       0f, 0f,
         1f, -1f,       1f, 0f,
         1f,  1f,       1f, 1f,
        -1f,  1f,       0f, 1f,
    };

    private static readonly uint[] Indices =
    {
        0, 1, 2,
        2, 3, 0,
    };

    public unsafe FullscreenQuad(GL gl)
    {
        _gl = gl;

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* v = Vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* i = Indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(Indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);
        }

        const uint stride = 4 * sizeof(float);

        // location 0: position (vec2)
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);

        // location 1: uv (vec2)
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.BindVertexArray(0);
    }

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)Indices.Length, DrawElementsType.UnsignedInt, (void*)0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
