using System.Globalization;
using SDL_Demo.Audio;

// Usage: TempoReplay <dump.csv>=<trueBpm> [<dump.csv>=<trueBpm> ...] [--timeline]
//
// Feeds each SDL_DEMO_DUMP file through the same BeatPipeline the app runs and reports, per
// dump, the share of tempo readings (sampled every 0.5s after a 10s warm-up) within 3% of the
// true BPM, and how many were octave errors (half/double). Exit code 1 if any dump scores
// below 90%, so it can gate tempo changes.
//
// Dump formats: v1 (written by AudioAnalyzer: "# sdl-demo-dump v1 sampleRate=.. fftSize=..
// bins=.." header, rows "seconds,tempo,mag1..magN") and the legacy ad-hoc dumps recorded during
// tuning (no header, rows "seconds,onset,beat,mag1..mag400[,tempo]", 48kHz / 2048-point FFT).

const double WarmupSeconds = 10;
const double SampleEverySeconds = 0.5;
const double Tolerance = 0.03;
const double PassRate = 0.9;

bool timeline = args.Contains("--timeline");
// --trace=FROM-TO (seconds): print every tempo estimate's decision steps in that range.
(double From, double To)? trace = args.FirstOrDefault(a => a.StartsWith("--trace=")) is string tr
    ? (double.Parse(tr[8..].Split('-')[0], CultureInfo.InvariantCulture), double.Parse(tr[8..].Split('-')[1], CultureInfo.InvariantCulture))
    : null;
bool anyFailed = false;

foreach (string arg in args.Where(a => !a.StartsWith("--")))
{
    int eq = arg.LastIndexOf('=');
    string path = arg[..eq];
    double trueBpm = double.Parse(arg[(eq + 1)..], CultureInfo.InvariantCulture);

    (int sampleRate, int fftSize, List<float[]> frames) = Load(path);
    var bandStart = new int[BandLayout.BandCount];
    var bandEnd = new int[BandLayout.BandCount];
    BandLayout.Compute(sampleRate, fftSize, bandStart, bandEnd);

    var pipeline = new BeatPipeline();
    pipeline.Tempo.TraceEstimates = trace != null;
    string? lastTrace = null;
    int every = (int)(SampleEverySeconds * BeatPipeline.FrameRate);
    int exact = 0, octave = 0, total = 0;
    var line = new List<string>();

    for (int n = 0; n < frames.Count; n++)
    {
        pipeline.Process(frames[n], bandStart, bandEnd);

        double t = n / BeatPipeline.FrameRate;
        if (trace is var (from, to) && t >= from && t <= to && !ReferenceEquals(pipeline.Tempo.LastTrace, lastTrace))
        {
            lastTrace = pipeline.Tempo.LastTrace;
            Console.WriteLine($"  {t,5:F1}s {lastTrace}");
        }
        if (n % every != 0 || t < WarmupSeconds)
        {
            continue;
        }

        double bpm = pipeline.Tempo.Bpm;
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
        $"{(rate >= PassRate ? "PASS" : "FAIL")} {Path.GetFileName(path),-20} true {trueBpm,6:F1}: " +
        $"exact {exact,3}/{total} ({rate:P0})  octave errors {octave,3}");
    if (timeline)
    {
        Console.WriteLine("     " + string.Join(" ", line));
    }
}

return anyFailed ? 1 : 0;

static bool Near(double bpm, double target) => Math.Abs((bpm / target) - 1) < Tolerance;

static (int SampleRate, int FftSize, List<float[]> Frames) Load(string path)
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
