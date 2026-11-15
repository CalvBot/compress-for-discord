using System.Text.Json;

namespace CompressForDiscord;

static class Paths
{
    public static readonly string Work = Path.Combine(Path.GetTempPath(), "compress-for-discord");
    /// <summary>Copied files stay here so the clipboard keeps pointing at something. Only the newest is kept long term.</summary>
    public static readonly string Out = Path.Combine(Work, "out");
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CompressForDiscord");
    public static readonly string Config = Path.Combine(Data, "config.json");
    public static readonly string Log = Path.Combine(Data, "log.jsonl");
    /// <summary>What the app did, in plain text, for debugging: opens, ffmpeg runs, errors, window closes</summary>
    public static readonly string AppLog = Path.Combine(Data, "app.log");
    /// <summary>Machine-local data (kept out of the roaming profile): the in-app ffmpeg download</summary>
    public static readonly string LocalData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompressForDiscord");
    public static readonly string Ffmpeg = Path.Combine(LocalData, "ffmpeg");

    /// <summary>
    /// Runs each time the app opens. Leftovers from cancelled or crashed encodes go after 6 hours. Copied files
    /// go after an hour, which leaves time to paste and send them, except the newest one, which may still be on
    /// the clipboard (it goes after 3 days).
    /// </summary>
    public static void CleanupStale()
    {
        RemoveOlderThan(Work, TimeSpan.FromHours(6));
        try
        {
            var copies = new DirectoryInfo(Out).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            for (var i = 0; i < copies.Count; i++)
                if (copies[i].LastWriteTimeUtc < DateTime.UtcNow - (i == 0 ? TimeSpan.FromDays(3) : TimeSpan.FromHours(1)))
                    try { copies[i].Delete(); } catch { }
        }
        catch { }
    }

    /// <summary>Run by the uninstaller: removes the downloaded ffmpeg, temp files and the desktop shortcut (settings are kept)</summary>
    public static void RemoveDownloads()
    {
        try { Shortcut.SetOnDesktop(false); } catch { }
        try { Directory.Delete(LocalData, true); } catch { }
        try { Directory.Delete(Work, true); } catch { }
    }

    static void RemoveOlderThan(string dir, TimeSpan age)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                if (f.LastWriteTimeUtc < DateTime.UtcNow - age) try { f.Delete(); } catch { }
        }
        catch { }
    }
}

/// <summary>Settings, shared by every window and saved on each change</summary>
sealed class AppConfig
{
    public static AppConfig Current { get; } = Load();

    /// <summary>Which one-time changes to older settings have been made (see Load)</summary>
    public int Version { get; set; }

    /// <summary>Last chosen size limit, remembered between runs</summary>
    public double TargetMb { get; set; } = 20;
    /// <summary>auto (GPU if there is one), x264, or a specific GPU encoder: nvenc, amf</summary>
    public string Encoder { get; set; } = "auto";
    /// <summary>Folder with ffmpeg.exe and ffprobe.exe; empty = find automatically</summary>
    public string FfmpegPath { get; set; } = "";
    /// <summary>Aim this many percent under the limit</summary>
    public double SafetyMarginPercent { get; set; } = 3;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Chime when a compress finishes</summary>
    public bool PlaySound { get; set; } = true;
    /// <summary>The playhead only stops on frames of the chosen frame rate</summary>
    public bool FrameLock { get; set; } = true;
    /// <summary>Waveforms drawn mirrored about the middle of their lane rather than up from the bottom</summary>
    public bool MirroredWaves { get; set; }
    /// <summary>How many recent projects are kept to open again; 0 = none are saved</summary>
    public int RecentProjects { get; set; } = 10;
    /// <summary>How many changes Ctrl+Z can take back</summary>
    public int UndoSteps { get; set; } = 100;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>Folder last saved to with "Compress & save"</summary>
    public string SaveFolder { get; set; } = "";

    static AppConfig Load()
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.Config)) ?? new();
            // 10 was the free-account chip before Discord raised that limit to 20. Moved up once only: a 10 typed
            // in since then is what was asked for.
            if (cfg.Version < 1 && cfg.TargetMb == 10) cfg.TargetMb = 20;
            cfg.Version = 1;
            // A file edited by hand can hold anything
            if (!(cfg.TargetMb > 0) || !double.IsFinite(cfg.TargetMb)) cfg.TargetMb = 20;
            if (!(cfg.SafetyMarginPercent >= 0 && cfg.SafetyMarginPercent < 90)) cfg.SafetyMarginPercent = 3;
            cfg.Encoder ??= "auto";
            cfg.FfmpegPath ??= "";
            cfg.SaveFolder ??= "";
            cfg.UndoSteps = Math.Clamp(cfg.UndoSteps, 1, 100);
            cfg.RecentProjects = Math.Max(0, cfg.RecentProjects);
            return cfg;
        }
        catch (FileNotFoundException) { return new() { Version = 1 }; }
        catch (DirectoryNotFoundException) { return new() { Version = 1 }; }
        catch (Exception ex)
        {
            // Unreadable (a typo from editing it by hand): start from the defaults, but keep the file to look at
            AppLog.Warn($"Couldn't read the settings, using the defaults: {ex.Message}");
            try { File.Copy(Paths.Config, Paths.Config + ".bad", true); } catch { }
            return new() { Version = 1 };
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            // Written beside it and swapped in, so another window opening just now never reads half a file
            var tmp = $"{Paths.Config}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Paths.Config, true);
        }
        catch { }
    }
}
