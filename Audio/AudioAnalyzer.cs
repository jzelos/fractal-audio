using System.Collections.Immutable;
using System.Diagnostics;
using NAudio.Dsp;

namespace SDL_Demo.Audio;

/// <summary>
/// Pulls the most recent mono window from <see cref="AudioCapture"/>'s ring buffer on its own
/// timer tick (decoupled from the capture callback — see PLAN.md's silence-timeout note), runs a
/// 2048-point Hann-windowed FFT via <c>NAudio.Dsp.FastFourierTransform.FFT</c>, bins into 7
/// log-spaced-by-Hz-range bands computed from the capture's *actual* sample rate, computes
/// RMS/peak/spectral centroid, runs a per-band <see cref="EnvelopeFollower"/>, runs
/// <see cref="OnsetDetector"/> twice (full-spectrum for general onsets, 20-150 Hz only for
/// kick-carried beats), and publishes one <see cref="AudioFrame"/> per tick to
/// <see cref="AudioFrameBus"/>.
/// </summary>
public sealed class AudioAnalyzer : IDisposable
{
    private const int FftSize = 2048;       // power of two, per plan.
    private const int FftSizeLog2 = 11;     // log2(2048), required by FastFourierTransform.FFT's `m` parameter.
    private const int BandCount = 7;
    private const double TickIntervalSeconds = 1.0 / 50.0; // analyzer tick rate — decoupled from capture/render.
    private const double AttackMs = 10.0;
    private const double ReleaseMs = 300.0;
    private const double ConsoleLogIntervalSeconds = 0.5; // ~2/sec, per plan's test instructions.

    // Band edges in Hz: sub-bass, bass, low-mid, mid, high-mid, presence, air.
    private static readonly float[] BandEdgesHz = { 20f, 60f, 150f, 400f, 1000f, 2500f, 6000f, 12000f };

    // Beat detector covers sub-bass + bass (bands 0..1, i.e. 20-150 Hz) — the kick carrier.
    private const int BeatBandFirst = 0;
    private const int BeatBandLast = 1;
    // Stricter than the general detector (playtest 2: bass lines were triggering "beats"): a
    // longer history so a busy bass line raises the baseline, a higher multiplier so only
    // kick-sized jumps clear it, and a longer refractory period.
    private const int BeatHistoryTicks = 75;           // ~1.5s at 50Hz.
    private const float BeatThresholdMultiplier = 2.0f;
    private const int BeatRefractoryTicks = 20;        // ~400ms at 50Hz, i.e. caps at ~150 BPM.

    // Grid beats fire this early to cover the analysis window (~20-40ms behind the kick, per the
    // offline prototype) plus about a frame of display latency.
    private const float BeatLeadSeconds = 0.05f;

    private readonly AudioCapture _capture;
    private readonly AudioFrameBus _bus;
    private readonly Stopwatch _clock = new();

    private readonly EnvelopeFollower[] _envelopeFollowers;
    private readonly OnsetDetector _onsetDetector = new();
    private readonly OnsetDetector _beatDetector = new(BeatHistoryTicks, BeatThresholdMultiplier, BeatRefractoryTicks);
    private readonly TempoTracker _tempo = new((float)(1.0 / TickIntervalSeconds), BeatLeadSeconds);
    private long _onsetCount;
    private long _beatCount;
    private long _rawKickCount;

