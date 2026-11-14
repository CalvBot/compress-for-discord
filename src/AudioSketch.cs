using System.Diagnostics;
using System.Runtime.InteropServices;
using static CompressForDiscord.Mf;

namespace CompressForDiscord;

/// <summary>
/// A rough first look at the tracks' waveforms, across everything on screen at once, ahead of ffmpeg's exact (but
/// start-to-finish) measuring. Windows' decoders (Media Foundation) jump to a spot in the file and decode a few
/// milliseconds of sound there almost instantly, so this reads spots spread evenly over the view: a handful first,
/// then halfway between those, and so on, so the shape sharpens as you watch. Zoom in and it starts again, coarse to
/// fine, over the new view, so a long recording can be explored before it's measured. Files Windows can't read just
/// don't get a sketch.
/// </summary>
static class AudioSketch
{
    /// <summary>Decoded pieces read at each spot (~20 ms each); the first after a jump can be quiet while the decoder warms up</summary>
    const int ReadsPerSpot = 3;
    /// <summary>Spots across the view at the finest: about two pixels apart on a typical lane</summary>
    const int SpotsPerView = 512;
    /// <summary>ffmpeg waits until this many spots are across the view (~0.7 s): a whole shape first, then detail</summary>
    const int LeadSpots = 64;

