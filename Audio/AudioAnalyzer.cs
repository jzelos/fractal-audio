using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using NAudio.Dsp;

namespace FractalAudio.Audio;

/// <summary>
/// Analyses <see cref="AudioCapture"/>'s mono stream in uniform audio-time frames (one per 20ms
/// of captured samples — see <see cref="TickLoop"/>), on its own thread, and per frame runs a
/// 2048-point Hann-windowed FFT via <c>NAudio.Dsp.FastFourierTransform.FFT</c>, bins into 7
/// log-spaced-by-Hz-range bands computed from the capture's *actual* sample rate
/// (<see cref="BandLayout"/>), computes RMS/peak/spectral centroid, runs a per-band
/// <see cref="EnvelopeFollower"/>, a full-spectrum <see cref="OnsetDetector"/> for general
/// onsets, and the <see cref="BeatPipeline"/> (kick detector + tempo tracker), and publishes one
/// <see cref="AudioFrame"/> per frame to <see cref="AudioFrameBus"/>.
///
/// Setting the FRACTAL_AUDIO_DUMP environment variable to a file path writes every frame's spectrum to
/// that file (see <see cref="WriteDumpRow"/>), for offline analysis and tools/TempoReplay.
/// </summary>
public sealed class AudioAnalyzer : IDisposable
{
    private const int FftSize = 2048;       // power of two, per plan.
    private const int FftSizeLog2 = 11;     // log2(2048), required by FastFourierTransform.FFT's `m` parameter.
    private const int BandCount = BandLayout.BandCount;
    private const double TickIntervalSeconds = 1.0 / BeatPipeline.FrameRate;
    private const double AttackMs = 10.0;
    private const double ReleaseMs = 300.0;
    private const double ConsoleLogIntervalSeconds = 0.5; // ~2/sec, per plan's test instructions.

    private readonly AudioCapture _capture;
    private readonly AudioFrameBus _bus;
    private readonly Stopwatch _clock = new();

    private readonly EnvelopeFollower[] _envelopeFollowers;
    private readonly OnsetDetector _onsetDetector = new();
    private readonly BeatPipeline _beats = new();
    private long _onsetCount;
    private long _beatCount;
    private long _rawKickCount;

