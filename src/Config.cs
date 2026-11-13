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

    /// <summary>Run by the uninstaller: removes the downloaded ffmpeg and temp files (settings are kept)</summary>
    public static void RemoveDownloads()
    {
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

    /// <summary>Last chosen size limit, remembered between runs</summary>
    public double TargetMb { get; set; } = 10;
    /// <summary>auto (GPU if there is one), x264, or a specific GPU encoder: nvenc, amf</summary>
    public string Encoder { get; set; } = "auto";
    /// <summary>Folder with ffmpeg.exe and ffprobe.exe; empty = find automatically</summary>
    public string FfmpegPath { get; set; } = "";
    /// <summary>Aim this many percent under the limit</summary>
    public double SafetyMarginPercent { get; set; } = 3;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Chime when a compress finishes</summary>
    public bool PlaySound { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>Folder last saved to with "Compress & save"</summary>
    public string SaveFolder { get; set; } = "";

    static AppConfig Load()
    {
        try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.Config)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Paths.Config, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
