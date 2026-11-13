using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>Base for every window: Discord colors, dark title bar, app icon, DPI-aware auto-size.</summary>
class DarkForm : Form
{
    public DarkForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Icon = AppIcon;
    }

    static Icon? appIcon;
    public static Icon? AppIcon
    {
        get
        {
            if (appIcon == null)
                try { appIcon = new Icon(typeof(DarkForm).Assembly.GetManifestResourceStream("app.ico")!); } catch { }
            return appIcon;
        }
    }

    /// <summary>
    /// Fit the window to its content panel with the same gap on every side. Done by hand because
    /// Form.AutoSize misjudges after DPI changes and when content is added after the window shows.
    /// </summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        FitToContent();
    }

    // Windows resizes the window itself when it moves to a screen with different scaling; refit afterwards
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(() => { PerformLayout(); FitToContent(); });
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitToContent();
    }

    void FitToContent()
    {
        if (Controls.Count == 0 || WindowState == FormWindowState.Minimized) return;
        var c = Controls[0];
        var size = new Size(c.Right + c.Left, c.Bottom + c.Top);
        if (ClientSize != size) ClientSize = size;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, 4); // immersive dark mode
        var caption = Theme.Bg3.R | Theme.Bg3.G << 8 | Theme.Bg3.B << 16;
        DwmSetWindowAttribute(Handle, 35, ref caption, 4); // caption color
    }

    // ---------- small builders shared by the windows ----------

    public static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Theme.Header, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 2) };

    public static Label Muted(string text, int maxWidth = 0) =>
        new() { Text = text, UseMnemonic = false, AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(maxWidth, 0), Margin = new Padding(0, 0, 0, 8) };

    public static LinkLabel Link(string text, Action onClick)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, LinkColor = Theme.Link, ActiveLinkColor = Theme.Header, VisitedLinkColor = Theme.Link, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 0, 14, 0) };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    /// <summary>"Update to vX" link, hidden until the daily background check finds a newer release</summary>
    public static LinkLabel UpdateLink(Form owner)
    {
        Release? release = null;
        LinkLabel link = null!;
        link = Link("", async () =>
        {
            if (release == null) return;
            link.Text = "Downloading update…";
            try { await Updater.Install(release); Application.Exit(); }
            catch { link.Text = "Update failed, try Settings"; }
        });
        link.Visible = false;
        link.LinkColor = link.VisitedLinkColor = Theme.Green;
        owner.Shown += async (_, _) =>
        {
            release = await Updater.CheckDaily();
            if (release != null && !link.IsDisposed) { link.Text = $"Update to {release.Version}"; link.Visible = true; }
        };
        return link;
    }

    /// <summary>Accept a single dropped file anywhere on the window (child controls included)</summary>
    protected void AcceptDrop(Action<string> onFile)
    {
        static string? Dropped(DragEventArgs e) =>
            e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f && File.Exists(f[0]) ? f[0] : null;
        void Hook(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => e.Effect = Dropped(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) => { if (Dropped(e) is { } f) BeginInvoke(() => onFile(f)); };
            foreach (Control child in c.Controls) Hook(child);
        }
        Hook(this);
    }
}
