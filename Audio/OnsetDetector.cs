namespace SDL_Demo.Audio;

/// <summary>
/// Normalized spectral flux onset/beat detector: half-wave-rectified sum of positive FFT-bin
/// magnitude deltas versus the previous analysis tick, compared against a rolling-mean adaptive
/// threshold. Emits a one-tick-wide pulse, not a sustained level — callers must treat
/// <see cref="Process"/>'s <c>OnsetDetected</c> result as edge-triggered.
///
/// Starting constants (rolling window size, threshold multiplier) are proposed defaults per
/// PLAN.md — expect an iterative by-ear tuning pass, not a from-first-principles derivation.
/// </summary>
public sealed class OnsetDetector
{
    private readonly int _historySize;
    private readonly float _thresholdMultiplier;
    private readonly int _refractoryTicks;
    private readonly Queue<float> _fluxHistory;

    private float[]? _previousMagnitudes;
    private int _ticksSinceOnset = int.MaxValue;

    /// <param name="refractoryTicks">Minimum ticks between two onsets. 0 disables it; the
    /// bass-only beat detector uses it so a sustained bass note's slow swell can't re-trigger on
    /// consecutive ticks.</param>
    public OnsetDetector(int historySize = 43, float thresholdMultiplier = 1.5f, int refractoryTicks = 0)
    {
        _historySize = historySize;
        _thresholdMultiplier = thresholdMultiplier;
        _refractoryTicks = refractoryTicks;
        _fluxHistory = new Queue<float>(historySize);
    }

    /// <summary>
    /// Processes one tick's FFT magnitude spectrum (or a contiguous bin range of it). Returns the
    /// normalized flux (roughly 0..1, saturating above the onset threshold — for shader/display
    /// use, not a physical unit) and whether an onset was detected on this tick.
    /// </summary>
    /// <summary>Un-normalised flux from the last <see cref="Process"/> call — the onset-strength
    /// signal <see cref="TempoTracker"/> correlates over.</summary>
    public float RawFlux { get; private set; }

    public (float NormalizedFlux, bool OnsetDetected) Process(ReadOnlySpan<float> magnitudes)
    {
        float rawFlux = 0f;
        if (_previousMagnitudes != null)
        {
            int n = Math.Min(magnitudes.Length, _previousMagnitudes.Length);
            for (int i = 0; i < n; i++)
            {
                float delta = magnitudes[i] - _previousMagnitudes[i];
                if (delta > 0f)
                {
                    rawFlux += delta;
                }
            }
        }

        RawFlux = rawFlux;

        if (_previousMagnitudes == null || _previousMagnitudes.Length != magnitudes.Length)
        {
            _previousMagnitudes = new float[magnitudes.Length];
        }
        magnitudes.CopyTo(_previousMagnitudes);

        float mean = 0f;
        if (_fluxHistory.Count > 0)
        {
            foreach (float f in _fluxHistory)
            {
                mean += f;
            }
            mean /= _fluxHistory.Count;
        }

        // Wait for a full history window: with only a few ticks the mean is meaningless and
        // nearly everything clears the threshold (the "frantic at startup" playtest report).
        bool onset = _fluxHistory.Count >= _historySize && rawFlux > (mean * _thresholdMultiplier) && rawFlux > 1e-6f
            && _ticksSinceOnset >= _refractoryTicks;
        _ticksSinceOnset = onset ? 0 : (_ticksSinceOnset == int.MaxValue ? int.MaxValue : _ticksSinceOnset + 1);

        _fluxHistory.Enqueue(rawFlux);
        while (_fluxHistory.Count > _historySize)
        {
            _fluxHistory.Dequeue();
        }

        float threshold = (mean * _thresholdMultiplier) + 1e-6f;
        float normalizedFlux = Math.Clamp(rawFlux / threshold, 0f, 1f);

        return (normalizedFlux, onset);
    }
}
