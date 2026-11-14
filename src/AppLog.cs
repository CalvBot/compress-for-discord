using System.Diagnostics;

namespace CompressForDiscord;

/// <summary>
/// Plain-text debug log at %AppData%\CompressForDiscord\app.log: what the app did and what went wrong, one
/// timestamped line each. When it passes 1 MB it's moved to app.old.log, so at most about 2 MB is kept.
/// Never throws: logging must not break the app.
/// </summary>
static class AppLog
{
    static readonly object gate = new();
    static readonly int pid = Environment.ProcessId;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>An ffmpeg command line as it could be pasted into a terminal</summary>
    public static string Command(string exe, IEnumerable<string> args) =>
        string.Join(" ", new[] { Path.GetFileName(exe) }.Concat(args.Select(a => a.Length == 0 || a.Any(c => c is ' ' or '"' or ';' or ',' or '[') ? $"\"{a.Replace("\"", "\\\"")}\"" : a)));

    static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{pid}] {level,-5} {message.Replace("\r", "").Replace("\n", "\n    ")}\n";
            lock (gate)
            {
                Directory.CreateDirectory(Paths.Data);
                var info = new FileInfo(Paths.AppLog);
                if (info.Exists && info.Length > 1024 * 1024)
                    File.Move(Paths.AppLog, Path.Combine(Paths.Data, "app.old.log"), true);
                File.AppendAllText(Paths.AppLog, line);
            }
#if DEBUG
            Debug.Write(line);
#endif
        }
        catch { }
    }
}