    private readonly float[] _sampleWindow = new float[FftSize];
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2];

    private readonly int[] _bandBinStart = new int[BandCount];
    private readonly int[] _bandBinEnd = new int[BandCount];
    private int _bandEdgesSampleRate = -1;

    private float _centroidRunningMaxHz = 1000f;

    private readonly StreamWriter? _dump;
    private bool _dumpHeaderWritten;

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

        if (Environment.GetEnvironmentVariable("FRACTAL_AUDIO_DUMP") is { Length: > 0 } dumpPath)
        {
            _dump = new StreamWriter(dumpPath) { AutoFlush = true };
            Console.WriteLine($"[AudioAnalyzer] dumping frames to {dumpPath}");
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

    /// <summary>
    /// Frames the analysis by audio time, not wall-clock time. WASAPI loopback delivers audio in
    /// bursts, so the old "every 20ms, analyse the latest 2048 samples" loop re-analysed an
    /// identical window on ~60% of ticks and then jumped; the onset signal was effectively 20Hz
    /// with irregular timing, and the 20ms/50ms aliasing produced a phantom ~79 BPM periodicity
    /// that every tempo test track showed. Now every captured hop of samples (20ms of audio) is
    /// analysed exactly once, in order, so downstream "ticks" are uniform audio-time frames.
    /// While no audio arrives (silence timeout / no device), silent frames are produced on the
    /// wall clock instead, so the visuals still decay to idle (WASAPI gotcha #1).
    /// </summary>
    private void TickLoop()
    {
        var sw = Stopwatch.StartNew();
        double lastSilentFrame = 0;
        long nextFrameEnd = -1; // absolute sample index the next frame's window ends at.

        while (_running)
        {
            int sampleRate = _capture.SampleRate;
            EnsureBandBinsFor(sampleRate);
            int hop = Math.Max(1, (int)(sampleRate * TickIntervalSeconds));

            if (_capture.Status == CaptureStatus.Capturing)
            {
                long total = _capture.TotalSamplesWritten;

                // (Re)sync on start, on resume after silence, or if we fell more than half a
                // second behind — skip ahead rather than replaying stale audio.
                if (nextFrameEnd < 0 || total - nextFrameEnd > sampleRate / 2)
                {
                    nextFrameEnd = total;
                }

                while (_running && nextFrameEnd <= total)
                {
                    if (_capture.TryReadMonoEndingAt(nextFrameEnd, _sampleWindow, FftSize))
                    {
                        AnalyzeWindow();
                    }
                    nextFrameEnd += hop;
                }
                lastSilentFrame = sw.Elapsed.TotalSeconds;
            }
            else
            {
                nextFrameEnd = -1;
                double now = sw.Elapsed.TotalSeconds;
                if (now - lastSilentFrame >= TickIntervalSeconds)
                {
                    lastSilentFrame = now;
                    Array.Clear(_sampleWindow);
                    AnalyzeWindow();
                }
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Analyses <see cref="_sampleWindow"/> (one frame) and publishes an AudioFrame.</summary>
    private void AnalyzeWindow()
    {
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
        (bool beatDetected, bool rawKick) = _beats.Process(_magnitudes, _bandBinStart, _bandBinEnd);
        TempoTracker tempo = _beats.Tempo;

        if (onsetDetected)
        {
            _onsetCount++;
        }
        if (beatDetected)
        {
            _beatCount++;
        }
        if (rawKick)
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
            TempoLocked = tempo.Locked,
            TempoBpm = tempo.Bpm,
            BeatPhase = tempo.Phase,
            TempoConfidence = tempo.Confidence,
            RawKickCount = _rawKickCount,
            BandEnergies = bandEnergies.MoveToImmutable(),
            BandEnergiesRaw = bandEnergiesRaw.MoveToImmutable(),
        };

        _bus.Publish(frame);
        WriteDumpRow(frame);
        MaybeLogToConsole(frame);
    }

    /// <summary>
    /// Opt-in frame dump (FRACTAL_AUDIO_DUMP=path). Format: a header line
    /// "# sdl-demo-dump v1 sampleRate=N fftSize=N bins=N", then one row per frame:
    /// "seconds,tempoBpm,mag[1],...,mag[bins]" — magnitudes up to the top band's end, which is
    /// everything the beat pipeline reads, so tools/TempoReplay can reproduce it exactly.
    /// </summary>
    private void WriteDumpRow(AudioFrame frame)
    {
        if (_dump == null)
        {
            return;
        }

        int bins = _bandBinEnd[BandCount - 1];
        if (!_dumpHeaderWritten)
        {
            _dump.WriteLine($"# sdl-demo-dump v1 sampleRate={_capture.SampleRate} fftSize={FftSize} bins={bins}");
            _dumpHeaderWritten = true;
        }

        var row = new System.Text.StringBuilder(bins * 10);
        row.Append(frame.TimestampSeconds.ToString("F4", CultureInfo.InvariantCulture))
            .Append(',').Append(frame.TempoBpm.ToString("F2", CultureInfo.InvariantCulture));
        for (int i = 1; i <= bins; i++)
        {
            row.Append(',').Append(_magnitudes[i].ToString("G6", CultureInfo.InvariantCulture));
        }
        _dump.WriteLine(row.ToString());
    }

    private void EnsureBandBinsFor(int sampleRate)
    {
        if (sampleRate == _bandEdgesSampleRate)
        {
            return;
        }

        _bandEdgesSampleRate = sampleRate;
        BandLayout.Compute(sampleRate, FftSize, _bandBinStart, _bandBinEnd);
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
        _dump?.Dispose();
    }
}
