namespace FractalAudio.Audio;

/// <summary>
/// Single-pole asymmetric envelope follower: fast attack, slow release. Coefficients are derived
/// from attack/release time constants (ms) and the analyzer's tick interval, so the smoothing
/// rate stays correct regardless of what tick rate <see cref="AudioAnalyzer"/> actually runs at.
/// </summary>
public sealed class EnvelopeFollower
{
    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;

    public float Value { get; private set; }

    public EnvelopeFollower(double attackMs, double releaseMs, double tickIntervalSeconds)
    {
        _attackCoeff = ComputeCoefficient(attackMs, tickIntervalSeconds);
        _releaseCoeff = ComputeCoefficient(releaseMs, tickIntervalSeconds);
    }

    private static float ComputeCoefficient(double timeMs, double tickIntervalSeconds)
    {
        if (timeMs <= 0)
        {
            return 0f;
        }
        double tau = timeMs / 1000.0;
        return (float)Math.Exp(-tickIntervalSeconds / tau);
    }

    public float Update(float input)
    {
        float coeff = input > Value ? _attackCoeff : _releaseCoeff;
        Value = (coeff * Value) + ((1f - coeff) * input);
        return Value;
    }
}
