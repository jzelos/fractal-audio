using FractalAudio.Audio;
using FractalAudio.Graphics;

namespace FractalAudio.Mapping;

/// <summary>Per-second warp applied to the feedback trails (see <see cref="FeedbackBuffer"/>).</summary>
public readonly record struct FeedbackWarp(float ZoomPerSecond, float RotationPerSecond);

public readonly record struct MapperDiagnostics(
    float Cx, float Cy, int Waypoint, int WaypointCount, float Zoom, float FitZoom,
    int PaletteFrom, int PaletteTo, float PaletteBlend);

/// <summary>
/// The one and only place that reads <see cref="AudioFrame"/> and writes the fractal shader's
/// uniforms + palette state. This is a deliberate design guard (see PLAN.md "Palette +
/// continuous/discrete mapping rule"): no other code in the codebase should call
/// <see cref="ShaderProgram.SetUniform1f"/>/etc. on the fractal program's audio-driven uniforms —
/// keeping every such write funneled through here is what makes the continuous-vs-discrete split
/// enforceable/verifiable in one place.
///
/// Continuous (every frame, smooth): bass+band perturbation of `c` around the current waypoint,
/// palette phase rotation driven by RMS, and bass-driven zoom "breathing" below a fit zoom that
/// keeps the silhouette inside a fixed screen border (see <see cref="JuliaBounds"/>). Also the
/// feedback trails' warp (<see cref="Warp"/>): they push outward with the beat pulse and swirl
/// with the low-mid vs presence tilt. The trails' fade is deliberately not audio-driven.
/// Discrete (edge-detected on the frame's monotonic counters, never on the one-tick bools — see
/// <see cref="AudioFrame.OnsetCount"/>):
///   - beat (the tempo tracker's phase-locked grid beat, or the raw bass-only onset detector
///     while unlocked): fires a decaying zoom punch, and — every N beats / a minimum hold —
///     glides to the next curated Julia-`c` waypoint along a loop of neighbouring points;
///   - palette advance, crossfaded, never snapped: every M grid beats while the tempo is locked
///     (always together with a shape change), else on a general onset after a minimum hold.
///
/// Julia-`c` mapping: a curated-waypoint approach (see PLAN.md "Julia mapping") rather than
/// IDEAS.md's literal unconstrained radius/angle mapping — bounds `c` to a perturbation radius
/// around hand-picked, known-interesting points near the Mandelbrot boundary, avoiding the
/// dead-zone risk (filled-blob interior / empty-disc exterior) that a raw audio-driven
/// radius/angle would risk wandering into.
/// </summary>
public sealed class AudioToFractalMapper
{
    // Curated Julia constants near the Mandelbrot boundary, ordered as a loop around it (upper
    // half, then the mirrored lower half — conj(c) gives the mirror-image set) so consecutive
    // waypoints are neighbours. The original unordered list made most glides cut straight across
    // the main cardioid, where every midpoint is a fat solid blob: each beat inflated and
    // deflated the silhouette (verified on a rendered contact sheet, playtest 3).
    private static readonly (float X, float Y)[] Waypoints =
    {
        (0.285f, 0.01f),
        (0.36f, 0.216f),
        (0.235f, 0.485f),
        (-0.1f, 0.651f),
        (-0.4f, 0.6f),
        (-0.74543f, 0.11301f),
        (-0.8f, 0.156f),
        (-0.835f, -0.2321f),
        (-0.74543f, -0.11301f),
        (-0.70176f, -0.3842f),
        (-0.4f, -0.6f),
        (-0.1f, -0.651f),
        (0.235f, -0.485f),
        (0.36f, -0.216f),
    };

    // Phase 9 starting values (PLAN.md) — all tune-by-eye, not derived.
    private const float PerturbRadius = 0.12f;         // continuous `c` perturbation bound (was 0.05 — too small to read as shape change).
    private const float GlideSeconds = 0.9f;           // beat-triggered waypoint glide (locked: rounded to whole beats).
    private const float MinWaypointHoldSeconds = 1.5f; // beats inside this window punch the zoom but don't change shape.
    private const float PaletteRotationSpeed = 0.6f;   // palette-phase units/sec at Rms == 1.
    private const float PaletteFadeSeconds = 1.5f;     // palette crossfade duration (locked: rounded to whole beats).
    private const float MinPaletteHoldSeconds = 6.0f;  // minimum time between palette changes (locked: rounded up to a power-of-two beat count).
    // Zoom is expressed relative to the "fit" zoom that puts the silhouette exactly at the
    // border: zoom = fit * (rest + depth*bass + punch*beatPulse), and rest + depth + punch == 1,
    // so even a loud bass hit plus a beat punch only reaches the border, never past it.
    private const float BorderFraction = 0.1f;         // kept clear on every side, as a fraction of the half-screen.
    private const float ZoomRestScale = 0.75f;
    private const float ZoomBreathDepth = 0.2f;
    private const float ZoomBeatPunch = 0.05f;
    private const float BeatPulseAttackSeconds = 0.04f; // pulse rises over a few frames rather than a one-frame step.
    private const float BeatPulseDecaySeconds = 0.25f;  // e-folding time of the per-beat pulse.
    private const float FitZoomOutSeconds = 0.15f;     // shape grew: follow quickly (the border absorbs the lag).
    private const float FitZoomInSeconds = 0.8f;       // shape shrank: ease in, so the frame doesn't jitter.

