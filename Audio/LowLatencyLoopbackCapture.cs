using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SDL_Demo.Audio;

/// <summary>
/// WASAPI loopback capture with a configurable buffer length. NAudio's own
/// <c>WasapiLoopbackCapture</c> always uses a 100ms buffer and, without event sync (unsupported
/// for loopback), polls at half of it — so audio arrived in ~50ms packets (measured: 19.7
/// packets/s, 50.7 ms/packet), making every analysis frame, and so every visual beat, up to
/// ~50ms late and bursty. The base <see cref="WasapiCapture"/> takes a buffer length; the only
/// other thing the loopback subclass adds is the Loopback stream flag, reproduced here.
/// </summary>
public sealed class LowLatencyLoopbackCapture : WasapiCapture
{
    public LowLatencyLoopbackCapture(int bufferMilliseconds)
        : base(GetDefaultRenderDevice(), useEventSync: false, bufferMilliseconds)
    {
    }

    private static MMDevice GetDefaultRenderDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}
