using System.Text.Json;

namespace CompressForDiscord;

/// <summary>Everything set up for one video, saved so it can be opened again after the window has closed</summary>
sealed class Project
{
    public string File { get; set; } = "";
    public DateTime Saved { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public double Current { get; set; }
    public double TargetMb { get; set; }
    public int? ScalePct { get; set; }
    public int? FrameRate { get; set; }
    public string CropMode { get; set; } = "src";
    public double CropAspect { get; set; }
    public bool Separate { get; set; }
    public bool TracksOpen { get; set; }
    public List<PosKey> Pos { get; set; } = [];
    public List<ValueKey> Scale { get; set; } = [];
    public List<TrackState> Tracks { get; set; } = [];
    public TrackState? Master { get; set; }
}

sealed record PosKey(double T, double X, double Y, Ease E);
sealed record ValueKey(double T, double V, Ease E);
sealed record TrackState(int Index, bool Active, double Volume, List<ValueKey> Keys);

/// <summary>
/// The last few projects, one file each, newest first. A video has at most one: saving it again replaces the
/// old one. Only as many as the setting says are kept.
/// </summary>
static partial class Projects
{
    static readonly string Folder = FindFolder();

    static string FindFolder()
    {
        var folder = Path.Combine(Paths.Data, "projects");
        TestFolder(ref folder);
        return folder;
    }

    /// <summary>For the test code in src\Debug (debug builds only), which keeps test runs off the real list; in a release build the call is dropped</summary>
    static partial void TestFolder(ref string folder);

    static bool Enabled => AppConfig.Current.RecentProjects > 0;

    public static List<(string Path, Project Project)> Recent()
    {
        var list = new List<(string, Project)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder, "*.json"))
                try { if (JsonSerializer.Deserialize<Project>(File.ReadAllText(f)) is { File.Length: > 0 } p) list.Add((f, p)); }
                catch { }
        }
        catch { }
        return list.OrderByDescending(x => x.Item2.Saved).ToList();
    }

    public static void Save(Project p)
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(Folder);
            p.Saved = DateTime.Now;
            var all = Recent();
            foreach (var (path, old) in all.Where(x => string.Equals(x.Project.File, p.File, StringComparison.OrdinalIgnoreCase)))
                Delete(path);
            File.WriteAllText(Path.Combine(Folder, $"{p.Saved:yyyyMMdd-HHmmss-fff}.json"), JsonSerializer.Serialize(p));
            Trim();
        }
        catch { }
    }

    /// <summary>Points a saved project at another video (found where it moved to, or a different one), keeping its place in the list</summary>
    public static void Relink(string path, Project p, string video)
    {
        try
        {
            p.File = Path.GetFullPath(video);
            foreach (var (other, _) in Recent().Where(x => x.Path != path && string.Equals(x.Project.File, p.File, StringComparison.OrdinalIgnoreCase)))
                Delete(other);
            File.WriteAllText(path, JsonSerializer.Serialize(p));
            // They were of the old video
            try { File.Delete(ThumbPath(path)); } catch { }
        }
        catch { }
    }

    public static void Remove(string path) => Delete(path);

    /// <summary>Drops all but the newest ones the setting keeps</summary>
    public static void Trim()
    {
        foreach (var (path, _) in Recent().Skip(AppConfig.Current.RecentProjects))
            Delete(path);
    }

    public static void Clear()
    {
        foreach (var (path, _) in Recent())
            Delete(path);
    }

    /// <summary>The project's thumbnails (<see cref="ProjectThumbs"/>), saved beside it</summary>
    public static string ThumbPath(string path) => Path.ChangeExtension(path, ".jpg");

    /// <summary>A project file and its thumbnails</summary>
    static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(ThumbPath(path)); } catch { }
    }

    /// <summary>"Today 14:05", "Yesterday 09:12", "12 Nov 18:40"</summary>
    public static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"Today {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {t:HH:mm}" : $"{t:d MMM HH:mm}";
}
