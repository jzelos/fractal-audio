namespace SDL_Demo.Audio;

/// <summary>
/// Tempo estimate + phase-locked beat clock (IDEAS.md Tier 2 "tempo estimate + beat-phase-locked
/// LFOs", pulled forward by the Phase 9 playtests). Fed one onset-strength sample per analyzer
/// tick (bass-band spectral flux); emits a perfectly regular grid beat once locked, instead of
/// the raw detector's per-hit pulses, which fire on off-beat bass hits and jitter by ±20%.
///
/// Every <see cref="EstimateEveryTicks"/> ticks, over the last <see cref="HistoryTicks"/>:
///   1. coarse period: autocorrelation over 50..200 BPM lags with a mild log-tempo prior;
///   2. refine: fractional period in ±4% of that lag maximising the folded profile's peak/mean
///      (integer lags alone are ~2% off at slow tempos — enough to make the phase drift);
///   3. confidence: normalised autocorrelation at the coarse lag, with lock/unlock hysteresis;
///   4. phase: the folded profile's peak says where in the cycle the beat sits; a clamped PLL
///      nudges the oscillator toward it. Music often has two near-equal hits per cycle, so errors
///      of more than a quarter cycle are ignored unless they persist.
/// A different tempo (including an octave error) is only adopted once it persists too.
///
/// Prototyped and tuned offline against a live dump of "Papua New Guinea" (FSOL): locks within
/// the first window at ~61.3 BPM, grid beats within ±40ms of the accented kick. Constants are
/// starting points from that one track, not universal.
/// </summary>
public sealed class TempoTracker
{
    private const int HistoryTicks = 400;          // 8s at 50Hz.
    private const int MinHistoryTicks = 200;       // first estimate after 4s, on a partial window,
                                                   // to shorten the unlocked (raw-detector) startup.
    private const int EstimateEveryTicks = 25;     // re-estimate every 0.5s.
    private const float MinBpm = 50f;
    private const float MaxBpm = 200f;
    private const float PriorBpm = 100f;           // centre of the log-tempo prior...
    private const float PriorOctaves = 1.5f;       // ...and its width; deliberately weak.
    private const float LockConfidence = 0.20f;
    private const float UnlockConfidence = 0.10f;
    private const int UnlockAfterEstimates = 2;
    private const int FoldBins = 24;
    private const int RefineSteps = 33;            // period candidates across [0.96, 1.04] x lag.
    private const float PeriodSmoothing = 0.3f;
    private const float TempoChangeTolerance = 0.06f;
    private const int TempoChangePersist = 4;      // 2s of agreeing estimates before switching tempo.
    private const float PhaseGain = 0.3f;
    private const float MaxPhaseCorrection = 0.05f; // cycles per estimate.
    private const float BigPhaseError = 0.25f;
    private const int PhaseFlipPersist = 4;

    private readonly float _tickRate;
    private readonly float _leadCycles;
    private readonly float[] _history = new float[HistoryTicks];
    private readonly float[] _window = new float[HistoryTicks];
    private readonly float[] _fold = new float[FoldBins];
    private readonly int[] _foldCount = new int[FoldBins];
    private readonly List<float> _pendingPeriods = new();
    private int _written;
    private int _ticksSinceEstimate;
    private int _windowLength;   // valid samples in _window for the current estimate.

    private float _period;       // in ticks; 0 until the first estimate.
    private float _phase;        // cycle fraction elapsed since the last beat.
    private bool _reseedPhase = true;
    private int _lowConfidenceCount;
    private int _phaseFlipCount;
    private float _ticksSinceBeat = float.MaxValue;

    /// <param name="tickRate">Analyzer ticks per second.</param>
    /// <param name="leadSeconds">Fire beats this early, to cover analysis-window and display
    /// latency so the visual lands on the audible kick rather than just after it.</param>
    public TempoTracker(float tickRate, float leadSeconds)
    {
        _tickRate = tickRate;
        _leadCycles = leadSeconds * tickRate;
    }

    public bool Locked { get; private set; }
    public float Bpm => _period > 0 ? 60f * _tickRate / _period : 0f;
    public float Confidence { get; private set; }

    /// <summary>Beat phase in [0,1): 0 at a beat, rising to 1 just before the next.</summary>
    public float Phase => _phase;

    /// <summary>Feeds one tick of onset strength; returns true on a grid beat (only while locked).</summary>
    public bool Process(float onsetStrength)
    {
        _history[_written % HistoryTicks] = onsetStrength;
        _written++;

        if (_written >= MinHistoryTicks && ++_ticksSinceEstimate >= EstimateEveryTicks)
        {
            _ticksSinceEstimate = 0;
            Estimate();
        }

        if (_period <= 0f)
        {
            return false;
        }

        // Advance; fire when the phase crosses the lead point before the next beat. The
        // half-period guard stops a backward PLL correction from producing a double beat.
        float lead = Math.Clamp(_leadCycles / _period, 0f, 0.25f);
        float before = _phase;
        _phase += 1f / _period;
        _ticksSinceBeat++;
        bool crossed = before < 1f - lead && _phase >= 1f - lead;
        if (_phase >= 1f)
        {
            _phase -= 1f;
        }

        if (crossed && Locked && _ticksSinceBeat > 0.5f * _period)
        {
            _ticksSinceBeat = 0;
            return true;
        }
        return false;
    }