    // Smoothing on the audio features that steer the shape. The analyzer's envelopes attack in
    // ~10ms and the spectral centroid is raw per-tick, which made `c` and the zoom twitch on
    // every sound (playtest 3: "disjointed, not smooth").
    private const float ShapeSmoothingSeconds = 0.5f;    // centroid / band tilt -> perturbation angle.
    private const float RadiusSmoothingSeconds = 0.2f;   // bass -> perturbation radius.
    private const float ZoomBassAttackSeconds = 0.08f;   // bass -> zoom breathing.
    private const float ZoomBassReleaseSeconds = 0.5f;
    private const float MinFitZoom = 0.3f;
    private const float MaxFitZoom = 3.0f;
    private const float BandAgcDecaySeconds = 5.0f;    // per-band running-max decay for normalisation.
    private const float BandAgcFloor = 5e-4f;          // stops the AGC amplifying near-silence to full scale.
    private const int MaxIter = 200;
    // Feedback trail warp: a slow drift plus a push on the beat pulse; rotation follows the
    // smoothed band tilt, so its direction changes with the mix.
    private const float TrailZoomRestPerSecond = 0.15f;
    private const float TrailZoomBeatPerSecond = 0.6f;
    private const float TrailRotationPerSecond = 0.4f; // radians/s at full tilt.

    // Phase 8 robustness clamps/rate-limits (see PLAN.md "Float precision / clamping" and
    // "Robustness pass") — these guard against float32 cracking at extreme zoom, runaway
    // frame-time cost from an unbounded iteration count, and visual strobing from a transient
    // driving a continuous uniform too far in a single frame.
    private const float MinZoom = 0.1f;
    private const float MaxZoom = 1.0e4f;
    private const int MinMaxIter = 100;
    private const int MaxMaxIterClamp = 500;
    private const float MaxCDeltaPerSecond = 2.0f;        // cap on |d(perturbation)/dt| — the waypoint glide is not capped.
    private const float MaxPaletteOffsetDeltaPerSecond = 2.0f; // cap on |d(paletteOffset)/dt|.

    private readonly float[] _bandMax;

    private long _lastOnsetCount;
    private long _lastBeatCount;

    private int _targetWaypointIndex;
    private (float X, float Y) _glideFromC;
    private (float X, float Y) _baseC;
    private double _glideStartSeconds = -1;
    private float _glideSeconds = GlideSeconds;
    private float _perturbX;
    private float _perturbY;

    private double _waypointChangeSeconds = -MinWaypointHoldSeconds;
    private bool _wasLocked;
    private long _gridBeats; // grid beats since the tempo last locked.
    private float _beatPulse;
    private float _beatPulseTarget;
    private AudioFrame? _phaseFrame;
    private double _phaseFrameSeenSeconds;
    private float _fitZoom = 1f;

    private float _smoothCentroid;
    private float _smoothTilt;
    private float _smoothRadiusBass;
    private float _smoothZoomBass;

    private int _paletteFrom;
    private int _paletteTo;
    private double _paletteChangeSeconds = -MinPaletteHoldSeconds;
    private float _paletteFadeSeconds = PaletteFadeSeconds;
    private float _paletteOffset;

    public AudioToFractalMapper(int paletteCount)
    {
        PaletteCount = paletteCount;
        _baseC = Waypoints[0];
        _glideFromC = Waypoints[0];
        _bandMax = new float[AudioFrame.Empty.BandEnergies.Length];
        Array.Fill(_bandMax, BandAgcFloor);
    }

    public int PaletteCount { get; }

    /// <summary>Read-only snapshot of what the last <see cref="Apply"/> wrote, for the on-screen
    /// diagnostics overlay. Reading it never affects the mapping.</summary>
    public MapperDiagnostics Diagnostics { get; private set; }

