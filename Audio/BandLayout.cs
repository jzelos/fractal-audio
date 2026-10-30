namespace FractalAudio.Audio;

/// <summary>
/// FFT-bin ranges for the analyzer's 7 bands, computed from the *actual* sample rate (not
/// hardcoded for 48kHz). Shared by <see cref="AudioAnalyzer"/> and the tools/TempoReplay harness
/// so a replayed dump sees exactly the bin ranges the live app used.
/// </summary>
public static class BandLayout
{
    public const int BandCount = 7;

    // Band edges in Hz: sub-bass, bass, low-mid, mid, high-mid, presence, air.
    public static readonly float[] BandEdgesHz = { 20f, 60f, 150f, 400f, 1000f, 2500f, 6000f, 12000f };

    /// <summary>Fills <paramref name="start"/>/<paramref name="end"/> (end exclusive) with each
    /// band's bin range for an FFT of <paramref name="fftSize"/> at <paramref name="sampleRate"/>.</summary>
    public static void Compute(int sampleRate, int fftSize, int[] start, int[] end)
    {
        float binWidthHz = (float)sampleRate / fftSize;
        int maxBin = (fftSize / 2) - 1;

        int[] edgeBins = new int[BandEdgesHz.Length];
        for (int i = 0; i < BandEdgesHz.Length; i++)
        {
            edgeBins[i] = Math.Clamp((int)MathF.Round(BandEdgesHz[i] / binWidthHz), 0, maxBin);
        }

        for (int b = 0; b < BandCount; b++)
        {
            int s = edgeBins[b];
            int e = Math.Max(s + 1, edgeBins[b + 1]);
            start[b] = s;
            end[b] = Math.Min(e, maxBin + 1);
        }
    }
}
