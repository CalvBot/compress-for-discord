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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Right-click passes the video; the Start menu / desktop shortcut passes nothing
        if (args.FirstOrDefault(File.Exists) is { } file) OpenVideo(file);
        else Show(new HomeForm());
        Application.Run(app);
    }

    /// <summary>Opens the compressor for a video, closing the window it was opened from</summary>
    public static void OpenVideo(string file, Form? from = null)
    {
        Show(new MainForm(Path.GetFullPath(file)));
        from?.Close();
    }

    /// <summary>The app exits once its last window closes</summary>
    static void Show(Form f)
    {
        openWindows++;
        f.FormClosed += (_, _) => { if (--openWindows == 0) app.ExitThread(); };
        f.Show();
    }
}
