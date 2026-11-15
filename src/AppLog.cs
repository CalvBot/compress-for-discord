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
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

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
                // Every right-click opens its own process, so another may be writing too: share the file, and
                // wait a moment rather than lose the line when it's busy
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                for (var attempt = 0; ; attempt++)
                    try
                    {
                        using var f = new FileStream(Paths.AppLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        f.Write(bytes);
                        break;
                    }
                    catch (IOException) when (attempt < 5) { Thread.Sleep(10); }
            }
            // To the debugger's output window too (the compiler leaves this call out of a release build)
            Debug.Write(line);
        }
        catch { }
    }
}