    private readonly float[] _sampleWindow = new float[FftSize];
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2];

    private int[] _bandBinStart = new int[BandCount];
    private int[] _bandBinEnd = new int[BandCount];
    private int _bandEdgesSampleRate = -1;

    private float _centroidRunningMaxHz = 1000f;

    private Thread? _thread;
    private volatile bool _running;
    private double _lastConsoleLogSeconds;

    public AudioAnalyzer(AudioCapture capture, AudioFrameBus bus)
    {
        _capture = capture;
        _bus = bus;

        _envelopeFollowers = new EnvelopeFollower[BandCount];
        for (int i = 0; i < BandCount; i++)
        {
            _envelopeFollowers[i] = new EnvelopeFollower(AttackMs, ReleaseMs, TickIntervalSeconds);
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _clock.Start();
        _running = true;
        _thread = new Thread(TickLoop)
        {
            IsBackground = true,
            Name = "AudioAnalyzer",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void TickLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextTick = 0;

        while (_running)
        {
            Tick();

            nextTick += TickIntervalSeconds;
            double remaining = nextTick - sw.Elapsed.TotalSeconds;
            if (remaining > 0)
            {
                Thread.Sleep((int)(remaining * 1000));
            }
            else
            {
                // Fell behind — resync rather than busy-spinning to catch up.
                nextTick = sw.Elapsed.TotalSeconds;
            }
        }
    }

    private void Tick()
    {
        EnsureBandBinsFor(_capture.SampleRate);

        _capture.TryReadLatestMono(_sampleWindow, FftSize);

        double sumSquares = 0;
        float peak = 0f;
        for (int i = 0; i < FftSize; i++)
        {
            float s = _sampleWindow[i];
            sumSquares += s * (double)s;
            float abs = Math.Abs(s);
            if (abs > peak)
            {
                peak = abs;
            }

            float windowCoeff = (float)FastFourierTransform.HannWindow(i, FftSize);
            _fftBuffer[i].X = s * windowCoeff;
            _fftBuffer[i].Y = 0f;
        }
        float rms = (float)Math.Sqrt(sumSquares / FftSize);

        FastFourierTransform.FFT(true, FftSizeLog2, _fftBuffer);

        for (int i = 0; i < _magnitudes.Length; i++)
        {
            float re = _fftBuffer[i].X;
            float im = _fftBuffer[i].Y;
            _magnitudes[i] = MathF.Sqrt((re * re) + (im * im));
        }
        _magnitudes[0] = 0f; // drop DC so it doesn't skew bands/centroid/flux.

        float binWidthHz = (float)_capture.SampleRate / FftSize;

        var bandEnergiesRaw = ImmutableArray.CreateBuilder<float>(BandCount);
        for (int b = 0; b < BandCount; b++)
        {
            int start = _bandBinStart[b];
            int end = _bandBinEnd[b];
            float sum = 0f;
            int count = Math.Max(1, end - start);
            for (int i = start; i < end; i++)
            {
                sum += _magnitudes[i];
            }
            bandEnergiesRaw.Add(sum / count);
        }

        var bandEnergies = ImmutableArray.CreateBuilder<float>(BandCount);
        for (int b = 0; b < BandCount; b++)
        {
            bandEnergies.Add(_envelopeFollowers[b].Update(bandEnergiesRaw[b]));
        }

        double weightedSum = 0;
        double magSum = 0;
        for (int i = 1; i < _magnitudes.Length; i++)
        {
            float freqHz = i * binWidthHz;
            weightedSum += freqHz * _magnitudes[i];
            magSum += _magnitudes[i];
        }
        float centroidHz = magSum > 1e-9 ? (float)(weightedSum / magSum) : 0f;

        if (centroidHz > _centroidRunningMaxHz)
        {
            _centroidRunningMaxHz = centroidHz;
        }
        else
        {
            _centroidRunningMaxHz = Math.Max(1000f, _centroidRunningMaxHz * 0.999f);
        }
        float centroid01 = _centroidRunningMaxHz > 1e-6f ? Math.Clamp(centroidHz / _centroidRunningMaxHz, 0f, 1f) : 0f;

        (float normalizedFlux, bool onsetDetected) = _onsetDetector.Process(_magnitudes);

        int beatStart = _bandBinStart[BeatBandFirst];
        int beatEnd = _bandBinEnd[BeatBandLast];
        (_, bool rawBeat) = _beatDetector.Process(_magnitudes.AsSpan(beatStart, beatEnd - beatStart));
        bool gridBeat = _tempo.Process(_beatDetector.RawFlux);
        bool beatDetected = _tempo.Locked ? gridBeat : rawBeat;

        if (onsetDetected)
        {
            _onsetCount++;
        }
        if (beatDetected)
        {
            _beatCount++;
        }
        if (rawBeat)
        {
            _rawKickCount++;
        }

        bool isSilent = _capture.Status != CaptureStatus.Capturing;

        var frame = new AudioFrame
        {
            TimestampSeconds = _clock.Elapsed.TotalSeconds,
            Status = _capture.Status,
            IsSilent = isSilent,
            Rms = rms,
            Peak = peak,
            SpectralCentroid01 = centroid01,
            Flux = normalizedFlux,
            OnsetDetected = onsetDetected,
            BeatDetected = beatDetected,
            OnsetCount = _onsetCount,
            BeatCount = _beatCount,
            TempoLocked = _tempo.Locked,
            TempoBpm = _tempo.Bpm,
            BeatPhase = _tempo.Phase,
            TempoConfidence = _tempo.Confidence,
            RawKickCount = _rawKickCount,
            BandEnergies = bandEnergies.MoveToImmutable(),
            BandEnergiesRaw = bandEnergiesRaw.MoveToImmutable(),
        };

        _bus.Publish(frame);

        MaybeLogToConsole(frame);
    }

    private void EnsureBandBinsFor(int sampleRate)
    {
        if (sampleRate == _bandEdgesSampleRate)
        {
            return;
        }

        _bandEdgesSampleRate = sampleRate;
        float binWidthHz = (float)sampleRate / FftSize;
        int maxBin = (FftSize / 2) - 1;

        int[] edgeBins = new int[BandEdgesHz.Length];
        for (int i = 0; i < BandEdgesHz.Length; i++)
        {
            edgeBins[i] = Math.Clamp((int)MathF.Round(BandEdgesHz[i] / binWidthHz), 0, maxBin);
        }

        for (int b = 0; b < BandCount; b++)
        {
            int start = edgeBins[b];
            int end = Math.Max(start + 1, edgeBins[b + 1]);
            _bandBinStart[b] = start;
            _bandBinEnd[b] = Math.Min(end, maxBin + 1);
        }
    }

    private void MaybeLogToConsole(AudioFrame frame)
    {
        if (frame.TimestampSeconds - _lastConsoleLogSeconds < ConsoleLogIntervalSeconds)
        {
            return;
        }
        _lastConsoleLogSeconds = frame.TimestampSeconds;

        string bands = string.Join(", ", frame.BandEnergies.Select(v => v.ToString("F4")));
        Console.WriteLine(
            $"[AudioAnalyzer] status={frame.Status} rms={frame.Rms:F4} centroid01={frame.SpectralCentroid01:F3} " +
            $"flux={frame.Flux:F3} onsets={frame.OnsetCount} beats={frame.BeatCount} " +
            $"tempo={frame.TempoBpm:F1}bpm locked={frame.TempoLocked} conf={frame.TempoConfidence:F2} bands=[{bands}]");
    }

    public void Dispose()
    {
        Stop();
    }
}
