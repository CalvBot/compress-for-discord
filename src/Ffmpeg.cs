using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompressForDiscord;

sealed record AudioTrack(int Index, string Codec, int Channels, string Layout, string Title, string Language, bool IsDefault)
{
    /// <summary>The track's own name ("Game", "Mic"); "Track 3" when it has none, or only its number</summary>
    public string Label => Title != "" && Title != (Index + 1).ToString() ? Title : $"Track {Index + 1}";
}

sealed record ProbeResult(double Duration, int Width, int Height, double Fps, long Bitrate, long VideoBitrate, string VideoCodec, List<AudioTrack> AudioTracks);

/// <param name="Volume">1 = unchanged; used when there are no Keys</param>
/// <param name="Keys">volume points in source time, in order; the volume moves in straight lines between them</param>
sealed record TrackMix(int Index, double Volume, List<(double T, double V)>? Keys = null);

sealed class EncodeRequest
{
    public required string Input;
    public double Start, Duration;
    public long TargetBytes;
    public int VideoKbps, AudioKbps;
    public List<TrackMix> Tracks = [];
    public int? ScaleShortSide, Fps;
    public bool Portrait;
    public string Encoder = "auto";
    /// <summary>Volume of the finished mix (the master lane); null = unchanged</summary>
    public TrackMix? Master;
    public bool CappedBySource;
    public double Calibration;
    /// <summary>Crop box in source pixels (left, top, width) at each key time; null = whole frame. Every box has the shape CropW:CropH.</summary>
    public List<(double T, double X, double Y, double W)>? Crop;
    /// <summary>Size of the smallest crop box. The output is this (times the scale), so nothing is ever enlarged.</summary>
    public int CropW, CropH;
}

