using System.Drawing.Drawing2D;

namespace CompressForDiscord;

/// <summary>Shown when the app is opened from the Start menu or desktop instead of a right-click</summary>
sealed class HomeForm : DarkForm
{
    const int W = 460;

    public HomeForm()
    {
        Text = "Compress for Discord";

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(20, 14), Margin = Padding.Empty };
        // Top bar: title on the left, update / Settings / Help on the right
        var top = new Panel { Size = new Size(W, 36), Margin = Padding.Empty };
        var title = Heading("Compress for Discord");
        title.Dock = DockStyle.Left;
        // Same icon buttons as the main window
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right, Padding = new Padding(8, 0, 0, 0) };
        var update = UpdateLink(this);
        update.Margin = new Padding(0, 5, 10, 0);
        var settings = new Pill("Settings") { Icon = PillIcon.Gear, Margin = new Padding(0, 0, 6, 0) };
        var help = new Pill("Help") { Icon = PillIcon.Help, Margin = Padding.Empty };
        settings.Click += (_, _) =>
        {
            using var form = new SettingsForm();
            form.ShowDialog(this);
            if (form.OpenProject is { } p) Program.OpenVideo(p.File, this, p);
        };
        help.Click += (_, _) => ShowHelp();
        var tips = new ToolTip();
        tips.SetToolTip(settings, "Settings");
        tips.SetToolTip(help, "Help and shortcuts (F1)");
        links.Controls.AddRange([update, settings, help]);
        top.Controls.Add(title);
        top.Controls.Add(links);
        root.Controls.Add(top);

        var drop = new DropZone { Size = new Size(W, 170), Margin = new Padding(0, 8, 0, 0) };
        var open = new Pill("Open video…", PillKind.Primary);
        open.Click += (_, _) => PickVideo();
        drop.Controls.Add(open);
        root.Controls.Add(drop);

        Controls.Add(root);
        AcceptDrop(f => Program.OpenVideo(f, this), on => drop.Hot = on);
#if DEBUG
        if (Environment.GetEnvironmentVariable("CFD_TEST") == "drophot") drop.Hot = true;
#endif
    }

    // A dialog isn't disposed when it closes, unlike a normal window
    void ShowHelp() { using var help = new HelpForm(); help.ShowDialog(this); }

    void PickVideo()
    {
        using var dlg = new OpenFileDialog { Title = "Choose a video to compress", Filter = Program.VideoFilter };
        if (dlg.ShowDialog(this) == DialogResult.OK) Program.OpenVideo(dlg.FileName, this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.O: case Keys.Enter: PickVideo(); return true;
            case Keys.F1: ShowHelp(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Dashed drop target</summary>
    sealed class DropZone : Panel
    {
        bool hot;
        /// <summary>A video is being dragged over the window: light up in brand blue</summary>
        public bool Hot { get => hot; set { if (hot == value) return; hot = value; Invalidate(); } }

        public DropZone()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg2;
        }

        // Center the button just below the middle (the text sits just above it)
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            foreach (Control c in Controls) c.Location = new Point((Width - c.Width) / 2, Height / 2 + (int)(4 * DeviceDpi / 96f));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var px = DeviceDpi / 96f;
            using var path = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 10 * px);
            using (var b = new SolidBrush(hot ? Theme.Blend(Theme.Bg2, Theme.Brand, 0.15) : Theme.Bg2)) g.FillPath(b, path);
            using (var pen = new Pen(hot ? Theme.Brand : Theme.Secondary, (hot ? 2.5f : 2) * px) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            var text = new Rectangle(0, 0, Width, Height / 2 - (int)(6 * px));
            using var font = new Font("Segoe UI Semibold", 12f);
            TextRenderer.DrawText(g, hot ? "Drop to open it" : "Drop a video here", font, text, hot ? Theme.Header : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
        }
    }
}
