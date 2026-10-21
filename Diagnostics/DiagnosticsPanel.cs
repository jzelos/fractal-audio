using System.Globalization;
using SDL_Demo.Audio;
using SDL_Demo.Graphics;
using SDL_Demo.Mapping;

namespace SDL_Demo.Diagnostics;

/// <summary>
/// Builds the on-screen diagnostics text from the latest <see cref="AudioFrame"/>, the mapper's
/// read-only state and tap tempo. Render thread only. Grid beats and raw kicks each flash their
/// own line, so the regular beat clock can be compared by eye/ear against what the raw detector
/// hears and against the music itself.
/// </summary>
public sealed class DiagnosticsPanel
{
    private const double FlashSeconds = 0.12;
    private const double KickRateWindowSeconds = 5.0;
    private const int PhaseBarWidth = 24;
    private const double TapDisplaySeconds = 10.0; // keep showing the tapped BPM this long after the last tap.

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly List<TextOverlay.Line> _lines = new();
    private readonly Queue<double> _kickTimes = new();
    private long _lastBeatCount;
    private long _lastKickCount;
    private double _beatFlashUntil;
    private double _kickFlashUntil;

    public IReadOnlyList<TextOverlay.Line> Build(
        AudioFrame frame, AudioToFractalMapper mapper, TapTempo tap, double nowSeconds, double frameTimeSeconds)
    {
        if (frame.BeatCount != _lastBeatCount)
        {
            _lastBeatCount = frame.BeatCount;
            _beatFlashUntil = nowSeconds + FlashSeconds;
        }
        if (frame.RawKickCount != _lastKickCount)
        {
            _lastKickCount = frame.RawKickCount;
            _kickFlashUntil = nowSeconds + FlashSeconds;
            _kickTimes.Enqueue(nowSeconds);
        }
        while (_kickTimes.Count > 0 && nowSeconds - _kickTimes.Peek() > KickRateWindowSeconds)
        {
            _kickTimes.Dequeue();
        }

        _lines.Clear();

        // Tempo: green when locked, amber while searching (beats then come from the raw detector).
        if (frame.TempoLocked)
        {
            Add($"Tempo  {frame.TempoBpm,6:F1} BPM  LOCKED     conf {frame.TempoConfidence:F2}", 120, 230, 120);
        }
        else
        {
            string bpm = frame.TempoBpm > 0 ? $"{frame.TempoBpm,6:F1} BPM" : "  ---     ";
            Add($"Tempo  {bpm}  searching  conf {frame.TempoConfidence:F2}", 240, 180, 80);
        }

        if (tap.Bpm is float tapBpm && tap.SecondsSinceLastTap(nowSeconds) < TapDisplaySeconds)
        {
            string ratio = frame.TempoBpm > 0 ? $"   tracker/tap x{frame.TempoBpm / tapBpm:F2}" : string.Empty;
            Add($"Tap    {tapBpm,6:F1} BPM  ({tap.TapCount} taps){ratio}", 150, 200, 255);
        }
        else
        {
            Add("Tap    press T or Space along with the beat", 140, 140, 140);
        }

        int filled = (int)(Math.Clamp(frame.BeatPhase, 0f, 1f) * PhaseBarWidth);
        Add($"Phase  [{new string('#', filled)}{new string('-', PhaseBarWidth - filled)}]");

        bool beatOn = nowSeconds < _beatFlashUntil;
        string beatSource = frame.TempoLocked ? "grid" : "raw";
        Add($"{(beatOn ? "BEAT" : "beat")}   #{frame.BeatCount} ({beatSource})",
            (byte)(beatOn ? 255 : 90), (byte)(beatOn ? 230 : 90), (byte)(beatOn ? 60 : 90));

        bool kickOn = nowSeconds < _kickFlashUntil;
        float kickRate = _kickTimes.Count / (float)KickRateWindowSeconds;
        Add($"{(kickOn ? "KICK" : "kick")}   #{frame.RawKickCount} raw detector, {kickRate:F1}/s ({kickRate * 60f:F0}/min)",
            (byte)(kickOn ? 80 : 90), (byte)(kickOn ? 220 : 90), (byte)(kickOn ? 255 : 90));

        Add($"Audio  {frame.Status}  rms {frame.Rms:F3}  centroid {frame.SpectralCentroid01:F2}  onsets #{frame.OnsetCount}");

        // Normalised bands as single digits 0-9: sub, bass, low-mid, mid, high-mid, presence, air.
        ReadOnlySpan<float> bands = mapper.NormalizedBands;
        var digits = new System.Text.StringBuilder();
        foreach (float b in bands)
        {
            digits.Append(' ').Append((int)Math.Round(Math.Clamp(b, 0f, 1f) * 9f));
        }
        Add($"Bands {digits}   (sub bass lo mid hi pres air, 0-9)");

        MapperDiagnostics m = mapper.Diagnostics;
        Add(string.Format(Inv, "Julia  c = {0:+0.000;-0.000} {1:+0.000;-0.000}i   waypoint {2}/{3}",
            m.Cx, m.Cy, m.Waypoint + 1, m.WaypointCount));
        Add($"Zoom   {m.Zoom:F2} (fit {m.FitZoom:F2})   palette {m.PaletteFrom}->{m.PaletteTo} blend {m.PaletteBlend:F2}");

        Add($"Frame  {frameTimeSeconds * 1000:F1} ms      D: hide overlay   T/Space: tap", 140, 140, 140);

        return _lines;
    }

    private void Add(string text, byte r = 220, byte g = 220, byte b = 220) =>
        _lines.Add(new TextOverlay.Line(text, r, g, b));
}