static class Ff
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F3(double d) => d.ToString("0.000", Inv);

    public static string Ffmpeg = "", Ffprobe = "";

    // ---------- discovery ----------

    /// <summary>Finds ffmpeg and ffprobe; returns an error message, or null when both were found.</summary>
    public static string? Locate(string? configured)
    {
        var ffmpeg = Find("ffmpeg", configured);
        var ffprobe = Find("ffprobe", configured);
        if (ffmpeg == null || ffprobe == null) return "This app needs ffmpeg, which isn't installed yet.";
        Ffmpeg = ffmpeg;
        Ffprobe = ffprobe;
        return null;
    }

    static string? Find(string name, string? configured)
    {
        var exe = name + ".exe";
        var c = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Accept either the folder or the exe itself
            var dir = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(configured)! : configured;
            c.Add(Path.Combine(dir, exe));
            c.Add(Path.Combine(dir, "bin", exe));
        }
        var env = (string v) => Environment.GetEnvironmentVariable(v) ?? "";
        c.Add(Path.Combine(Paths.Ffmpeg, exe)); // our own download (DownloadFfmpeg)
        c.Add(Path.Combine(env("LOCALAPPDATA"), "Microsoft", "WinGet", "Links", exe));
        c.Add(Path.Combine(env("USERPROFILE"), "scoop", "shims", exe));
        c.Add(Path.Combine(env("ProgramData"), "chocolatey", "bin", exe));
        c.Add(Path.Combine(env("ProgramFiles"), "ffmpeg", "bin", exe));
        foreach (var d in env("PATH").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { c.Add(Path.Combine(d.Trim('"'), exe)); } catch { }
        return c.FirstOrDefault(File.Exists);
    }

    // GPL build (has libx264 for the CPU fallback); "shared" = exe + dlls, half the download of the static one
    const string FfmpegZip = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";

    /// <summary>Downloads ffmpeg + ffprobe (and their dlls) into Paths.Ffmpeg. Progress is 0..1.</summary>
    public static async Task DownloadFfmpeg(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.Work);
        var zip = Path.Combine(Paths.Work, "ffmpeg-download.zip");
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CompressForDiscord");
            using var resp = await http.GetAsync(FfmpegZip, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 90_000_000;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            long done = 0;
            for (int n; (n = await src.ReadAsync(buf, ct)) > 0;)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress.Report(Math.Min(1, (double)done / total));
            }
        }

        // Unpack only bin\ (ffmpeg.exe, ffprobe.exe, dlls) into a temp folder, then swap it in
        var tmp = Paths.Ffmpeg + ".new";
        try { Directory.Delete(tmp, true); } catch { }
        Directory.CreateDirectory(tmp);
        using (var archive = ZipFile.OpenRead(zip))
            foreach (var e in archive.Entries)
                if (e.FullName.Contains("/bin/") && e.Name != "" && !e.Name.StartsWith("ffplay", StringComparison.OrdinalIgnoreCase))
                    e.ExtractToFile(Path.Combine(tmp, e.Name), true);
        File.Delete(zip);
        if (!File.Exists(Path.Combine(tmp, "ffmpeg.exe")) || !File.Exists(Path.Combine(tmp, "ffprobe.exe")))
            throw new InvalidDataException("The download didn't contain ffmpeg.");
        try { Directory.Delete(Paths.Ffmpeg, true); } catch { }
        Directory.Move(tmp, Paths.Ffmpeg);
    }

    static Task<string>? gpu;
    /// <summary>"auto" becomes the first GPU encoder that works here (NVIDIA "nvenc", then AMD "amf"), else "x264"</summary>
    public static Task<string> PickEncoder(string pref)
    {
        if (pref != "auto") return Task.FromResult(pref);
        // One shared check: a compress started while it's still running waits for the answer instead of assuming CPU
        return gpu ??= FindGpuEncoder();
    }

    static async Task<string> FindGpuEncoder()
    {
        foreach (var name in new[] { "nvenc", "amf" })
            // A tiny test encode is the only reliable check: ffmpeg lists both encoders whatever card is installed
            try
            {
                if ((await Run(Ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.2", "-c:v", "h264_" + name, "-f", "null", "-"], timeoutMs: 15000).ConfigureAwait(false)).Code == 0)
                    return name;
            }
            catch { }
        return "x264";
    }

    // ---------- process helper ----------

    internal static async Task<(int Code, byte[] Stdout, string Stderr)> Run(string exe, IEnumerable<string> args, CancellationToken ct = default, int timeoutMs = 0)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) cts.CancelAfter(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        await outTask.ConfigureAwait(false);
        return (p.ExitCode, stdout.ToArray(), await errTask.ConfigureAwait(false));
    }

    // ---------- probing ----------

    static double Num(JsonNode? n)
    {
        if (n == null) return 0;
        if (n.GetValueKind() == JsonValueKind.Number) return n.GetValue<double>();
        return double.TryParse(n.ToString(), NumberStyles.Float, Inv, out var d) ? d : 0;
    }

    static string Str(JsonNode? n) => n?.ToString() ?? "";

    static double Rate(string r)
    {
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out var a) && double.TryParse(parts[1], NumberStyles.Float, Inv, out var b) && a > 0 && b > 0) return a / b;
        return 0;
    }

    public static async Task<ProbeResult> Probe(string path)
    {
        var (code, stdout, _) = await Run(Ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]);
        if (code != 0) throw new Exception("ffprobe could not read this file. Is it a video?");
        var root = JsonNode.Parse(stdout)!;
        var streams = root["streams"]?.AsArray().Where(s => s != null).Select(s => s!).ToList() ?? [];
        var v = streams.FirstOrDefault(s => Str(s["codec_type"]) == "video" && Num(s["disposition"]?["attached_pic"]) != 1)
            ?? throw new Exception("No video stream found in this file.");

        var fps = Rate(Str(v["avg_frame_rate"]));
        if (fps <= 0) fps = Rate(Str(v["r_frame_rate"]));
        if (fps <= 0) fps = 30;

        // Phone videos store rotation as metadata; swap so width/height match what you see
        var rotNode = v["tags"]?["rotate"] ?? v["side_data_list"]?.AsArray().FirstOrDefault(s => s?["rotation"] != null)?["rotation"];
        var rot = Math.Abs(Num(rotNode));
        var swap = rot == 90 || rot == 270;
        int w = (int)Num(v["width"]), h = (int)Num(v["height"]);

        var audio = streams.Where(s => Str(s["codec_type"]) == "audio").Select((s, i) =>
        {
            // OBS writes its track names ("Game", "Mic") to "name"; other tools use "title", and remuxes can leave it in
            // "handler_name". Skip what's only the name of the tool that wrote the file ("SoundHandler").
            string[] toolNames = ["handler", "mainconcept", "iso media"];
            var title = new[] { "title", "name", "handler_name" }.Select(k => Str(s["tags"]?[k]).Trim())
                .FirstOrDefault(v => v != "" && !toolNames.Any(tool => v.Contains(tool, StringComparison.OrdinalIgnoreCase))) ?? "";
            var lang = Str(s["tags"]?["language"]);
            return new AudioTrack(i, Str(s["codec_name"]), (int)Num(s["channels"]), Str(s["channel_layout"]), title.Trim(), lang == "und" ? "" : lang, Num(s["disposition"]?["default"]) == 1);
        }).ToList();

        var duration = Num(root["format"]?["duration"]);
        if (duration <= 0) duration = Num(v["duration"]);
        return new ProbeResult(duration, swap ? h : w, swap ? w : h, fps, (long)Num(root["format"]?["bit_rate"]), (long)Num(v["bit_rate"]), Str(v["codec_name"]), audio);
    }

    // ---------- frames ----------

    public static async Task<Image?> Frame(string path, double time, int height, CancellationToken ct = default)
    {
        try
        {
            var (code, stdout, _) = await Run(Ffmpeg, [
                "-hide_banner", "-loglevel", "error", "-ss", F3(time), "-i", path,
                "-frames:v", "1", "-vf", $"scale=-2:{height}", "-q:v", "4", "-f", "image2pipe", "-c:v", "mjpeg", "-"
            ], ct, 20000);
            if (code != 0 || stdout.Length == 0) return null;
            using var ms = new MemoryStream(stdout);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    /// <summary>Evenly spaced frames for the timeline, 4 at a time; each one is reported as soon as it's ready.</summary>
    /// <param name="start">where the stretch of video begins</param>
    /// <param name="duration">how long the stretch is</param>
    public static async Task Thumbnails(string path, double start, double duration, int count, int height, Action<int, Image> onFrame, CancellationToken ct)
    {
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < count && !ct.IsCancellationRequested)
            {
                var img = await Frame(path, start + (i + 0.5) * duration / count, height, ct);
                if (img != null) onFrame(i, img);
            }
        })));
    }

    // ---------- encoding ----------

    /// <summary>Loudest sample (0..1) in each of `buckets` equal slices of an audio track, for drawing its waveform</summary>
    public static async Task<float[]?> Peaks(string path, int track, int buckets, CancellationToken ct)
    {
        var (code, pcm, _) = await Run(Ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", path, "-map", $"0:a:{track}", "-ac", "1", "-ar", "4000", "-f", "s16le", "-"], ct).ConfigureAwait(false);
        var n = pcm.Length / 2;
        if (code != 0 || n == 0) return null;
        var peaks = new float[buckets];
        for (var i = 0; i < n; i++)
        {
            var v = Math.Abs((float)BitConverter.ToInt16(pcm, i * 2)) / 32768f;
            var b = (int)((long)i * buckets / n);
            if (v > peaks[b]) peaks[b] = v;
        }
        return peaks;
    }

    /// <summary>
    /// ffmpeg expression for a value that moves in straight lines between points over time (and holds before the
    /// first and after the last). Seeking with -ss restarts the filter clock at 0, so the offset is added back
    /// to get source time.
    /// </summary>
    static string Ramp(IReadOnlyList<(double T, double V)> k, double offset)
    {
        var time = $"(t+{F3(offset)})";
        var expr = F3(k[^1].V);
        for (var i = k.Count - 1; i >= 1; i--)
        {
            var (a, b) = (k[i - 1], k[i]);
            var ramp = b.T - a.T < 0.001 ? F3(b.V) : $"{F3(a.V)}+({F3(b.V - a.V)})*({time}-{F3(a.T)})/{F3(b.T - a.T)}";
            expr = $"if(lt({time},{F3(b.T)}),{ramp},{expr})";
        }
        return $"if(lt({time},{F3(k[0].T)}),{F3(k[0].V)},{expr})";
    }

    /// <summary>ffmpeg volume filter for a track: a plain number, or a ramp between its points</summary>
    static string VolumeFilter(TrackMix t, double offset) =>
        t.Keys is { Count: > 0 } k ? $"volume='{Ramp(k, offset)}':eval=frame" : $"volume={F3(t.Volume)}";

    /// <summary>
    /// Crop (and scale) filters. A box that doesn't change is a plain crop. A moving or zooming one can't be, because
    /// ffmpeg's crop can't change size mid-video: instead each frame is scaled so the box comes out at the output
    /// size, and a fixed-size crop then follows the box around.
    /// </summary>
    static IEnumerable<string> CropFilters(EncodeRequest o)
    {
        var c = o.Crop!;
        // Output size: the smallest box, times the chosen scale, kept even for H.264
        var f = o.ScaleShortSide is { } s ? s / (double)Math.Min(o.CropW, o.CropH) : 1;
        int outW = Math.Max(2, (int)Math.Round(o.CropW * f) & ~1), outH = Math.Max(2, (int)Math.Round(o.CropH * f) & ~1);
        if (c.All(k => Math.Abs(k.X - c[0].X) < 0.5 && Math.Abs(k.Y - c[0].Y) < 0.5 && Math.Abs(k.W - c[0].W) < 0.5))
        {
            yield return $"crop={o.CropW}:{o.CropH}:{(int)c[0].X}:{(int)c[0].Y}";
            if (f != 1) yield return $"scale={outW}:{outH}";
            yield break;
        }
        string w = Ramp(c.Select(k => (k.T, k.W)).ToList(), o.Start), x = Ramp(c.Select(k => (k.T, k.X)).ToList(), o.Start), y = Ramp(c.Select(k => (k.T, k.Y)).ToList(), o.Start);
        // Rounded up to even so the scaled frame is never a pixel smaller than the crop taken from it
        yield return $"scale=w='ceil(iw*{outW}/({w})/2)*2':h='ceil(ih*{outW}/({w})/2)*2':eval=frame";
        yield return $"crop={outW}:{outH}:x='({x})*{outW}/({w})':y='({y})*{outW}/({w})'";
    }

    /// <param name="offset">source time the input was seeked to</param>
    /// <param name="master">volume applied to the finished mix; null = leave it</param>
    internal static string? AudioGraph(List<TrackMix> tracks, double offset, TrackMix? master = null)
    {
        if (tracks.Count == 0) return null;
        var after = master != null && (master.Keys is { Count: > 0 } || Math.Abs(master.Volume - 1) > 0.001) ? "," + VolumeFilter(master, offset) : "";
        var parts = tracks.Select((t, i) => $"[0:a:{t.Index}]{VolumeFilter(t, offset)},aformat=channel_layouts=stereo[a{i}]").ToList();
        if (tracks.Count == 1) return parts[0][..^4] + after + "[aout]";
        return $"{string.Join(";", parts)};{string.Concat(tracks.Select((_, i) => $"[a{i}]"))}amix=inputs={tracks.Count}:normalize=0:duration=longest{after}[aout]";
    }

    static async Task RunEncode(List<string> args, double duration, double from, double span, string stage, IProgress<(string Stage, double Progress)> progress, CancellationToken ct)
    {
        progress.Report((stage, from));
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && duration > 0)
                progress.Report((stage, from + Math.Clamp(us / 1e6 / duration, 0, 1) * span));
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (tail) { tail.Enqueue(e.Data); if (tail.Count > 4) tail.Dequeue(); }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        if (p.ExitCode != 0) lock (tail) throw new Exception($"ffmpeg failed (code {p.ExitCode}): {string.Join(" | ", tail.TakeLast(3))}");
    }

    /// <summary>Encodes, retrying at a lower bitrate up to 3 times if the result overshoots. Returns the temp output path.</summary>
    public static async Task<(string Path, long Size)> Encode(EncodeRequest o, IProgress<(string Stage, double Progress)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.Work);
        var encoder = await PickEncoder(o.Encoder);
        var id = Guid.NewGuid().ToString("N");
        var output = Path.Combine(Paths.Work, $"{id}-out.mp4");
        var passlog = Path.Combine(Paths.Work, $"{id}-pass");

        var vf = new List<string>();
        if (o.Crop is { Count: > 0 }) vf.AddRange(CropFilters(o));
        else if (o.ScaleShortSide is { } s) vf.Add(o.Portrait ? $"scale={s}:-2" : $"scale=-2:{s}");
        if (o.Fps is { } f) vf.Add($"fps={f}");
        vf.Add("format=yuv420p");
        var audio = o.AudioKbps > 0 ? AudioGraph(o.Tracks, o.Start, o.Master) : null;
        var videoGraph = $"[0:v:0]{string.Join(",", vf)}[vout]";
        var graph = videoGraph + (audio != null ? $";{audio}" : "");

        string[] inputArgs = ["-ss", F3(o.Start), "-i", o.Input, "-t", F3(o.Duration)];
        string[] common = ["-filter_complex", graph, "-map", "[vout]", "-map_metadata", "-1"];
        // x264 pass 1 writes no audio, and ffmpeg rejects a graph with an unconnected [aout].
        // The pass options are video-only (":v"): newer ffmpeg otherwise looks for a pass log for the audio too and fails.
        string[] videoOnly = ["-filter_complex", videoGraph, "-map", "[vout]", "-map_metadata", "-1"];
        string[] audioOut = audio != null ? ["-map", "[aout]", "-c:a", "aac", "-b:a", $"{o.AudioKbps}k", "-ac", "2"] : ["-an"];

        try
        {
            var videoKbps = o.VideoKbps;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var t0 = Stopwatch.StartNew();
                string[] rate = ["-b:v", $"{videoKbps}k", "-maxrate", $"{Math.Round(videoKbps * 1.5)}k", "-bufsize", $"{videoKbps * 2}k"];
                var label = attempt > 0 ? $" (retry {attempt})" : "";

                if (encoder == "nvenc")
                {
                    await RunEncode([.. inputArgs, .. common, "-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres", .. rate, .. audioOut, "-movflags", "+faststart", output],
                        o.Duration, 0, 1, $"Compressing on GPU{label}", progress, ct);
                }
                else if (encoder == "amf")
                {
                    await RunEncode([.. inputArgs, .. common, "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "quality", "-rc", "vbr_peak", .. rate, .. audioOut, "-movflags", "+faststart", output],
                        o.Duration, 0, 1, $"Compressing on GPU{label}", progress, ct);
                }
                else
                {
                    await RunEncode([.. inputArgs, .. videoOnly, "-c:v", "libx264", "-preset", "medium", .. rate, "-pass:v", "1", "-passlogfile:v", passlog, "-an", "-f", "mp4", "NUL"],
                        o.Duration, 0, 0.5, $"Analyzing (pass 1 of 2){label}", progress, ct);
                    await RunEncode([.. inputArgs, .. common, "-c:v", "libx264", "-preset", "medium", .. rate, "-pass:v", "2", "-passlogfile:v", passlog, .. audioOut, "-movflags", "+faststart", output],
                        o.Duration, 0.5, 0.5, $"Compressing (pass 2 of 2){label}", progress, ct);
                }

                var size = new FileInfo(output).Length;
                var predicted = (videoKbps + (audio != null ? o.AudioKbps : 0)) * 125.0 * o.Duration;
                Log.Append(new Dictionary<string, object?>
                {
                    ["kind"] = "encode",
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["encoder"] = encoder,
                    ["attempt"] = attempt,
                    ["duration"] = o.Duration,
                    ["outputShortSide"] = o.ScaleShortSide,
                    ["fps"] = o.Fps,
                    ["videoKbps"] = videoKbps,
                    ["audioKbps"] = audio != null ? o.AudioKbps : 0,
                    ["audioTracks"] = o.Tracks.Count,
                    ["targetBytes"] = o.TargetBytes,
                    ["predictedBytes"] = Math.Round(predicted),
                    ["actualBytes"] = size,
                    ["ratio"] = Math.Round(size / predicted, 4),
                    ["fits"] = size <= o.TargetBytes,
                    ["cappedBySource"] = o.CappedBySource,
                    ["calibration"] = o.Calibration,
                    ["seconds"] = Math.Round(t0.Elapsed.TotalSeconds, 1),
                });

                if (size <= o.TargetBytes) return (output, size);
                videoKbps = Math.Max(50, (int)Math.Floor(videoKbps * ((double)o.TargetBytes / size) * 0.92));
            }
            throw new Exception("Couldn't get the video under the size limit. Try trimming it shorter.");
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }
        finally
        {
            foreach (var f2 in Directory.EnumerateFiles(Paths.Work, $"{id}-pass*")) try { File.Delete(f2); } catch { }
        }
    }

    /// <summary>Moves a finished encode to the output folder under a readable, unique name.</summary>
    public static string MoveToOut(string temp, string name)
    {
        Directory.CreateDirectory(Paths.Out);
        var baseName = Path.GetFileNameWithoutExtension(name);
        var dest = Path.Combine(Paths.Out, name);
        for (var n = 2; File.Exists(dest); n++) dest = Path.Combine(Paths.Out, $"{baseName} ({n}){Path.GetExtension(name)}");
        File.Move(temp, dest);
        return dest;
    }
}

