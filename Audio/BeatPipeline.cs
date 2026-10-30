namespace FractalAudio.Audio;

/// <summary>
/// Everything between one frame's FFT magnitudes and a beat: the raw bass-band kick detector,
/// the tempo tracker's onset-strength signal, and <see cref="TempoTracker"/> itself. Lives in
/// one class so <see cref="AudioAnalyzer"/> and the tools/TempoReplay harness (which feeds
/// recorded dumps through it to score tempo changes against known BPMs) run identical code.
/// </summary>
public sealed class BeatPipeline
{
    public const float FrameRate = 50f;  // analysis frames per second of audio.

    // Raw kick detector over sub-bass + bass (bands 0..1, 20-150 Hz). Stricter than the general
    // onset detector (playtest 2: bass lines were triggering "beats"): a longer history so a busy
    // bass line raises the baseline, a higher multiplier so only kick-sized jumps clear it, and a
    // longer refractory period.
    private const int KickBandFirst = 0;
    private const int KickBandLast = 1;
    private const int KickHistoryFrames = 75;           // ~1.5s.
    private const float KickThresholdMultiplier = 2.0f;
    private const int KickRefractoryFrames = 20;        // ~400ms, i.e. caps at ~150 BPM.

    // Grid beats fire this early to cover the analysis window plus about a frame of display latency.
    // Public so phase-driven visuals can place the beat where the fired grid beat sits.
    public const float BeatLeadSeconds = 0.05f;

    // Tempo tracker input: full-band log-compressed spectral flux, normalised by its running mean.
    // Bass alone failed on "Catch" (Kosheen, 130 BPM): its sustained bass line has ~no
    // periodicity at the beat. A half-weighted bass term was added for "Papua New Guinea", but
    // that was tuned on the pre-audio-time-framing (aliased) signal; on clean recordings it only
    // hurt — "Diesel Power" (Prodigy, 106 BPM), whose bass peaks at 53/132, read 0% with it and
    // 100% without, with every other test recording unchanged (tools/TempoReplay). The bass
    // flux is still passed to the tracker separately, as kick evidence for its octave/3:2 rules.
    private const float OnsetLogScale = 1000f;          // log1p(scale * magnitude) compression.
    private const float OnsetMeanSeconds = 10f;         // running-mean time constant for normalisation.

    private readonly OnsetDetector _kickDetector = new(KickHistoryFrames, KickThresholdMultiplier, KickRefractoryFrames);
    private float[] _previousLogMagnitudes = Array.Empty<float>();
    private float _fullFluxMean;
    private int _onsetMeanFrames;

    public TempoTracker Tempo { get; } = new(FrameRate, BeatLeadSeconds);

    /// <summary>
    /// Processes one frame. <paramref name="bandStart"/>/<paramref name="bandEnd"/> come from
    /// <see cref="BandLayout.Compute"/>. Returns the beat to act on (the tempo grid's beat while
    /// locked, else the raw kick) and the raw kick itself.
    /// </summary>
    public (bool Beat, bool RawKick) Process(ReadOnlySpan<float> magnitudes, int[] bandStart, int[] bandEnd)
    {
        int kickStart = bandStart[KickBandFirst];
        int kickEnd = bandEnd[KickBandLast];
        (_, bool rawKick) = _kickDetector.Process(magnitudes.Slice(kickStart, kickEnd - kickStart));
        float bassFlux = _kickDetector.RawFlux;

        float onset = OnsetStrength(magnitudes, bandStart[0], bandEnd[BandLayout.BandCount - 1]);
        bool gridBeat = Tempo.Process(onset, bassFlux);
        return (Tempo.Locked ? gridBeat : rawKick, rawKick);
    }

    private float OnsetStrength(ReadOnlySpan<float> magnitudes, int start, int end)
    {
        if (_previousLogMagnitudes.Length < magnitudes.Length)
        {
            _previousLogMagnitudes = new float[magnitudes.Length];
        }

        float fullFlux = 0f;
        for (int i = start; i < end; i++)
        {
            float logMag = MathF.Log(1f + (OnsetLogScale * magnitudes[i]));
            float delta = logMag - _previousLogMagnitudes[i];
            if (delta > 0f)
            {
                fullFlux += delta;
            }
            _previousLogMagnitudes[i] = logMag;
        }

        // Cumulative average until enough frames have passed, then the fixed time constant — so
        // the mean is right from the first frame rather than ramping up from zero.
        _onsetMeanFrames++;
        float alpha = Math.Max(1f / (FrameRate * OnsetMeanSeconds), 1f / _onsetMeanFrames);
        _fullFluxMean += alpha * (fullFlux - _fullFluxMean);

        return fullFlux / Math.Max(_fullFluxMean, 1e-6f);
    }
}