    /// <summary>The feedback trails' warp for this frame, from the last <see cref="Apply"/>.</summary>
    public FeedbackWarp Warp { get; private set; } = new(TrailZoomRestPerSecond, 0f);

    /// <summary>AGC-normalised band levels (0..1) from the last <see cref="Apply"/>.</summary>
    public ReadOnlySpan<float> NormalizedBands => _normalizedBands;

    private readonly float[] _normalizedBands = new float[AudioFrame.Empty.BandEnergies.Length];

    /// <summary>Reads the latest audio frame and writes every fractal uniform for this render
    /// frame. Call exactly once per frame, on the render thread.</summary>
    public void Apply(ShaderProgram program, AudioFrame frame, double nowSeconds, double dtSeconds, int widthPx, int heightPx)
    {
        float dtF = Math.Max((float)dtSeconds, 0f);

        // --- Discrete: edge-detect on counters, so a frame re-read on two render frames can't
        // fire twice and a frame skipped between two render frames can't be lost. ---
        bool beat = frame.BeatCount != _lastBeatCount;
        bool onset = frame.OnsetCount != _lastOnsetCount;
        _lastBeatCount = frame.BeatCount;
        _lastOnsetCount = frame.OnsetCount;

        bool locked = frame.TempoLocked && frame.TempoBpm > 0f;
        if (locked != _wasLocked)
        {
            _wasLocked = locked;
            _gridBeats = 0;
        }

        if (beat)
        {
            _beatPulseTarget = 1f;

            // Locked to a tempo: count grid beats and change shape every N of them and the
            // palette every M (each the smallest power of two spanning its hold time), so both
            // fall on the musical grid — and since M is a multiple of N, every palette change
            // lands together with a shape change. Unlocked (raw kick detector fallback): the
            // shape uses a plain time hold and the palette follows onsets, below.
            if (locked)
            {
                _gridBeats++;
                int beatsPerWaypoint = BeatsSpanning(frame.TempoBpm, MinWaypointHoldSeconds);
                if (_gridBeats % beatsPerWaypoint == 0)
                {
                    // Glide for a whole number of beats nearest GlideSeconds, so the morph
                    // settles on a beat (and always before the next change).
                    float beatSeconds = 60f / frame.TempoBpm;
                    int glideBeats = Math.Clamp((int)MathF.Round(GlideSeconds / beatSeconds), 1, beatsPerWaypoint);
                    AdvanceWaypoint(nowSeconds, glideBeats * beatSeconds);
                }
                // The fade check only matters when the tempo has just locked after an
                // onset-driven change: never cut a crossfade short.
                int beatsPerPalette = BeatsSpanning(frame.TempoBpm, MinPaletteHoldSeconds);
                if (_gridBeats % beatsPerPalette == 0
                    && nowSeconds - _paletteChangeSeconds >= _paletteFadeSeconds)
                {
                    // Like the glide: fade for a whole number of beats, so it ends on a beat.
                    float beatSeconds = 60f / frame.TempoBpm;
                    int fadeBeats = Math.Clamp((int)MathF.Round(PaletteFadeSeconds / beatSeconds), 1, beatsPerPalette);
                    AdvancePalette(nowSeconds, fadeBeats * beatSeconds);
                }
            }
            else if (nowSeconds - _waypointChangeSeconds >= MinWaypointHoldSeconds)
            {
                AdvanceWaypoint(nowSeconds, GlideSeconds);
            }
        }

        if (!locked && onset && nowSeconds - _paletteChangeSeconds >= MinPaletteHoldSeconds)
        {
            AdvancePalette(nowSeconds, PaletteFadeSeconds);
        }

        // --- Normalise band envelopes (raw FFT magnitudes sit far below 1) with a decaying
        // per-band running max, so "bass energy" actually spans ~0..1. ---
        float agcDecay = MathF.Exp(-dtF / BandAgcDecaySeconds);
        Span<float> bands = stackalloc float[_bandMax.Length];
        for (int b = 0; b < bands.Length; b++)
        {
            float e = b < frame.BandEnergies.Length ? frame.BandEnergies[b] : 0f;
            _bandMax[b] = Math.Max(Math.Max(e, _bandMax[b] * agcDecay), BandAgcFloor);
            bands[b] = Math.Clamp(e / _bandMax[b], 0f, 1f);
        }
        float bass = (bands[0] + bands[1]) * 0.5f;

        _smoothCentroid = Smooth(_smoothCentroid, frame.SpectralCentroid01, ShapeSmoothingSeconds, dtF);
        _smoothTilt = Smooth(_smoothTilt, bands[2] - bands[5], ShapeSmoothingSeconds, dtF);
        _smoothRadiusBass = Smooth(_smoothRadiusBass, bass, RadiusSmoothingSeconds, dtF);
        _smoothZoomBass = Smooth(
            _smoothZoomBass, bass, bass > _smoothZoomBass ? ZoomBassAttackSeconds : ZoomBassReleaseSeconds, dtF);

        // --- Glide the base waypoint toward the target. Smoothstep (zero velocity at both ends)
        // rather than ease-out: the beat itself is marked by the zoom pulse, so the shape can
        // morph without a kink at the start. ---
        (float targetBaseX, float targetBaseY) = Waypoints[_targetWaypointIndex];
        float glideT = _glideStartSeconds < 0
            ? 1f
            : (float)Math.Clamp((nowSeconds - _glideStartSeconds) / _glideSeconds, 0.0, 1.0);
        float easedT = SmoothStep(glideT);
        _baseC = (Lerp(_glideFromC.X, targetBaseX, easedT), Lerp(_glideFromC.Y, targetBaseY, easedT));

        // --- Continuous: bass -> perturbation radius; centroid plus a low-mid vs presence tilt
        // -> angle, so the boundary morphs with the mix and not only with brightness. All three
        // inputs are smoothed above. ---
        float angle = (_smoothCentroid * MathF.PI * 2f) + (MathF.PI * _smoothTilt);
        float radius = _smoothRadiusBass * PerturbRadius;

        // Rate-limit only the perturbation (not the beat glide) so a loud transient can't jerk
        // `c` within a single frame.
        float maxStep = MaxCDeltaPerSecond * dtF;
        _perturbX = StepTowards(_perturbX, radius * MathF.Cos(angle), maxStep);
        _perturbY = StepTowards(_perturbY, radius * MathF.Sin(angle), maxStep);
        float cx = _baseC.X + _perturbX;
        float cy = _baseC.Y + _perturbY;

        // --- Continuous: palette phase rotation driven by RMS, rate-limited. Discrete palette
        // changes only ever show up through the crossfade blend below. ---
        float paletteDelta = Math.Clamp(
            frame.Rms * PaletteRotationSpeed * dtF,
            -MaxPaletteOffsetDeltaPerSecond * dtF,
            MaxPaletteOffsetDeltaPerSecond * dtF);
        _paletteOffset += paletteDelta;
        _paletteOffset -= MathF.Floor(_paletteOffset); // wrap to [0,1)
        float paletteBlend = SmoothStep((float)Math.Clamp((nowSeconds - _paletteChangeSeconds) / _paletteFadeSeconds, 0.0, 1.0));

        // --- Fit: the zoom at which the silhouette's extent for the current `c` touches the
        // border (fractal.frag maps screen to y in [-1,1], x in [-aspect,aspect]). The set is
        // symmetric about the origin, so the center stays at (0,0). Dust sets with nothing
        // bounded keep the previous fit. ---
        if (JuliaBounds.Measure(cx, cy) is (float extentX, float extentY))
        {
            float aspect = widthPx / (float)Math.Max(heightPx, 1);
            float usable = 1f - BorderFraction;
            float targetFit = Math.Clamp(
                Math.Min(aspect * usable / extentX, usable / extentY), MinFitZoom, MaxFitZoom);
            float tau = targetFit < _fitZoom ? FitZoomOutSeconds : FitZoomInSeconds;
            _fitZoom = Lerp(_fitZoom, targetFit, 1f - MathF.Exp(-dtF / tau));
        }

        // --- Zoom breathes with bass (plus the decaying beat punch) below the fit zoom —
        // bounded, so the silhouette grows toward the border on loud passages but never past it. ---
        // The pulse chases a target that a beat sets to 1 and that then decays, so it rises over
        // a few frames instead of stepping, and never exceeds 1.
        // Locked, the target is instead a function of the tracker's beat phase (a beat-locked
        // LFO): free of the analyzer/render frame jitter on the beat event, and it starts rising
        // one attack time before the beat, so the punch peaks on the beat rather than after it.
        _beatPulseTarget *= MathF.Exp(-dtF / BeatPulseDecaySeconds);
        float pulseTarget = locked
            ? PhaseLockedPulse(frame, nowSeconds)
            : _beatPulseTarget;
        _beatPulse = Smooth(_beatPulse, pulseTarget, BeatPulseAttackSeconds, dtF);
        float zoom = Math.Clamp(
            _fitZoom * (ZoomRestScale + (ZoomBreathDepth * _smoothZoomBass) + (ZoomBeatPunch * _beatPulse)), MinZoom, MaxZoom);

        int maxIter = Math.Clamp(MaxIter, MinMaxIter, MaxMaxIterClamp);

        program.SetUniform2f("u_resolution", widthPx, heightPx);
        program.SetUniform2f("u_c", cx, cy);
        program.SetUniform1f("u_zoom", zoom);
        program.SetUniform2f("u_center", 0f, 0f);
        program.SetUniform1i("u_maxIter", maxIter);
        program.SetUniform1f("u_paletteOffset", _paletteOffset);
        program.SetUniform1i("u_paletteFrom", _paletteFrom);
        program.SetUniform1i("u_paletteTo", _paletteTo);
        program.SetUniform1f("u_paletteBlend", paletteBlend);

        Warp = new FeedbackWarp(
            TrailZoomRestPerSecond + (TrailZoomBeatPerSecond * _beatPulse),
            TrailRotationPerSecond * Math.Clamp(_smoothTilt, -1f, 1f));

        bands.CopyTo(_normalizedBands);
        Diagnostics = new MapperDiagnostics(
            cx, cy, _targetWaypointIndex, Waypoints.Length, zoom, _fitZoom,
            _paletteFrom, _paletteTo, paletteBlend);
    }

