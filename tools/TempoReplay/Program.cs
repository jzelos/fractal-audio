using System.Globalization;
using FractalAudio.Audio;

// Usage: TempoReplay <input> [<input> ...] [--timeline] [--trace=FROM-TO] [--export=DIR]
//
// An input is a recording file, optionally suffixed "=<trueBpm>", or a directory (every
// recording in it). Recordings are either:
//   - spectrum dumps (*.csv) written by the app with FRACTAL_AUDIO_DUMP — the FFT magnitudes,
//     replayed through the same BeatPipeline the app runs. Large, and close enough to the audio
//     to be resynthesised, so they stay out of the repo;
//   - tempo-signal files (*.tsig): just the two values per frame the tempo tracker reads
//     (onset strength, bass flux), with the true BPM in the header. Tiny and not audio, so the
//     test set is committed as these (recordings/signals/). They replay TempoTracker only, so
//     they can't test changes to the spectrum analysis that produces those two values.
//
// For each recording, reports the share of tempo readings (sampled every 0.5s after a 10s
// warm-up) within 3% of the true BPM, and how many were octave errors (half/double). Exit code
// 1 if any scores below 90%, so it can gate tempo changes.
//
// --export=DIR writes a .tsig for each spectrum dump into DIR (the true BPM must be given).
//
// Spectrum dump formats: v1 (written by AudioAnalyzer: "# sdl-demo-dump v1 sampleRate=..
// fftSize=.. bins=.." header, rows "seconds,tempo,mag1..magN") and the legacy ad-hoc dumps
// recorded during tuning (no header, rows "seconds,onset,beat,mag1..mag400[,tempo]", 48kHz /
// 2048-point FFT). Tempo-signal format: header "# fractal-audio tempo-signals v1 frameRate=50
// trueBpm=N", then rows "onset,bassFlux".

const double WarmupSeconds = 10;
const double SampleEverySeconds = 0.5;
const double Tolerance = 0.03;
const double PassRate = 0.9;
const string SignalsExtension = ".tsig";

bool timeline = args.Contains("--timeline");
// --trace=FROM-TO (seconds): print every tempo estimate's decision steps in that range.
(double From, double To)? trace = args.FirstOrDefault(a => a.StartsWith("--trace=")) is string tr
    ? (double.Parse(tr[8..].Split('-')[0], CultureInfo.InvariantCulture), double.Parse(tr[8..].Split('-')[1], CultureInfo.InvariantCulture))
    : null;
string? exportDir = args.FirstOrDefault(a => a.StartsWith("--export="))?[9..];
bool anyFailed = false;

foreach ((string path, double? givenBpm) in Inputs(args.Where(a => !a.StartsWith("--"))))
{
    bool isSignals = path.EndsWith(SignalsExtension, StringComparison.OrdinalIgnoreCase);
    TempoTracker tracker;
    Action<int> step;
    int frameCount;
    double? headerBpm = null;
    List<(float Onset, float BassFlux)>? exported = null;

    if (isSignals)
    {
        (List<(float Onset, float BassFlux)> signals, headerBpm) = LoadSignals(path);
        tracker = new TempoTracker(BeatPipeline.FrameRate, BeatPipeline.BeatLeadSeconds);
        step = n => tracker.Process(signals[n].Onset, signals[n].BassFlux);
        frameCount = signals.Count;
    }
    else
    {
        (int sampleRate, int fftSize, List<float[]> frames) = LoadSpectra(path);
        var bandStart = new int[BandLayout.BandCount];
        var bandEnd = new int[BandLayout.BandCount];
        BandLayout.Compute(sampleRate, fftSize, bandStart, bandEnd);
        var pipeline = new BeatPipeline();
        tracker = pipeline.Tempo;
        exported = exportDir != null ? new() : null;
        step = n =>
        {
            pipeline.Process(frames[n], bandStart, bandEnd);
            exported?.Add(pipeline.LastTempoInput);
        };
        frameCount = frames.Count;
    }

    if ((givenBpm ?? headerBpm) is not double trueBpm)
    {
        Console.WriteLine($"SKIP {Path.GetFileName(path)}: no true BPM (give it as {path}=<bpm>)");
        anyFailed = true;
        continue;
    }

    tracker.TraceEstimates = trace != null;
    string? lastTrace = null;
    int every = (int)(SampleEverySeconds * BeatPipeline.FrameRate);
    int exact = 0, octave = 0, total = 0;
    var line = new List<string>();

    for (int n = 0; n < frameCount; n++)
    {
        step(n);

        double t = n / BeatPipeline.FrameRate;
        if (trace is var (from, to) && t >= from && t <= to && !ReferenceEquals(tracker.LastTrace, lastTrace))
        {
            lastTrace = tracker.LastTrace;
            Console.WriteLine($"  {t,5:F1}s {lastTrace}");
        }
        if (n % every != 0 || t < WarmupSeconds)
        {
            continue;
        }

        double bpm = tracker.Bpm;
        total++;
        if (Near(bpm, trueBpm))
        {
            exact++;
        }
        else if (Near(bpm, trueBpm / 2) || Near(bpm, trueBpm * 2))
        {
            octave++;
        }
        if (n % (every * 6) == 0)
        {
            line.Add($"{t:F0}s:{bpm:F0}");
        }
    }

    double rate = total > 0 ? (double)exact / total : 0;
    anyFailed |= rate < PassRate;
    Console.WriteLine(
        $"{(rate >= PassRate ? "PASS" : "FAIL")} {Path.GetFileName(path),-24} true {trueBpm,6:F1}: " +
        $"exact {exact,3}/{total} ({rate:P0})  octave errors {octave,3}");
    if (timeline)
    {
        Console.WriteLine("     " + string.Join(" ", line));
    }

    if (exported != null && exportDir != null)
    {
        Directory.CreateDirectory(exportDir);
        string outPath = Path.Combine(exportDir, Path.GetFileNameWithoutExtension(path) + SignalsExtension);
        WriteSignals(outPath, trueBpm, exported);
        Console.WriteLine($"     exported {outPath}");
    }
}

