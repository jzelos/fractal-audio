using NAudio.Wave;

namespace SDL_Demo.Audio;

/// <summary>
/// Wraps <see cref="WasapiLoopbackCapture"/> against the default render endpoint, downmixes to
/// mono, and writes into a fixed-capacity circular float buffer. Runs its own monitor thread
/// (separate from NAudio's internal capture callback thread) that watches for the two timeout
/// gotchas and updates <see cref="Status"/> accordingly. A simple lock guards the ring buffer —
/// writes happen roughly every ~10-20ms from the capture callback and reads happen at the
/// analyzer's tick rate (tens of Hz), so contention is negligible; this is not the
/// <see cref="AudioFrameBus"/> lock-free path, which is a separate, hotter, single-field swap.
///
/// WASAPI gotchas handled explicitly:
/// 1. Silence timeout — loopback capture can simply stop firing DataAvailable during digital
///    silence. <see cref="_lastDataAvailableUtc"/> is tracked; if nothing arrives within
///    <see cref="SilenceTimeoutMs"/> of steady-state capturing, status flips to
///    <see cref="CaptureStatus.SilenceTimeout"/>. Callers should treat that status as "treat the
///    next read as silence", not "keep showing the last real window" — see
///    <see cref="TryReadLatestMono"/>.
/// 2. WaveFormat handling — the actual <see cref="WasapiLoopbackCapture.WaveFormat"/> is read
///    after construction (not assumed to be 48kHz/stereo/float) and downmixing averages
///    whatever channel count is actually reported.
/// 3. Exclusive-mode device — if StartRecording() succeeds but *no* data arrives at all within
///    <see cref="StartupGraceMs"/>, status flips to <see cref="CaptureStatus.DeviceUnavailable"/>
///    with a clear console log, distinct from the steady-state silence timeout.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private const int SilenceTimeoutMs = 250;   // steady-state: no data within this -> silence.
    private const int StartupGraceMs = 2000;    // startup: no data at all within this -> device unavailable.
    private const int MonitorPollMs = 50;

    private readonly object _bufferLock = new();
    private readonly float[] _ringBuffer;
    private readonly int _ringCapacity;
    private int _writePos;       // next write index into _ringBuffer, mod _ringCapacity
    private long _totalSamplesWritten;

    private WasapiLoopbackCapture? _capture;
    private Thread? _monitorThread;
    private volatile bool _running;
    private volatile bool _hasEverReceivedData;
    private long _startUtcTicks;
    private long _lastDataAvailableUtcTicks;

    public CaptureStatus Status { get; private set; } = CaptureStatus.DeviceUnavailable;
    public WaveFormat WaveFormat { get; private set; } = new WaveFormat(48000, 16, 1);
    public int SampleRate => WaveFormat.SampleRate;

    public AudioCapture(double ringBufferSeconds = 2.0)
    {
        // Sized generously (default ~2s at 48kHz) — the analyzer only ever needs the most recent
        // ~2048 samples, this just gives slack against any read/write timing jitter.
        _ringCapacity = Math.Max(4096, (int)(48000 * ringBufferSeconds));
        _ringBuffer = new float[_ringCapacity];
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _capture = new WasapiLoopbackCapture();
        WaveFormat = _capture.WaveFormat;
        Console.WriteLine($"[AudioCapture] actual WaveFormat: {WaveFormat}");

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _startUtcTicks = DateTime.UtcNow.Ticks;
        _lastDataAvailableUtcTicks = _startUtcTicks;
        _hasEverReceivedData = false;
        Status = CaptureStatus.DeviceUnavailable; // optimistic default is wrong — start pessimistic, the monitor thread confirms Capturing once data arrives.
        _running = true;

        // Runs on its own dedicated thread, distinct from NAudio's internal capture callback
        // thread. StartRecording() itself is non-blocking (it kicks off NAudio's own worker
        // thread), so this thread doubles as the status-monitor loop.
        _monitorThread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "AudioCaptureMonitor",
        };
        _monitorThread.Start();
    }

    private void MonitorLoop()
    {
        try
        {
            _capture!.StartRecording();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioCapture] StartRecording failed: {ex.Message}");
            Status = CaptureStatus.DeviceUnavailable;
            return;
        }

        CaptureStatus lastLogged = Status;

        while (_running)
        {
            Thread.Sleep(MonitorPollMs);

            long nowTicks = DateTime.UtcNow.Ticks;
            CaptureStatus next;

            if (!_hasEverReceivedData)
            {
                double sinceStartMs = TimeSpan.FromTicks(nowTicks - _startUtcTicks).TotalMilliseconds;
                next = sinceStartMs > StartupGraceMs ? CaptureStatus.DeviceUnavailable : Status;
            }
            else
            {
                double sinceDataMs = TimeSpan.FromTicks(nowTicks - Interlocked.Read(ref _lastDataAvailableUtcTicks)).TotalMilliseconds;
                next = sinceDataMs > SilenceTimeoutMs ? CaptureStatus.SilenceTimeout : CaptureStatus.Capturing;
            }

            Status = next;

            if (next != lastLogged)
            {
                lastLogged = next;
                switch (next)
                {
                    case CaptureStatus.DeviceUnavailable:
                        Console.WriteLine(
                            "[AudioCapture] DeviceUnavailable: no capture data received — the default " +
                            "render endpoint may be held in WASAPI exclusive mode by another application.");
                        break;
                    case CaptureStatus.SilenceTimeout:
                        Console.WriteLine("[AudioCapture] SilenceTimeout: no new data — treating as silence.");
                        break;
                    case CaptureStatus.Capturing:
                        Console.WriteLine("[AudioCapture] Capturing: receiving data.");
                        break;
                }
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Interlocked.Exchange(ref _lastDataAvailableUtcTicks, DateTime.UtcNow.Ticks);
        _hasEverReceivedData = true;

        int channels = Math.Max(1, WaveFormat.Channels);
        float[] mono = DownmixToMono(e.Buffer, e.BytesRecorded, WaveFormat, channels);

        lock (_bufferLock)
        {
            for (int i = 0; i < mono.Length; i++)
            {
                _ringBuffer[_writePos] = mono[i];
                _writePos = (_writePos + 1) % _ringCapacity;
            }
            _totalSamplesWritten += mono.Length;
        }
    }

    private static float[] DownmixToMono(byte[] buffer, int bytesRecorded, WaveFormat format, int channels)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int sampleCount = bytesRecorded / 4;
            int frameCount = sampleCount / channels;
            float[] mono = new float[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sum = 0f;
                int baseIdx = frame * channels * 4;
                for (int ch = 0; ch < channels; ch++)
                {
                    sum += BitConverter.ToSingle(buffer, baseIdx + (ch * 4));
                }
                mono[frame] = sum / channels;
            }
            return mono;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            int sampleCount = bytesRecorded / 2;
            int frameCount = sampleCount / channels;
            float[] mono = new float[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sum = 0f;
                int baseIdx = frame * channels * 2;
                for (int ch = 0; ch < channels; ch++)
                {
                    short s = BitConverter.ToInt16(buffer, baseIdx + (ch * 2));
                    sum += s / 32768f;
                }
                mono[frame] = sum / channels;
            }
            return mono;
        }

        // Unsupported format encountered — log once per occurrence is too noisy; drop silently
        // to zero-length so the ring buffer just doesn't advance (silence-timeout path handles
        // the rest).
        return Array.Empty<float>();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Console.WriteLine($"[AudioCapture] RecordingStopped with exception: {e.Exception.Message}");
        }
    }

    /// <summary>
    /// Copies the most recent <paramref name="count"/> mono samples into
    /// <paramref name="destination"/> (which must be at least <paramref name="count"/> long).
    /// If <see cref="Status"/> is <see cref="CaptureStatus.SilenceTimeout"/> or
    /// <see cref="CaptureStatus.DeviceUnavailable"/>, fills with zeros instead of returning
    /// stale ring-buffer content — this is what keeps the silence path from "freezing" the last
    /// real window. Zero-pads with silence if fewer than <paramref name="count"/> samples have
    /// ever been written.
    /// </summary>
    public void TryReadLatestMono(float[] destination, int count)
    {
        if (Status != CaptureStatus.Capturing)
        {
            Array.Clear(destination, 0, count);
            return;
        }

        lock (_bufferLock)
        {
            long available = Math.Min(_totalSamplesWritten, _ringCapacity);
            if (available < count)
            {
                // Not enough history yet — zero-pad the front, copy what little exists to the tail.
                Array.Clear(destination, 0, count);
            }

            int toCopy = (int)Math.Min(count, available);
            int readStart = (_writePos - toCopy + _ringCapacity) % _ringCapacity;
            int destOffset = count - toCopy;

            for (int i = 0; i < toCopy; i++)
            {
                destination[destOffset + i] = _ringBuffer[(readStart + i) % _ringCapacity];
            }
        }
    }

    /// <summary>Total mono samples captured so far — the audio-time clock the analyzer frames by.</summary>
    public long TotalSamplesWritten
    {
        get
        {
            lock (_bufferLock)
            {
                return _totalSamplesWritten;
            }
        }
    }

    /// <summary>
    /// Copies the <paramref name="count"/> mono samples ending at absolute sample index
    /// <paramref name="endSample"/> (exclusive) into <paramref name="destination"/>. Returns false
    /// if those samples haven't all arrived yet or have already been overwritten. Samples before
    /// the start of capture are zero-filled. Sample k lives at ring index k % capacity, since the
    /// write position advances in lockstep with the total.
    /// </summary>
    public bool TryReadMonoEndingAt(long endSample, float[] destination, int count)
    {
        lock (_bufferLock)
        {
            if (endSample > _totalSamplesWritten || endSample - count < _totalSamplesWritten - _ringCapacity)
            {
                return false;
            }

            long start = endSample - count;
            for (int i = 0; i < count; i++)
            {
                long k = start + i;
                destination[i] = k < 0 ? 0f : _ringBuffer[k % _ringCapacity];
            }
            return true;
        }
    }

    public float ComputeRms(int sampleCount)
    {
        float[] window = new float[sampleCount];
        TryReadLatestMono(window, sampleCount);

        double sumSquares = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            sumSquares += window[i] * (double)window[i];
        }
        return (float)Math.Sqrt(sumSquares / sampleCount);
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // Best-effort stop; teardown continues regardless.
        }
        _monitorThread?.Join(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        Stop();
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }
}
