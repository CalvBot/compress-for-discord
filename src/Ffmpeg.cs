using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompressForDiscord;

sealed record AudioTrack(int Index, string Codec, int Channels, string Layout, string Title, string Language, bool IsDefault)
{
    public string Label
    {
        get
        {
            var layout = Layout != "" ? Layout : Channels == 1 ? "mono" : Channels == 2 ? "stereo" : $"{Channels} ch";
            return string.Join(" · ", new[] { $"Track {Index + 1}", Title, layout, Language }.Where(s => s != ""));
        }
    }
}

sealed record ProbeResult(double Duration, int Width, int Height, double Fps, long Bitrate, long VideoBitrate, string VideoCodec, List<AudioTrack> AudioTracks);

sealed record TrackMix(int Index, double Volume);

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
    public bool CappedBySource;
    public double Calibration;
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

    static string? gpu;
    /// <summary>"auto" becomes the first GPU encoder that works here (NVIDIA "nvenc", then AMD "amf"), else "x264"</summary>
    public static async Task<string> PickEncoder(string pref)
    {
        if (pref != "auto") return pref;
        if (gpu == null)
        {
            gpu = "x264";
            foreach (var name in new[] { "nvenc", "amf" })
                // A tiny test encode is the only reliable check: ffmpeg lists both encoders whatever card is installed
                if ((await Run(Ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.2", "-c:v", "h264_" + name, "-f", "null", "-"], timeoutMs: 15000).ConfigureAwait(false)).Code == 0)
                { gpu = name; break; }
        }
        return gpu;
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
            var title = Str(s["tags"]?["title"] ?? s["tags"]?["handler_name"]);
            // Container tool names ("SoundHandler") aren't real titles
            if (title.Contains("handler", StringComparison.OrdinalIgnoreCase)) title = "";
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
    public static async Task Thumbnails(string path, double duration, int count, int height, Action<int, Image> onFrame, CancellationToken ct)
    {
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < count && !ct.IsCancellationRequested)
            {
                var img = await Frame(path, (i + 0.5) * duration / count, height, ct);
                if (img != null) onFrame(i, img);
            }
        })));
    }

    // ---------- encoding ----------

    internal static string? AudioGraph(List<TrackMix> tracks)
    {
        if (tracks.Count == 0) return null;
        var parts = tracks.Select((t, i) => $"[0:a:{t.Index}]volume={t.Volume.ToString("0.000", Inv)},aformat=channel_layouts=stereo[a{i}]").ToList();
        if (tracks.Count == 1) return parts[0][..^4] + "[aout]";
        return $"{string.Join(";", parts)};{string.Concat(tracks.Select((_, i) => $"[a{i}]"))}amix=inputs={tracks.Count}:normalize=0:duration=longest[aout]";
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
        if (o.ScaleShortSide is { } s) vf.Add(o.Portrait ? $"scale={s}:-2" : $"scale=-2:{s}");
        if (o.Fps is { } f) vf.Add($"fps={f}");
        vf.Add("format=yuv420p");
        var audio = o.AudioKbps > 0 ? AudioGraph(o.Tracks) : null;
        var videoGraph = $"[0:v:0]{string.Join(",", vf)}[vout]";
        var graph = videoGraph + (audio != null ? $";{audio}" : "");

        string[] inputArgs = ["-ss", F3(o.Start), "-i", o.Input, "-t", F3(o.Duration)];
        string[] common = ["-filter_complex", graph, "-map", "[vout]", "-map_metadata", "-1"];
        // x264 pass 1 writes no audio, and ffmpeg rejects a graph with an unconnected [aout]
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
                    await RunEncode([.. inputArgs, .. videoOnly, "-c:v", "libx264", "-preset", "medium", .. rate, "-pass", "1", "-passlogfile", passlog, "-an", "-f", "mp4", "NUL"],
                        o.Duration, 0, 0.5, $"Analyzing (pass 1 of 2){label}", progress, ct);
                    await RunEncode([.. inputArgs, .. common, "-c:v", "libx264", "-preset", "medium", .. rate, "-pass", "2", "-passlogfile", passlog, .. audioOut, "-movflags", "+faststart", output],
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
        for (var n = 2; File.Exists(dest); n++) dest = Path.Combine(Paths.Out, $"{baseName} ({n}).mp4");
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
