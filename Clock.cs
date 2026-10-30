using System.Diagnostics;

namespace FractalAudio;

/// <summary>
/// Fixed-step accumulator plus measured wall frame time. Presentation (SDL_GL_SwapWindow /
/// SDL_RenderPresent) stays decoupled from the logical update step and is still vsync-gated by
/// the driver — this class only tracks timing, it never blocks or sleeps.
/// </summary>
public sealed class Clock
{
    /// <summary>Logical fixed update step, in seconds. 1/120s per the plan.</summary>
    public const double FixedStepSeconds = 1.0 / 120.0;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _lastTickSeconds;
    private double _accumulator;
    private double _rollingAverageFrameTimeSeconds;

    /// <summary>Total elapsed real (wall-clock) time in seconds since the clock was created.</summary>
    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;

    /// <summary>Wall-clock delta time of the most recent call to <see cref="Tick"/>, in seconds.</summary>
    public double DeltaSeconds { get; private set; }

    /// <summary>Rolling average frame time in seconds (simple exponential moving average).</summary>
    public double RollingAverageFrameTimeSeconds => _rollingAverageFrameTimeSeconds;

    /// <summary>
    /// Call once per iteration of the main loop. Updates <see cref="DeltaSeconds"/> and the
    /// rolling average, and returns the number of fixed steps that should be run this frame.
    /// </summary>
    public int Tick()
    {
        double now = ElapsedSeconds;
        DeltaSeconds = now - _lastTickSeconds;
        _lastTickSeconds = now;

        // Guard against a huge delta (e.g. breakpoint/debugger pause) blowing up the accumulator.
        double clampedDelta = Math.Min(DeltaSeconds, 0.25);

        const double emaAlpha = 0.1;
        _rollingAverageFrameTimeSeconds = _rollingAverageFrameTimeSeconds <= 0
            ? DeltaSeconds
            : (emaAlpha * DeltaSeconds) + ((1 - emaAlpha) * _rollingAverageFrameTimeSeconds);

        _accumulator += clampedDelta;

        int steps = 0;
        while (_accumulator >= FixedStepSeconds)
        {
            _accumulator -= FixedStepSeconds;
            steps++;
        }

        return steps;
    }
}
