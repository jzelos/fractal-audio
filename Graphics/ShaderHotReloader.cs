using Silk.NET.OpenGL;

namespace SDL_Demo.Graphics;

/// <summary>
/// Watches the vertex/fragment shader source files on disk and recompiles the program when they
/// change, keeping the last-good <see cref="ShaderProgram"/> bound if recompilation fails.
///
/// GL thread affinity: <see cref="FileSystemWatcher"/> fires its event callbacks on a
/// thread-pool thread. That callback (<see cref="OnFileEvent"/>) must only ever set a "dirty"
/// flag — it must NEVER call into GL. All GL calls, including shader recompilation, happen
/// inside <see cref="Update"/>, which the caller must invoke once per frame on the single render
/// thread. This is the one place in the codebase where that rule could be silently broken, so
/// keep any future changes here consistent with it.
/// </summary>
public sealed class ShaderHotReloader : IDisposable
{
    private const double DebounceSeconds = 0.15;

    private readonly GL _gl;
    private readonly string _vertPath;
    private readonly string _fragPath;
    private readonly FileSystemWatcher _watcher;

    private volatile bool _dirty;
    private DateTime _lastEventUtc = DateTime.MinValue;

    public ShaderProgram Current { get; private set; }

    public ShaderHotReloader(GL gl, string vertPath, string fragPath)
    {
        _gl = gl;
        _vertPath = vertPath;
        _fragPath = fragPath;

        // Initial load is allowed to throw — there is no "last good" program yet to fall back to.
        Current = Compile();

        string directory = Path.GetDirectoryName(Path.GetFullPath(vertPath))
            ?? throw new InvalidOperationException("Shader path has no directory.");

        _watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            Filter = "*.*",
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Thread-pool thread — flag only, never touch GL here.
        _lastEventUtc = DateTime.UtcNow;
        _dirty = true;
    }

    /// <summary>Call once per frame on the render thread.</summary>
    public void Update()
    {
        if (!_dirty)
        {
            return;
        }

        // Debounce: coalesce rapid save events (editors often write + touch metadata separately).
        if ((DateTime.UtcNow - _lastEventUtc).TotalSeconds < DebounceSeconds)
        {
            return;
        }

        _dirty = false;
        TryReload();
    }

    private void TryReload()
    {
        try
        {
            ShaderProgram next = Compile();
            Current.Dispose();
            Current = next;
            Console.WriteLine("[ShaderHotReloader] shader reloaded.");
        }
        catch (ShaderCompileException ex)
        {
            Console.WriteLine($"[ShaderHotReloader] reload failed, keeping last-good program: {ex.Message}");
        }
        catch (IOException)
        {
            // File likely still being written by the editor; retry next frame.
            _dirty = true;
        }
    }

    private ShaderProgram Compile()
    {
        string vertexSource = File.ReadAllText(_vertPath);
        string fragmentSource = File.ReadAllText(_fragPath);
        return ShaderProgram.Create(_gl, vertexSource, fragmentSource);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        Current.Dispose();
    }
}
