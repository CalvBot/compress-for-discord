using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>
/// Plays 16-bit stereo 48 kHz sound as it's made, through Windows' waveOut: a few short buffers are queued and
/// refilled as each finishes, so playing can start as soon as the first ones are in rather than once the whole
/// stretch has been decoded. Starts paused; Start lets it go, so the picture and sound can begin together.
/// </summary>
sealed class AudioOut : IDisposable
{
    const int Rate = 48000, BytesPerSample = 4;
    /// <summary>Queued buffers, 50 ms each: enough to ride out a busy moment, little enough to stop at once</summary>
    const int Buffers = 8, BufferBytes = Rate * BytesPerSample / 20;
    /// <summary>Buffers queued before it counts as ready to start</summary>
    const int Primed = 3;

    IntPtr wave;
    readonly IntPtr[] headers = new IntPtr[Buffers];
    readonly bool[] prepared = new bool[Buffers];
    static readonly int HeaderSize = Marshal.SizeOf<WaveHdr>();
    static readonly int FlagsAt = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));
    const int WHDR_DONE = 1;

    AudioOut(IntPtr wave)
    {
        this.wave = wave;
        for (var i = 0; i < Buffers; i++)
        {
            headers[i] = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHdr { lpData = Marshal.AllocHGlobal(BufferBytes) }, headers[i], false);
        }
    }

    /// <summary>The default output, paused; null if there's no sound device</summary>
    public static AudioOut? Open()
    {
        var format = new WaveFormat { wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * BytesPerSample, nBlockAlign = BytesPerSample, wBitsPerSample = 16 };
        if (waveOutOpen(out var wave, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) return null;
        waveOutPause(wave);
        return new AudioOut(wave);
    }

    /// <summary>Lets the queued sound play</summary>
    public void Start() => waveOutRestart(wave);

    /// <summary>
    /// Feeds raw sound from the stream into the queue until it ends (or ct). Ready is set once a few buffers are
    /// queued, or when the sound ends before that.
    /// </summary>
    public async Task Pump(Stream pcm, TaskCompletionSource ready, CancellationToken ct)
    {
        var buf = new byte[BufferBytes];
        try
        {
            for (var n = 0; ; n++)
            {
                var i = n % Buffers;
                var h = headers[i];
                // Wait for this buffer to have been played before filling it again
                while (prepared[i] && (Marshal.ReadInt32(h, FlagsAt) & WHDR_DONE) == 0) await Task.Delay(10, ct);
                var got = 0;
                while (got < buf.Length)
                {
                    var r = await pcm.ReadAsync(buf.AsMemory(got), ct);
                    if (r == 0) break;
                    got += r;
                }
                got -= got % BytesPerSample;
                if (got == 0) return;
                if (prepared[i]) { waveOutUnprepareHeader(wave, h, HeaderSize); prepared[i] = false; }
                var data = Marshal.ReadIntPtr(h);
                Marshal.Copy(buf, 0, data, got);
                Marshal.StructureToPtr(new WaveHdr { lpData = data, dwBufferLength = got }, h, false);
                if (waveOutPrepareHeader(wave, h, HeaderSize) != 0) return;
                prepared[i] = true;
                if (waveOutWrite(wave, h, HeaderSize) != 0) return;
                if (n + 1 == Primed) ready.TrySetResult();
                if (got < buf.Length) return; // the end of the sound
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        finally { ready.TrySetResult(); }
    }

    /// <summary>Stops at once. Only after the pump has finished.</summary>
    public void Dispose()
    {
        if (wave == IntPtr.Zero) return;
        // Hands every queued buffer back, so they can be let go
        waveOutReset(wave);
        for (var i = 0; i < Buffers; i++)
        {
            if (prepared[i]) waveOutUnprepareHeader(wave, headers[i], HeaderSize);
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(headers[i]));
            Marshal.FreeHGlobal(headers[i]);
        }
        waveOutClose(wave);
        wave = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat
    {
        public short wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public short nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr
    {
        public IntPtr lpData;
        public int dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr wave, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr wave, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr wave);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr wave);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr wave);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr wave);
}