    /// <summary>Beat-pulse target from the tempo grid: 1 for the last attack time before a
    /// beat, then decaying. The analyzer publishes ~50 frames/s against ~60 render frames/s, so
    /// the phase is extrapolated from when this frame was first seen.</summary>
    private float PhaseLockedPulse(AudioFrame frame, double nowSeconds)
    {
        if (!ReferenceEquals(frame, _phaseFrame))
        {
            _phaseFrame = frame;
            _phaseFrameSeenSeconds = nowSeconds;
        }

        float beatSeconds = 60f / frame.TempoBpm;
        // Phase 0 here is where the grid beat fires (BeatLeadSeconds before the tracker's beat).
        float phase = frame.BeatPhase
            + ((float)(nowSeconds - _phaseFrameSeenSeconds) / beatSeconds)
            + (BeatPipeline.BeatLeadSeconds / beatSeconds);
        phase -= MathF.Floor(phase);

        float sinceBeat = phase * beatSeconds;
        float untilBeat = beatSeconds - sinceBeat;
        return untilBeat <= BeatPulseAttackSeconds ? 1f : MathF.Exp(-sinceBeat / BeatPulseDecaySeconds);
    }

    private void AdvanceWaypoint(double nowSeconds, float glideSeconds)
    {
        _glideSeconds = glideSeconds;
        _glideFromC = _baseC;
        _targetWaypointIndex = (_targetWaypointIndex + 1) % Waypoints.Length;
        _glideStartSeconds = nowSeconds;
        _waypointChangeSeconds = nowSeconds;
    }

