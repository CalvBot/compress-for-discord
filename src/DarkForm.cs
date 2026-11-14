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

    protected virtual void FitToContent()
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
        var l = new LinkLabel { Text = text, UseMnemonic = false, AutoSize = true, LinkColor = Theme.Link, ActiveLinkColor = Theme.Header, VisitedLinkColor = Theme.Link, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 0, 14, 0) };
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
            try
            {
                if (await Updater.Install(release)) Application.Exit();
                else link.Text = $"Update to {release.Version}";
            }
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

    static string? Dropped(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f && File.Exists(f[0]) ? f[0] : null;

    /// <summary>
    /// Accept a single dropped file anywhere on the window (child controls included). While one is dragged over,
    /// `hot` is told so the window can light up its own drop target; without one, the window is covered with a
    /// "drop to open" panel that takes the drop.
    /// </summary>
    protected void AcceptDrop(Action<string> onFile, Action<bool>? hot = null)
    {
        DropOverlay? overlay = null;
        void Enter(DragEventArgs e)
        {
            var ok = Dropped(e) != null;
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (!ok) return;
            if (hot != null) { hot(true); return; }
            overlay ??= new DropOverlay(this, onFile);
            overlay.Cover();
        }
        // Moving between child controls leaves one and enters the next: only off the window counts as leaving
        void Leave() => BeginInvoke(() => { if (!ClientRectangle.Contains(PointToClient(MousePosition))) hot?.Invoke(false); });
        void Hook(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => Enter(e);
            c.DragLeave += (_, _) => Leave();
            c.DragDrop += (_, e) => { hot?.Invoke(false); if (Dropped(e) is { } f) BeginInvoke(() => onFile(f)); };
            foreach (Control child in c.Controls) Hook(child);
        }
        Hook(this);
#if DEBUG
        if (hot == null && (Environment.GetEnvironmentVariable("CFD_TEST") ?? "").Contains("dropoverlay"))
            Shown += (_, _) => BeginInvoke(() => { overlay ??= new DropOverlay(this, onFile); overlay.Cover(); });
#endif
    }

    /// <summary>Covers a window while a video is dragged over it: a dashed brand-blue target that takes the drop</summary>
    sealed class DropOverlay : Form
    {
        readonly Form owner;

        public DropOverlay(Form owner, Action<string> onFile)
        {
            this.owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg3;
            Opacity = 0.93;
            AllowDrop = true;
            Owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DragEnter += (_, e) => e.Effect = Dropped(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            DragLeave += (_, _) => Hide();
            DragDrop += (_, e) =>
            {
                Hide();
                if (Dropped(e) is { } f) owner.BeginInvoke(() => onFile(f));
            };
            owner.FormClosed += (_, _) => Dispose();
        }

        protected override bool ShowWithoutActivation => true;

        public void Cover()
        {
            Bounds = owner.RectangleToScreen(owner.ClientRectangle);
            if (!Visible) Show(owner);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg3);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var px = DeviceDpi / 96f;
            var inset = 16 * px;
            using var path = Theme.Round(new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset), 12 * px);
            using (var fill = new SolidBrush(Theme.Blend(Theme.Bg3, Theme.Brand, 0.12))) g.FillPath(fill, path);
            using (var pen = new Pen(Theme.Brand, 2.5f * px) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawPath(pen, path);
            using var font = new Font("Segoe UI Semibold", 14f);
            TextRenderer.DrawText(g, "Drop to open this video", font, ClientRectangle, Theme.Header, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