    /// <param name="view">The part of the recording on screen</param>
    /// <param name="leading">
    /// The sketch is taken to be going first (ffmpeg waits) from the start; told false when it's done that or can't
    /// sketch, and true again when it goes first over a new view
    /// </param>
    /// <param name="fileKinds">The file's video and audio streams in order (ProbeResult.StreamKinds)</param>
    public static void Run(string path, string fileKinds, IReadOnlyList<(Waveform Wave, int Track)> tracks, Func<(double From, double To)> view,
        Func<bool> paused, Action<bool> leading, CancellationToken ct)
    {
        if (tracks.Count == 0 || !Started.Value)
        {
            leading(false);
            return;
        }
        var clock = Stopwatch.StartNew();
        IMFSourceReader? reader = null;
        var spots = 0;
        var leadingNow = true;
        long firstLook = -1;
        try
        {
            Check(MFCreateSourceReaderFromURL(path, null!, out reader));
            Check(reader.SetStreamSelection(AllStreams, false));
            // A track (ffmpeg's numbering) is the nth audio stream in the file. Windows can list the streams in another
            // order: an MP4's came out backwards (video last, audio tracks reversed), and its own stream IDs are only
            // its numbering. So compare the order of video and audio streams with the file's.
            var audio = new List<uint>();
            var kinds = "";
            for (uint s = 0; reader.GetNativeMediaType(s, 0, out var native) >= 0; s++)
            {
                var key = MF_MT_MAJOR_TYPE;
                if (native.GetGUID(ref key, out var major) >= 0)
                {
                    if (major == MFMediaType_Audio)
                    {
                        audio.Add(s);
                        kinds += "a";
                    }
                    else if (major == MFMediaType_Video) kinds += "v";
                }
                Marshal.ReleaseComObject(native);
            }
            if (audio.Count > 1)
            {
                var backwards = new string(fileKinds.Reverse().ToArray());
                // Can't tell which way round (the same either way, or not the same streams): better no sketch than the
                // wrong track's
                if (fileKinds == backwards || (kinds != fileKinds && kinds != backwards))
                {
                    AppLog.Info($"No quick waveform sketch: can't match Windows' streams ({kinds}) to the file's ({fileKinds})");
                    return;
                }
                if (kinds == backwards) audio.Reverse();
            }
            var waveOf = new Dictionary<uint, int>();
            for (var i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Track >= audio.Count) continue;
                var s = audio[tracks[i].Track];
                Check(MFCreateMediaType(out var pcm));
                try
                {
                    var (majorKey, subKey, bitsKey) = (MF_MT_MAJOR_TYPE, MF_MT_SUBTYPE, MF_MT_AUDIO_BITS_PER_SAMPLE);
                    var (kind, format) = (MFMediaType_Audio, MFAudioFormat_PCM);
                    Check(pcm.SetGUID(ref majorKey, ref kind));
                    Check(pcm.SetGUID(ref subKey, ref format));
                    Check(pcm.SetUINT32(ref bitsKey, 16));
                    // A track Windows can't decode is left out; the others still get sketched
                    if (reader.SetCurrentMediaType(s, IntPtr.Zero, pcm) < 0) continue;
                }
                finally { Marshal.ReleaseComObject(pcm); }
                Check(reader.SetStreamSelection(s, true));
                waveOf[s] = i;
            }
            if (waveOf.Count == 0) return;

            var waves = tracks.Select(t => t.Wave).ToArray();
            var n = waves[0].SketchPoints;
            var per = n / waves[0].Duration;
            var visited = new bool[n];
            var got = new int[tracks.Count];
            var peak = new int[tracks.Count];
            var samples = new short[4096];
            // ffmpeg has measured all of it: the sketch wouldn't show anywhere
            while (!waves.All(w => w.Complete))
            {
                ct.ThrowIfCancellationRequested();
                if (paused())
                {
                    Thread.Sleep(50);
                    continue;
                }
                var seen = view();
                var (from, to) = seen.To > seen.From ? seen : (0, waves[0].Duration);
                var a = Math.Clamp((int)(from * per), 0, n - 1);
                var b = Math.Clamp((int)Math.Ceiling(to * per) - 1, a, n - 1);
                // The finest spacing worth reading here: about two pixels apart on screen
                var finest = 1;
                while (finest * SpotsPerView < b - a + 1) finest *= 2;
                var pass = NextPass(visited, a, b, finest, out var stride);
                if (pass.Count == 0)
                {
                    // All of what's on screen is sketched: wait for a zoom or scroll somewhere new
                    Lead(false);
                    Thread.Sleep(100);
                    continue;
                }
                // The first few passes over what's on screen go before ffmpeg's exact measuring, so the whole view has
                // a shape within a fraction of a second
                Lead((b - a + 1) / stride <= LeadSpots);
                foreach (var i in pass)
                {
                    // Moved: start again from coarse over the new view
                    if (view() != seen || paused() || ct.IsCancellationRequested) break;
                    visited[i] = true;
                    var at = waves[0].SketchTime(i);
                    // Measured there already: the sketch wouldn't show
                    if (waveOf.Values.All(w => waves[w].Measured(at))) continue;
                    if (ReadSpot(at)) spots++;
                }
            }
            AppLog.Info($"Waveform sketch: {spots} spots in {clock.ElapsedMilliseconds} ms");

            bool ReadSpot(double at)
            {
                var format = Guid.Empty;
                var pos = new PropVariant { vt = 20, hVal = (long)(at * 1e7) };
                if (reader.SetCurrentPosition(ref format, ref pos) < 0) return false;
                Array.Clear(got);
                Array.Clear(peak);
                for (var reads = 0; reads < 10 * ReadsPerSpot * waveOf.Count && waveOf.Values.Any(w => got[w] < ReadsPerSpot); reads++)
                {
                    if (reader.ReadSample(AnyStream, 0, out var stream, out var flags, out _, out var sample) < 0) break;
                    if (!waveOf.TryGetValue(stream, out var w))
                    {
                        if (sample != null) Marshal.ReleaseComObject(sample);
                        if ((flags & 1) != 0) break; // an error
                        continue;
                    }
                    // The track ended before here: silent
                    if ((flags & 2) != 0) got[w] = ReadsPerSpot;
                    if (sample == null) continue;
                    try
                    {
                        peak[w] = Math.Max(peak[w], Loudest(sample, samples));
                        got[w]++;
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
                var i = Math.Clamp((int)(at * per), 0, n - 1);
                foreach (var w in waveOf.Values) waves[w].Sketch(i, peak[w]);
                return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Info($"No quick waveform sketch after {spots} spots ({ex.Message})"); }
        finally
        {
            Lead(false);
            if (reader != null) Marshal.ReleaseComObject(reader);
        }

        void Lead(bool on)
        {
            if (on == leadingNow) return;
            leadingNow = on;
            leading(on);
            if (!on && firstLook < 0 && spots > 0)
            {
                firstLook = clock.ElapsedMilliseconds;
                AppLog.Info($"Waveform sketch: first look in {firstLook} ms ({spots} spots)");
            }
        }
    }

    /// <summary>
    /// The coarsest pass over spots a to b with any left to read: every stride-th spot, the stride halving each pass
    /// (so each pass lands halfway between the last one's), down to every finest-th
    /// </summary>
    static List<int> NextPass(bool[] visited, int a, int b, int finest, out int stride)
    {
        var list = new List<int>();
        for (stride = 1 << (int)Math.Log2(visited.Length); stride >= finest; stride /= 2)
        {
            for (var i = (a + stride - 1) / stride * stride; i <= b; i += stride) if (!visited[i]) list.Add(i);
            if (list.Count > 0) return list;
        }
        return list;
    }

    /// <summary>The loudest sample (0..32768) in a piece of decoded 16-bit sound, any channel</summary>
    static int Loudest(IMFSample sample, short[] buf)
    {
        Check(sample.ConvertToContiguousBuffer(out var buffer));
        try
        {
            Check(buffer.Lock(out var data, out _, out var length));
            try
            {
                var loud = 0;
                for (var at = 0; at + 2 <= length;)
                {
                    var n = (int)Math.Min(buf.Length, (length - at) / 2);
                    Marshal.Copy(data + at, buf, 0, n);
                    for (var k = 0; k < n; k++) loud = Math.Max(loud, Math.Abs((int)buf[k]));
                    at += 2 * n;
                }
                return loud;
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }
}
