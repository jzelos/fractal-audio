namespace SDL_Demo.Diagnostics;

/// <summary>
/// Tap-tempo: the user taps along with the beat they hear; BPM is the mean of the recent tap
/// intervals. A ground truth to compare <see cref="Audio.TempoTracker"/>'s estimate against —
/// in particular to spot octave errors (tracker at 2x or 0.5x the felt beat).
/// </summary>
public sealed class TapTempo
{
    private const double ResetGapSeconds = 2.5; // a pause this long starts a new tap sequence.
    private const int MaxTaps = 9;              // averages the last 8 intervals.

    private readonly Queue<double> _taps = new();

    public int TapCount => _taps.Count;

    /// <summary>Null until there are at least two taps.</summary>
    public float? Bpm { get; private set; }

    public void Tap(double seconds)
    {
        if (_taps.Count > 0 && seconds - _taps.Last() > ResetGapSeconds)
        {
            _taps.Clear();
            Bpm = null;
        }

        _taps.Enqueue(seconds);
        while (_taps.Count > MaxTaps)
        {
            _taps.Dequeue();
        }

        if (_taps.Count >= 2)
        {
            double span = _taps.Last() - _taps.Peek();
            Bpm = (float)(60.0 * (_taps.Count - 1) / span);
        }
    }

    public double SecondsSinceLastTap(double nowSeconds) =>
        _taps.Count == 0 ? double.PositiveInfinity : nowSeconds - _taps.Last();
}
