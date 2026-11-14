namespace CompressForDiscord;

static class Program
{
    public const string VideoFilter = "Videos|*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.flv;*.ts;*.mts;*.m2ts;*.3gp;*.mpg;*.mpeg|All files|*.*";

    static readonly ApplicationContext app = new();
    static int openWindows;

    [STAThread]
    static void Main(string[] args)
    {
        // Run by the uninstaller
        if (args.Contains("--remove-downloads")) { Paths.RemoveDownloads(); return; }

        AppLog.Info($"Started v{Updater.Current} ({Environment.OSVersion.VersionString}, .NET {Environment.Version}), args: {string.Join(" ", args)}");
        // Anything that would otherwise vanish or crash silently goes in the log first
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("Unhandled UI error", e.Exception);
            MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nDetails are in {Paths.AppLog}", "Compress for Discord", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled error, closing", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("Background task error", e.Exception); e.SetObserved(); };
        Application.ApplicationExit += (_, _) => AppLog.Info("Exited");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Right-click passes the video; the Start menu / desktop shortcut passes nothing
        if (args.FirstOrDefault(File.Exists) is { } file) OpenVideo(file);
        else Show(new HomeForm());
        Application.Run(app);
    }

    /// <summary>Opens the compressor for a video (set up as a saved project, if given), closing the window it was opened from</summary>
    public static void OpenVideo(string file, Form? from = null, Project? project = null)
    {
        Show(new MainForm(Path.GetFullPath(file), project));
        from?.Close();
    }

    /// <summary>The app exits once its last window closes</summary>
    static void Show(Form f)
    {
        openWindows++;
        AppLog.Info($"Opened {f.GetType().Name}");
        f.FormClosed += (_, e) =>
        {
            AppLog.Info($"Closed {f.GetType().Name} ({e.CloseReason})");
            if (--openWindows == 0) app.ExitThread();
        };
        f.Show();
    }
}
