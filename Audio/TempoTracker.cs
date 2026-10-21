namespace SDL_Demo.Audio;

/// <summary>
/// Tempo estimate + phase-locked beat clock (IDEAS.md Tier 2 "tempo estimate + beat-phase-locked
/// LFOs", pulled forward by the Phase 9 playtests). Fed one onset-strength sample per analyzer
/// tick (see AudioAnalyzer.TempoOnsetStrength); emits a perfectly regular grid beat once locked, instead of
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
/// Prototyped and tuned offline against live dumps of two tracks: "Papua New Guinea" (FSOL),
/// locking at ~61.3 BPM with beats within ±40ms of the accented kick, and "Catch" (Kosheen,
/// known 130 BPM), locking at ~130 BPM. Constants are starting points, not universal.
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
    // Keep the current tempo while its prior-weighted ACF is at least this fraction of the best
    // lag's. Without it, "Catch" (130 BPM) dropped to a syncopated 91 BPM after 30s locked,
    // because that rival out-scored the beat for just 2s in one passage.
    private const float TempoStickiness = 0.8f;

    // Dotted-riff (3:2) handling. "What Time Is Love" (KLF, 120 BPM) has a riff repeating every
    // 1.5 beats that dominates every band (ACF 0.76 at 79 BPM vs 0.46 at 120). Promote: when the
    // best lag has a rival at ~2/3 of it scoring >= DottedRatio x best AND the kick (bass flux)
    // supports the rival at least as well, the rival is the beat. The bass gate matters:
    // "Papua New Guinea" has a 3:2 rival too, but its kick supports the slower period. Protect:
    // an established tempo isn't displaced by its own 1.5x-period riff unless it drops below
    // DottedRatio x the riff's score. Offline on all three dumps: 100% of estimates right.
    private const float DottedRatio = 0.5f;
    private const float MinBassSupport = 0.2f;

    // Half-tempo correction. "Overdrive" (Lazerhawk, 133 BPM) locked at 66.5: with alternating
    // kick/snare the onset signal repeats more exactly every two beats, so autocorrelation
    // prefers the slower octave — and fold sharpness can't tell either, since folding at twice
    // the period just gives two equal peaks with the same peak/mean. The kick fold's *shape*
    // can: if it has a second peak half a cycle after the main one, nearly as tall, the kick is
    // landing twice per "beat" and the real tempo is double. Measured half-cycle ratios:
    // Overdrive 0.80 (-> doubled, exact 133), Disco 0.17-0.52, Teardrop 0.16-0.29 (unchanged).
    private const float HalfCycleKickRatio = 0.6f;
    private const float MinBassFoldSharpness = 2.0f; // bass must have a clear beat to be trusted.
    private const float PhaseGain = 0.3f;
    private const float MaxPhaseCorrection = 0.05f; // cycles per estimate.
    private const float BigPhaseError = 0.25f;
    private const int PhaseFlipPersist = 4;

    private readonly float _tickRate;
    private readonly float _leadCycles;
    private readonly float[] _history = new float[HistoryTicks];
    private readonly float[] _window = new float[HistoryTicks];
    private readonly float[] _bassHistory = new float[HistoryTicks];
    private readonly float[] _bassWindow = new float[HistoryTicks];
    private readonly float[] _fold = new float[FoldBins];
    private readonly int[] _foldCount = new int[FoldBins];
    private readonly List<float> _pendingPeriods = new();
    private readonly float[] _lagAc = new float[HistoryTicks];
    private readonly float[] _lagScore = new float[HistoryTicks];
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

    /// <summary>Feeds one tick of onset strength (the tempo signal) and bass-band onset strength
    /// (kick evidence, used only to arbitrate 3:2 ambiguity); returns true on a grid beat (only
    /// while locked).</summary>
    public bool Process(float onsetStrength, float bassOnsetStrength)
    {
        _history[_written % HistoryTicks] = onsetStrength;
        _bassHistory[_written % HistoryTicks] = bassOnsetStrength;
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
            _bassWindow[i] = _bassHistory[(start + i) % HistoryTicks];
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
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            float ac = 0f;
            for (int i = 0; i + lag < n; i++)
            {
                ac += (_window[i] - mean) * (_window[i + lag] - mean);
            }
            ac /= energy;
            _lagAc[lag] = ac;

            float octaves = MathF.Log2(60f * _tickRate / lag / PriorBpm) / PriorOctaves;
            _lagScore[lag] = ac * MathF.Exp(-0.5f * octaves * octaves);
            if (_lagScore[lag] > bestScore)
            {
                bestScore = _lagScore[lag];
                bestLag = lag;
            }
        }

        // Promote a kick-supported rival at ~2/3 of the best lag (the beat under a dotted riff).
        bool promoted = false;
        int rivalLag = BestNear(2f * bestLag / 3f, minLag, maxLag);
        if (rivalLag > 0 && _lagScore[rivalLag] >= DottedRatio * _lagScore[bestLag])
        {
            float bassAtRival = BassAutocorrelation(rivalLag, n);
            if (bassAtRival >= MinBassSupport && bassAtRival >= BassAutocorrelation(bestLag, n))
            {
                bestLag = rivalLag;
                promoted = true;
            }
        }

        // Hysteresis: stay on the current tempo unless a rival clearly beats it — and be much
        // stickier against the current beat's own dotted-riff period (1.5x its lag).
        int currentLag = (int)MathF.Round(_period);
        if (!promoted && _period > 0f && currentLag >= minLag && currentLag <= maxLag)
        {
            bool rivalIsDottedRiff = MathF.Abs(((float)bestLag / currentLag) - 1.5f) < 0.06f;
            float keep = rivalIsDottedRiff ? DottedRatio : TempoStickiness;
            if (_lagScore[currentLag] >= keep * _lagScore[bestLag])
            {
                bestLag = currentLag;
            }
        }
        float bestAc = _lagAc[bestLag];

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

        // Half-tempo check on the kick's fold shape (see HalfCycleKickRatio).
        if (estimate / 2f >= minLag && KickLandsOnHalfCycle(estimate))
        {
            estimate /= 2f;
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

    /// <summary>Of round(target) and its two neighbours within [minLag, maxLag], the lag with
    /// the highest autocorrelation; 0 if none is in range.</summary>
    private int BestNear(float target, int minLag, int maxLag)
    {
        int best = 0;
        int centre = (int)MathF.Round(target);
        for (int lag = centre - 1; lag <= centre + 1; lag++)
        {
            if (lag >= minLag && lag <= maxLag && (best == 0 || _lagAc[lag] > _lagAc[best]))
            {
                best = lag;
            }
        }
        return best;
    }

    /// <summary>Normalised autocorrelation of the bass onset signal over the current window.</summary>
    private float BassAutocorrelation(int lag, int n)
    {
        float mean = 0f;
        for (int i = 0; i < n; i++)
        {
            mean += _bassWindow[i];
        }
        mean /= n;

        float energy = 0f;
        float ac = 0f;
        for (int i = 0; i < n; i++)
        {
            float d = _bassWindow[i] - mean;
            energy += d * d;
            if (i + lag < n)
            {
                ac += d * (_bassWindow[i + lag] - mean);
            }
        }
        return energy > 1e-12f ? ac / energy : 0f;
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

    /// <summary>True when the bass (kick) fold at <paramref name="period"/> is clearly beat-like
    /// and has a second peak half a cycle from its main one at least HalfCycleKickRatio as tall
    /// — i.e. the real beat is twice as fast.</summary>
    private bool KickLandsOnHalfCycle(float period)
    {
        float sharpness = FoldSharpness(period, _bassWindow);
        if (sharpness < MinBassFoldSharpness)
        {
            return false;
        }

        int peak = 0;
        for (int k = 1; k < FoldBins; k++)
        {
            if (_fold[k] > _fold[peak])
            {
                peak = k;
            }
        }
        if (_fold[peak] <= 0f)
        {
            return false;
        }

        float half = 0f;
        for (int d = -1; d <= 1; d++)
        {
            half = Math.Max(half, _fold[(peak + (FoldBins / 2) + d + FoldBins) % FoldBins]);
        }
        return half / _fold[peak] >= HalfCycleKickRatio;
    }

    /// <summary>Folds the tempo-signal window at <paramref name="period"/> ticks into
    /// <see cref="_fold"/> and returns peak/mean.</summary>
    private float FoldSharpness(float period) => FoldSharpness(period, _window);

    /// <summary>Folds <paramref name="source"/> (the current window's samples) at
    /// <paramref name="period"/> ticks into <see cref="_fold"/> (bin k = k/FoldBins of a cycle
    /// before the window's end) and returns peak/mean.</summary>
    private float FoldSharpness(float period, float[] source)
    {
        Array.Clear(_fold);
        Array.Clear(_foldCount);
        for (int i = 0; i < _windowLength; i++)
        {
            float age = (_windowLength - 1 - i) % period;
            int k = (int)(age / period * FoldBins) % FoldBins;
            _fold[k] += source[i];
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