    private void Estimate()
    {
        // Unroll the newest n samples of the ring buffer, oldest first.
        int n = Math.Min(_written, HistoryTicks);
        _windowLength = n;
        int start = (_written - n) % HistoryTicks;
        float mean = 0f;
        for (int i = 0; i < n; i++)
        {
            _window[i] = _history[(start + i) % HistoryTicks];
            mean += _window[i];
        }
        mean /= n;

        float energy = 0f;
        for (int i = 0; i < n; i++)
        {
            energy += (_window[i] - mean) * (_window[i] - mean);
        }
        if (energy <= 1e-12f)
        {
            UpdateLock(0f);
            return;
        }

        // 1. Coarse period from autocorrelation with a log-tempo prior.
        int minLag = (int)(60f * _tickRate / MaxBpm);
        int maxLag = (int)(60f * _tickRate / MinBpm);
        int bestLag = minLag;
        float bestScore = float.MinValue;
        float bestAc = 0f;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            float ac = 0f;
            for (int i = 0; i + lag < n; i++)
            {
                ac += (_window[i] - mean) * (_window[i + lag] - mean);
            }
            ac /= energy;

            float octaves = MathF.Log2(60f * _tickRate / lag / PriorBpm) / PriorOctaves;
            float score = ac * MathF.Exp(-0.5f * octaves * octaves);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
                bestAc = ac;
            }
        }

        // 2. Refine to a fractional period by fold sharpness.
        float estimate = bestLag;
        float bestSharpness = float.MinValue;
        for (int s = 0; s < RefineSteps; s++)
        {
            float candidate = bestLag * (0.96f + (0.08f * s / (RefineSteps - 1)));
            float sharpness = FoldSharpness(candidate);
            if (sharpness > bestSharpness)
            {
                bestSharpness = sharpness;
                estimate = candidate;
            }
        }

        if (_period <= 0f)
        {
            _period = estimate;
        }
        else if (MathF.Abs((estimate / _period) - 1f) <= TempoChangeTolerance)
        {
            _period += PeriodSmoothing * (estimate - _period);
            _pendingPeriods.Clear();
        }
        else
        {
            // A different tempo, or an octave error: only switch once it persists.
            _pendingPeriods.RemoveAll(p => MathF.Abs((estimate / p) - 1f) > TempoChangeTolerance);
            _pendingPeriods.Add(estimate);
            if (_pendingPeriods.Count >= TempoChangePersist)
            {
                _period = estimate;
                _pendingPeriods.Clear();
                _reseedPhase = true;
            }
        }

        // 3. Lock hysteresis.
        UpdateLock(bestAc);

        // 4. Phase: the fold's peak bin is how long ago (in cycles) the beat sat, measured from
        // the window's end — i.e. from now.
        FoldSharpness(_period);
        int peakBin = 0;
        for (int k = 1; k < FoldBins; k++)
        {
            if (_fold[k] > _fold[peakBin])
            {
                peakBin = k;
            }
        }
        float measured = (((peakBin + 0.5f) / FoldBins) + (1f / _period)) % 1f;
        float error = measured - _phase;
        error -= MathF.Floor(error + 0.5f); // wrap to [-0.5, 0.5)

        if (_reseedPhase)
        {
            _phase = measured;
            _reseedPhase = false;
            _phaseFlipCount = 0;
        }
        else if (MathF.Abs(error) > BigPhaseError)
        {
            if (++_phaseFlipCount >= PhaseFlipPersist)
            {
                _phase = measured;
                _phaseFlipCount = 0;
            }
        }
        else
        {
            _phaseFlipCount = 0;
            _phase += Math.Clamp(PhaseGain * error, -MaxPhaseCorrection, MaxPhaseCorrection);
            _phase -= MathF.Floor(_phase);
        }
    }

    private void UpdateLock(float confidence)
    {
        Confidence = confidence;
        if (confidence >= LockConfidence)
        {
            Locked = true;
            _lowConfidenceCount = 0;
        }
        else if (confidence < UnlockConfidence && ++_lowConfidenceCount >= UnlockAfterEstimates)
        {
            Locked = false;
            _reseedPhase = true;
        }
    }

    /// <summary>Folds the window at <paramref name="period"/> ticks into <see cref="_fold"/>
    /// (bin k = k/FoldBins of a cycle before the window's end) and returns peak/mean.</summary>
    private float FoldSharpness(float period)
    {
        Array.Clear(_fold);
        Array.Clear(_foldCount);
        for (int i = 0; i < _windowLength; i++)
        {
            float age = (_windowLength - 1 - i) % period;
            int k = (int)(age / period * FoldBins) % FoldBins;
            _fold[k] += _window[i];
            _foldCount[k]++;
        }

        float sum = 0f;
        float max = 0f;
        for (int k = 0; k < FoldBins; k++)
        {
            _fold[k] /= Math.Max(_foldCount[k], 1);
            sum += _fold[k];
            max = Math.Max(max, _fold[k]);
        }
        float avg = sum / FoldBins;
        return avg > 0f ? max / avg : 0f;
    }
}