    private void AdvancePalette(double nowSeconds, float fadeSeconds)
    {
        _paletteFadeSeconds = fadeSeconds;
        _paletteFrom = _paletteTo;
        _paletteTo = (_paletteTo + 1) % PaletteCount;
        _paletteChangeSeconds = nowSeconds;
    }

    /// <summary>The smallest power-of-two number of beats at <paramref name="bpm"/> that lasts
    /// at least <paramref name="seconds"/> (capped at 32).</summary>
    private static int BeatsSpanning(float bpm, float seconds)
    {
        float beatSeconds = 60f / bpm;
        int n = 1;
        while (n < 32 && n * beatSeconds < seconds)
        {
            n *= 2;
        }
        return n;
    }

    private static float SmoothStep(float t) => t * t * (3f - (2f * t));

    /// <summary>Frame-rate-independent one-pole low-pass: moves <paramref name="current"/>
    /// toward <paramref name="target"/> with time constant <paramref name="tauSeconds"/>.</summary>
    private static float Smooth(float current, float target, float tauSeconds, float dtSeconds) =>
        Lerp(current, target, 1f - MathF.Exp(-dtSeconds / tauSeconds));

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most
    /// <paramref name="maxStep"/> — the per-frame rate limit for continuous uniform deltas.</summary>
    private static float StepTowards(float current, float target, float maxStep)
    {
        float delta = target - current;
        if (MathF.Abs(delta) <= maxStep || maxStep <= 0f)
        {
            return maxStep <= 0f ? current : target;
        }
        return current + (MathF.Sign(delta) * maxStep);
    }
}