return anyFailed ? 1 : 0;

static bool Near(double bpm, double target) => Math.Abs((bpm / target) - 1) < Tolerance;

// Expands directories to their recordings (*.tsig and *.csv) and
// splits off an optional "=<trueBpm>".
static IEnumerable<(string Path, double? Bpm)> Inputs(IEnumerable<string> args)
{
    foreach (string arg in args)
    {
        if (Directory.Exists(arg))
        {
            foreach (string file in Directory.EnumerateFiles(arg)
                .Where(f => f.EndsWith(SignalsExtension, StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                yield return (file, null);
            }
            continue;
        }

        int eq = arg.LastIndexOf('=');
        yield return eq > 0
            ? (arg[..eq], double.Parse(arg[(eq + 1)..], CultureInfo.InvariantCulture))
            : (arg, null);
    }
}

static (int SampleRate, int FftSize, List<float[]> Frames) LoadSpectra(string path)
{
    int sampleRate = 48000, fftSize = 2048, firstMag = 3, magCount = 400;
    var frames = new List<float[]>();

    foreach (string raw in File.ReadLines(path))
    {
        if (raw.StartsWith('#'))
        {
            foreach (string kv in raw.Split(' ').Where(p => p.Contains('=')))
            {
                string[] p = kv.Split('=');
                int v = int.Parse(p[1], CultureInfo.InvariantCulture);
                switch (p[0])
                {
                    case "sampleRate": sampleRate = v; break;
                    case "fftSize": fftSize = v; break;
                    case "bins": magCount = v; break;
                }
            }
            firstMag = 2;
            continue;
        }

        string[] cols = raw.Split(',');
        var mags = new float[fftSize / 2];
        int count = Math.Min(magCount, cols.Length - firstMag);
        for (int i = 0; i < count; i++)
        {
            mags[i + 1] = float.Parse(cols[firstMag + i], CultureInfo.InvariantCulture);
        }
        frames.Add(mags);
    }
    return (sampleRate, fftSize, frames);
}

static (List<(float Onset, float BassFlux)> Signals, double? TrueBpm) LoadSignals(string path)
{
    var signals = new List<(float, float)>();
    double? trueBpm = null;
    foreach (string raw in File.ReadLines(path))
    {
        if (raw.StartsWith('#'))
        {
            foreach (string kv in raw.Split(' ').Where(p => p.Contains('=')))
            {
                string[] p = kv.Split('=');
                if (p[0] == "frameRate" && float.Parse(p[1], CultureInfo.InvariantCulture) != BeatPipeline.FrameRate)
                {
                    throw new InvalidDataException($"{path}: frameRate {p[1]}, tracker expects {BeatPipeline.FrameRate}");
                }
                if (p[0] == "trueBpm")
                {
                    trueBpm = double.Parse(p[1], CultureInfo.InvariantCulture);
                }
            }
            continue;
        }

        string[] cols = raw.Split(',');
        signals.Add((float.Parse(cols[0], CultureInfo.InvariantCulture), float.Parse(cols[1], CultureInfo.InvariantCulture)));
    }
    return (signals, trueBpm);
}

// Floats are written round-trip exact, so a replay of the file matches the dump's exactly.
static void WriteSignals(string path, double trueBpm, List<(float Onset, float BassFlux)> signals)
{
    using var writer = new StreamWriter(path) { NewLine = "\n" };
    writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"# fractal-audio tempo-signals v1 frameRate={BeatPipeline.FrameRate} trueBpm={trueBpm}"));
    foreach ((float onset, float bass) in signals)
    {
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{onset},{bass}"));
    }
}
