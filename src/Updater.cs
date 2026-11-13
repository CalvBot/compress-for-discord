using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CompressForDiscord;

sealed record Release(Version Version, string PageUrl, string? MsiUrl);

/// <summary>Checks GitHub releases for a newer version and installs its MSI over this one.</summary>
static class Updater
{
    public const string Repo = "CalvBot/compress-for-discord";
    public const string RepoUrl = "https://github.com/" + Repo;

    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(1, 0, 0);

    static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CompressForDiscord/" + Current);
        return http;
    }

    /// <summary>The latest release if it's newer than this build, else null. Throws if GitHub can't be reached.</summary>
    public static async Task<Release?> Check()
    {
        using var http = Http();
        var json = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"))!;
        var tag = json["tag_name"]?.ToString().TrimStart('v', 'V') ?? "";
        if (!Version.TryParse(tag, out var latest) || latest <= Current) return null;
        var msi = json["assets"]?.AsArray().Select(a => a?["browser_download_url"]?.ToString())
            .FirstOrDefault(u => u != null && Ours(u, "/releases/download/") && u.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        var page = json["html_url"]?.ToString() is { } p && Ours(p, "/releases/") ? p : RepoUrl + "/releases";
        return new Release(latest, page, msi);
    }

    // Only ever download from or open this repo's own release pages, whatever the API response says
    static bool Ours(string url, string under) => url.StartsWith(RepoUrl + under, StringComparison.OrdinalIgnoreCase);

    /// <summary>Background check, at most once a day, when enabled in settings</summary>
    public static async Task<Release?> CheckDaily()
    {
        var cfg = AppConfig.Current;
        if (!cfg.CheckForUpdates || DateTime.UtcNow - cfg.LastUpdateCheck < TimeSpan.FromDays(1)) return null;
        try
        {
            var r = await Check();
            cfg.LastUpdateCheck = DateTime.UtcNow;
            cfg.Save();
            return r;
        }
        catch { return null; }
    }

    /// <summary>Downloads the new MSI, starts it and returns; the caller then exits so the files can be replaced.</summary>
    public static async Task Install(Release r)
    {
        if (r.MsiUrl == null) { Open(r.PageUrl); return; }
        Directory.CreateDirectory(Paths.Work);
        var msi = Path.Combine(Paths.Work, $"CompressForDiscord-{r.Version}.msi");
        using (var http = Http())
        {
            http.Timeout = TimeSpan.FromMinutes(5);
            await File.WriteAllBytesAsync(msi, await http.GetByteArrayAsync(r.MsiUrl));
        }
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /passive") { UseShellExecute = true });
    }

    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
