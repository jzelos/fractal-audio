namespace SDL_Demo.Audio;

/// <summary>
/// Current health of the WASAPI loopback capture pipeline, set by <see cref="AudioCapture"/> and
/// read by <see cref="AudioAnalyzer"/>/console logging (and, in a later tier, a debug overlay —
/// not built in this tier).
/// </summary>
public enum CaptureStatus
{
    /// <summary>Receiving data from the capture callback within the steady-state timeout.</summary>
    Capturing,

    /// <summary>Was capturing, but no DataAvailable callback has fired within the silence
    /// timeout — likely digital silence on the monitored render endpoint.</summary>
    SilenceTimeout,

    /// <summary>No DataAvailable callback has fired at all within the startup grace window —
    /// likely the default render endpoint is held in WASAPI exclusive mode by another
    /// application, or there is no active render endpoint.</summary>
    DeviceUnavailable,
}