/// <summary>Every encode is logged so the planner learns how far each encoder lands from its target.</summary>
static class Log
{
    public static void Append(Dictionary<string, object?> entry)
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.AppendAllText(Paths.Log, JsonSerializer.Serialize(entry) + "\n");
        }
        catch { }
    }

    /// <summary>Median actual/predicted size ratio per encoder, from the 20 latest uncapped first attempts.</summary>
    public static Dictionary<string, double> Calibration()
    {
        var result = new Dictionary<string, double>();
        try
        {
            if (!File.Exists(Paths.Log)) return result;
            var entries = new List<(string Encoder, double Ratio)>();
            foreach (var line in File.ReadLines(Paths.Log))
            {
                try
                {
                    var e = JsonNode.Parse(line);
                    if (e?["kind"]?.ToString() != "encode" || e["attempt"]?.GetValue<int>() != 0 || e["cappedBySource"]?.GetValue<bool>() == true) continue;
                    entries.Add((e["encoder"]!.ToString(), e["ratio"]!.GetValue<double>()));
                }
                catch { }
            }
            foreach (var g in entries.GroupBy(e => e.Encoder))
            {
                var r = g.TakeLast(20).Select(e => e.Ratio).Order().ToList();
                result[g.Key] = r.Count % 2 == 1 ? r[r.Count / 2] : (r[r.Count / 2 - 1] + r[r.Count / 2]) / 2;
            }
        }
        catch { }
        return result;
    }
}
