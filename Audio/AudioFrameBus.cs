namespace SDL_Demo.Audio;

/// <summary>
/// Lock-free single-writer (audio analyzer thread)/single-reader (render thread) publish/consume
/// bus. Reference assignment/read in .NET is atomic, so a `volatile` reference field gives a
/// correct swap with zero locking and zero risk of the render thread observing a torn mix of
/// old/new fields. <see cref="Latest"/> never returns null — it starts at
/// <see cref="AudioFrame.Empty"/> so the render thread always has a valid frame from the first
/// render call onward, even before capture/analysis has produced anything.
/// </summary>
public sealed class AudioFrameBus
{
    private volatile AudioFrame _latest = AudioFrame.Empty;

    /// <summary>Render thread reads this, lock-free. Never null.</summary>
    public AudioFrame Latest => _latest;

    /// <summary>Audio analyzer thread writes this — exactly one call per analysis tick.</summary>
    public void Publish(AudioFrame frame) => _latest = frame;
}
