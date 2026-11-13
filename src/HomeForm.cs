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
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right, Padding = new Padding(0, 9, 0, 0) };
        links.Controls.Add(UpdateLink(this));
        links.Controls.Add(Link("Settings", () => new SettingsForm().ShowDialog(this)));
        var help = Link("Help", () => new HelpForm().ShowDialog(this));
        help.Margin = Padding.Empty;
        links.Controls.Add(help);
        top.Controls.Add(title);
        top.Controls.Add(links);
        root.Controls.Add(top);

        var drop = new DropZone { Size = new Size(W, 170), Margin = new Padding(0, 8, 0, 0) };
        var open = new Pill("Open video…", PillKind.Primary);
        open.Click += (_, _) => PickVideo();
        drop.Controls.Add(open);
        root.Controls.Add(drop);

        Controls.Add(root);
        AcceptDrop(f => Program.OpenVideo(f, this));
    }

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
            case Keys.F1: new HelpForm().ShowDialog(this); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Dashed drop target</summary>
    sealed class DropZone : Panel
    {
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
            using (var b = new SolidBrush(Theme.Bg2)) g.FillPath(b, path);
            using (var pen = new Pen(Theme.Secondary, 2 * px) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            var text = new Rectangle(0, 0, Width, Height / 2 - (int)(6 * px));
            using var font = new Font("Segoe UI Semibold", 12f);
            TextRenderer.DrawText(g, "Drop a video here", font, text, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
        }
    }
}
