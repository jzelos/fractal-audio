using System.Collections.Immutable;

namespace SDL_Demo.Audio;

/// <summary>
/// Immutable snapshot of one analysis tick's audio features. A sealed class (not a struct) —
/// see <see cref="AudioFrameBus"/> for the atomicity rationale: a `volatile` reference field
/// gives a correct single-writer/single-reader swap with zero locking, which a multi-field
/// mutable struct would not guarantee across all its fields.
/// </summary>
public sealed class AudioFrame
{
    public static readonly AudioFrame Empty = new()
    {
        TimestampSeconds = 0,
        Status = CaptureStatus.DeviceUnavailable,
        IsSilent = true,
        Rms = 0f,
        Peak = 0f,
        SpectralCentroid01 = 0f,
        Flux = 0f,
        OnsetDetected = false,
        BeatDetected = false,
        OnsetCount = 0,
        BeatCount = 0,
        TempoLocked = false,
        TempoBpm = 0f,
        BeatPhase = 0f,
        TempoConfidence = 0f,
        RawKickCount = 0,
        BandEnergies = ImmutableArray.Create(0f, 0f, 0f, 0f, 0f, 0f, 0f),
        BandEnergiesRaw = ImmutableArray.Create(0f, 0f, 0f, 0f, 0f, 0f, 0f),
    };

    public required double TimestampSeconds { get; init; }   // capture-side clock, monotonic
    public required CaptureStatus Status { get; init; }
    public required bool IsSilent { get; init; }              // true once silence timeout fires
    public required float Rms { get; init; }
    public required float Peak { get; init; }
    public required float SpectralCentroid01 { get; init; }   // normalized 0..1
    public required float Flux { get; init; }                 // normalized spectral flux
    public required bool OnsetDetected { get; init; }          // one-tick pulse, not sustained
    // One-tick beat pulse: the TempoTracker's phase-locked grid beat while TempoLocked, else the
    // raw bass-band (kick) onset detector as a fallback.
    public required bool BeatDetected { get; init; }

    // Monotonic totals of OnsetDetected/BeatDetected pulses so far. The render thread (~60Hz)
    // doesn't see every analyzer frame (~50Hz) exactly once — it can re-read one or skip one —
    // so consumers should edge-detect on these counters rather than act on the bools directly.
    public required long OnsetCount { get; init; }
    public required long BeatCount { get; init; }

    public required bool TempoLocked { get; init; }   // TempoTracker has a confident tempo + phase
    public required float TempoBpm { get; init; }     // current estimate; 0 before the first one
    public required float BeatPhase { get; init; }    // 0 at a beat, rising toward 1 before the next
    public required float TempoConfidence { get; init; } // normalised autocorrelation at the tempo lag

    // Monotonic count of the raw bass-band detector's kicks, whether or not the tempo is locked —
    // diagnostics only, to compare the grid against what the detector actually hears.
    public required long RawKickCount { get; init; }
    public required ImmutableArray<float> BandEnergies { get; init; }      // 7, post-envelope
    public required ImmutableArray<float> BandEnergiesRaw { get; init; }   // 7, pre-envelope (debug)
}
