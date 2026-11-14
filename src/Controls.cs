using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>Discord's dark theme colors</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x31, 0x33, 0x38);
    public static readonly Color Bg2 = Color.FromArgb(0x2b, 0x2d, 0x31);
    public static readonly Color Bg3 = Color.FromArgb(0x1e, 0x1f, 0x22);
    public static readonly Color Text = Color.FromArgb(0xdb, 0xde, 0xe1);
    public static readonly Color Header = Color.FromArgb(0xf2, 0xf3, 0xf5);
    public static readonly Color Muted = Color.FromArgb(0x94, 0x9b, 0xa4);
    public static readonly Color Brand = Color.FromArgb(0x58, 0x65, 0xf2);
    public static readonly Color BrandHover = Color.FromArgb(0x47, 0x52, 0xc4);
    public static readonly Color Secondary = Color.FromArgb(0x4e, 0x50, 0x58);
    public static readonly Color SecondaryHover = Color.FromArgb(0x6d, 0x6f, 0x78);
    public static readonly Color Danger = Color.FromArgb(0xf2, 0x3f, 0x43);
    public static readonly Color Warn = Color.FromArgb(0xf0, 0xb2, 0x32);
    public static readonly Color Green = Color.FromArgb(0x23, 0xa5, 0x59);
    public static readonly Color Link = Color.FromArgb(0x00, 0xa8, 0xfc);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

enum PillKind { Chip, Primary, Secondary }

/// <summary>Rounded chip or button, painted like Discord's</summary>
sealed class Pill : Control
{
    readonly PillKind kind;
    bool on, hover;

    public bool On { get => on; set { on = value; Invalidate(); } }
    /// <summary>Minimum width of a button (not a chip), in 96-dpi pixels</summary>
    public int MinWidth { get; init; } = 96;
    /// <summary>Rounds only the left or right corners, for split buttons</summary>
    public bool FlatLeft { get; init; }
    public bool FlatRight { get; init; }
    string? alt;
    /// <summary>Drawn in place of Text without resizing the button, e.g. "Compressing…"</summary>
    public string? AltText { get => alt; set { alt = value; Invalidate(); } }
    /// <summary>Drawn on a red fill while the mouse is over the button, e.g. "✕  Cancel"</summary>
    public string? HoverText { get; set; }

    public Pill(string text, PillKind kind = PillKind.Chip)
    {
        this.kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 6, 6);
        Text = text;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return kind == PillKind.Chip ? new Size(t.Width + Px(20), Px(28)) : new Size(Math.Max(t.Width + Px(MinWidth < 96 ? 16 : 32), Px(MinWidth)), Px(38));
    }

    void Fit() => Size = GetPreferredSize(Size.Empty);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    // DeviceDpi is only right once the handle exists (before that it's the primary screen's), so measure again
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    // Size is already in device pixels (Px), so don't let form auto-scaling scale it again
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill, text = Color.White;
        Color? border = null;
        switch (kind)
        {
            case PillKind.Chip:
                fill = on ? Theme.Brand : hover && Enabled ? Color.FromArgb(60, Theme.Secondary) : Parent?.BackColor ?? Theme.Bg;
                border = on ? Theme.Brand : Theme.Secondary;
                text = on ? Color.White : Theme.Text;
                break;
            case PillKind.Primary:
                fill = hover && Enabled ? HoverText != null ? Theme.Danger : Theme.BrandHover : Theme.Brand;
                break;
            default:
                fill = hover && Enabled ? Theme.SecondaryHover : Theme.Secondary;
                break;
        }
        if (!Enabled)
        {
            fill = Color.FromArgb(110, fill);
            text = Color.FromArgb(130, text);
        }
        using var path = Shape(r, kind == PillKind.Chip ? Height / 2f : Px(8));
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is { } bc) using (var pen = new Pen(Enabled ? bc : Color.FromArgb(110, bc))) g.DrawPath(pen, path);
        var label = hover && Enabled && HoverText != null ? HoverText : alt ?? Text;
        TextRenderer.DrawText(g, label, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    /// <summary>Rounded rectangle; a split-button half keeps its joined side square, out to the edge</summary>
    GraphicsPath Shape(RectangleF r, float rad)
    {
        if (!FlatLeft && !FlatRight) return Theme.Round(r, rad);
        var p = new GraphicsPath();
        var d = rad * 2;
        if (FlatLeft)
        {
            p.AddLine(0, r.Top, r.Right - rad, r.Top);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddLine(r.Right - rad, r.Bottom, 0, r.Bottom);
        }
        else
        {
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddLine(r.Left + rad, r.Top, Width, r.Top);
            p.AddLine(Width, r.Bottom, r.Left + rad, r.Bottom);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        }
        p.CloseFigure();
        return p;
    }
}

/// <summary>Popup menu in Discord's colors</summary>
sealed class DarkMenu : ContextMenuStrip
{
    public DarkMenu()
    {
        Renderer = new Painter();
        BackColor = Theme.Bg3;
        ForeColor = Theme.Text;
        ShowImageMargin = false;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(4);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2; // DWMWCP_ROUND: Windows 11 rounds the popup's corners (ignored on Windows 10)
        DwmSetWindowAttribute(Handle, 33, ref round, 4);
    }

    /// <summary>Opens just below a control, at least `width` wide, with its left edge (or right edge) lined up with it</summary>
    public void ShowUnder(Control anchor, int width, bool alignRight = false)
    {
        MinimumSize = new Size(width, 0);
        var x = alignRight ? width - Math.Max(width, GetPreferredSize(Size.Empty).Width) : 0;
        Show(anchor, new Point(x, anchor.Height + 4));
    }

    public ToolStripMenuItem Add(string text, string keys, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys == "" ? null : keys, ForeColor = Theme.Text, Padding = new Padding(6, 7, 6, 7) };
        item.Click += (_, _) => onClick();
        Items.Add(item);
        return item;
    }

    sealed class Painter : ToolStripProfessionalRenderer
    {
        public Painter() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Theme.Round(new RectangleF(0.5f, 0.5f, e.Item.Width - 1.5f, e.Item.Height - 1.5f), 5 * e.Item.Owner!.DeviceDpi / 96f);
            using var fill = new SolidBrush(Theme.Brand);
            e.Graphics.FillPath(fill, shape);
        }

        // The stock renderer sits the text low; centre it in the full height of the item
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var shortcut = e.Text != e.Item.Text;
            var r = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, e.Item.Selected ? Color.White : shortcut ? Theme.Muted : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | (shortcut ? TextFormatFlags.Right : TextFormatFlags.Left));
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Bg3;
        public override Color MenuBorder => Theme.Bg3;
        public override Color MenuItemBorder => Theme.Brand;
        public override Color MenuItemSelected => Theme.Brand;
        public override Color MenuItemSelectedGradientBegin => Theme.Brand;
        public override Color MenuItemSelectedGradientEnd => Theme.Brand;
        public override Color SeparatorDark => Theme.Secondary;
        public override Color SeparatorLight => Theme.Bg3;
    }
}

/// <summary>Thin progress bar</summary>
sealed class Bar : Control
{
    double value;
    public double Value { get => value; set { this.value = Math.Clamp(value, 0, 1); Invalidate(); } }

    public Bar() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, Height / 2f)) using (var b = new SolidBrush(Theme.Secondary)) g.FillPath(b, path);
        if (value > 0)
        {
            using var path = Theme.Round(new RectangleF(0, 0, Math.Max(Height, (float)((Width - 1) * value)), Height - 1), Height / 2f);
            using var b = new SolidBrush(Theme.Brand);
            g.FillPath(b, path);
        }
    }
}

/// <summary>
/// Thumbnail strip with a playhead. Dragging on the strip scrubs. The start and end are moved with the tabs
/// above it, and the bar between the tabs moves the whole selection. Keys live in MainForm.
/// </summary>
sealed class Timeline : Control
{
    const double MinClip = 0.1;
    enum DragKind { None, Start, End, Range, Seek }

    public double Duration = 1, Fps = 30;
    public double Start, End, Current;
    public Image?[] Thumbs = new Image?[16];
    /// <summary>Start or End changed</summary>
    public event Action? RangeChanged;
    /// <summary>The playhead moved; the form shows that frame</summary>
    public event Action<double>? Seeked;
    /// <summary>The strip was double-clicked at this time; the form plays from there, trimmed or not</summary>
    public event Action<double>? PlayRequested;

    DragKind drag;
    double grabOffset, length;

    public Timeline()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    /// <summary>Height of the row of tabs above the thumbnails</summary>
    int TabH => Px(14);
    int TabW => Px(16);
    float X(double t) => (float)(t / Duration * Width);
    double T(int x) => Math.Clamp(x / (double)Math.Max(1, Width) * Duration, 0, Duration);

    public void Reset(double duration)
    {
        Duration = Math.Max(0.01, duration);
        Start = 0;
        End = Duration;
        Current = 0;
        Invalidate();
    }

    /// <summary>Moves the playhead without raising Seeked (used during playback)</summary>
    public void ShowTime(double t)
    {
        Current = Math.Clamp(t, 0, Duration);
        Invalidate();
    }

    void SetRange(double s, double e)
    {
        Start = s;
        End = e;
        Invalidate();
        RangeChanged?.Invoke();
    }

    public void Seek(double t)
    {
        Current = Math.Clamp(t, 0, Duration);
        Invalidate();
        Seeked?.Invoke(Current);
    }

    public void SetStart(double t) => SetRange(Math.Min(Math.Clamp(t, 0, Duration), End - MinClip), End);
    public void SetEnd(double t) => SetRange(Start, Math.Max(Math.Clamp(t, 0, Duration), Start + MinClip));

    DragKind HitTest(Point p)
    {
        if (p.Y >= TabH + 2) return DragKind.Seek;
        float s = X(Start), e = X(End);
        bool onStart = p.X >= s - 4 && p.X <= s + TabW + 4, onEnd = p.X >= e - TabW - 4 && p.X <= e + 4;
        // A very short selection puts the tabs on top of each other: take the nearer edge
        if (onStart && onEnd) return Math.Abs(p.X - s) <= Math.Abs(p.X - e) ? DragKind.Start : DragKind.End;
        if (onStart) return DragKind.Start;
        if (onEnd) return DragKind.End;
        return p.X > s && p.X < e ? DragKind.Range : DragKind.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        var t = T(e.X);
        drag = HitTest(e.Location);
        if (drag == DragKind.Range) { grabOffset = t - Start; length = End - Start; }
        if (drag == DragKind.Seek) Seek(t);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag == DragKind.None)
        {
            Cursor = HitTest(e.Location) switch { DragKind.Start or DragKind.End => Cursors.SizeWE, DragKind.Range => Cursors.SizeAll, DragKind.Seek => Cursors.Hand, _ => Cursors.Default };
            return;
        }
        var t = T(e.X);
        switch (drag)
        {
            // Normally the playhead follows the edge so you see the frame you're cutting on. With Shift it stays
            // where it is, and the edge snaps to it when close.
            case DragKind.Start when ModifierKeys.HasFlag(Keys.Shift): SetStart(SnapToPlayhead(t, e.X)); break;
            case DragKind.End when ModifierKeys.HasFlag(Keys.Shift): SetEnd(SnapToPlayhead(t, e.X)); break;
            case DragKind.Start: SetStart(t); Seek(Start); break;
            case DragKind.End: SetEnd(t); Seek(End); break;
            case DragKind.Range:
                var s = Math.Clamp(t - grabOffset, 0, Duration - length);
                SetRange(s, s + length);
                Seek(s);
                break;
            default: Seek(t); break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (Enabled && e.Button == MouseButtons.Left && HitTest(e.Location) == DragKind.Seek) PlayRequested?.Invoke(T(e.X));
    }

    double SnapToPlayhead(double t, int x) => Math.Abs(x - X(Current)) <= Px(7) ? Current : t;

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = DragKind.None; }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    /// <summary>A tab: rounded on top, square where it joins the frame</summary>
    static GraphicsPath TabShape(int x, int w, int h, int r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, 0, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, 0, r * 2, r * 2, 270, 90);
        p.AddLine(x + w, h, x, h);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        int top = TabH, bodyH = Height - top;
        using (var black = new SolidBrush(Color.Black)) g.FillRectangle(black, 0, top, Width, bodyH);

        // Thumbnails, cropped to fill each cell
        var n = Thumbs.Length;
        for (var i = 0; i < n; i++)
        {
            var cell = new RectangleF(i * Width / (float)n, top, Width / (float)n + 1, bodyH);
            if (Thumbs[i] is not { } img)
            {
                using var b = new SolidBrush(Color.FromArgb(i % 2 == 0 ? 18 : 26, Color.White));
                g.FillRectangle(b, cell);
                continue;
            }
            var scale = Math.Max(cell.Width / img.Width, cell.Height / img.Height);
            float sw = cell.Width / scale, sh = cell.Height / scale;
            g.DrawImage(img, cell, new RectangleF((img.Width - sw) / 2, (img.Height - sh) / 2, sw, sh), GraphicsUnit.Pixel);
        }

        // Whole pixels throughout, so the tabs and the frame meet without soft or missing pixels
        int s = (int)Math.Round(X(Start)), en = (int)Math.Round(X(End)), edge = Math.Max(2, Px(3)), tw = TabW;
        using (var dim = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        {
            g.FillRectangle(dim, 0, top, s, bodyH);
            g.FillRectangle(dim, en, top, Width - en, bodyH);
        }
        using (var brand = new SolidBrush(Enabled ? Theme.Brand : Theme.Secondary))
        {
            // Frame around the selection
            g.FillRectangle(brand, s, top, en - s, edge);
            g.FillRectangle(brand, s, Height - edge, en - s, edge);
            g.FillRectangle(brand, s, top, edge, bodyH);
            g.FillRectangle(brand, en - edge, top, edge, bodyH);
            // Bar between the tabs (drag it to move the selection) and the two tabs
            g.FillRectangle(brand, s, top - edge, en - s, edge);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var x in new[] { s, en - tw })
                using (var tab = TabShape(x, tw, top, Px(4)))
                    g.FillPath(brand, tab);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }
        // Grip: a 3x3 grid of dots on each tab
        using (var dots = new SolidBrush(Color.FromArgb(235, Color.White)))
        {
            int dot = Px(2), gap = Px(2), grid = dot * 3 + gap * 2;
            foreach (var x in new[] { s, en - tw })
                for (var i = 0; i < 9; i++)
                    g.FillRectangle(dots, x + (tw - grid) / 2 + i % 3 * (dot + gap), (top - grid) / 2 + i / 3 * (dot + gap), dot, dot);
        }
        using (var head = new Pen(Color.White, 2))
            g.DrawLine(head, X(Current), top, X(Current), Height);
        if (Focused)
            using (var focus = new Pen(Color.FromArgb(120, Theme.Brand), 2))
                g.DrawRectangle(focus, 1, top + 1, Width - 2, bodyH - 2);
    }
}

/// <summary>
/// Chip-shaped frame for a text box, so it sits in a row of chips without looking out of place.
/// Sizes itself from DeviceDpi like Pill does, so it's always exactly as tall as the chips next to it.
/// </summary>
sealed class RoundBox : Panel
{
    public RoundBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg2;
        Fit();
    }

    // Sizes in 96-dpi pixels. Width 0 = the layout decides the width (docked or anchored).
    readonly int logicalWidth = 96, logicalHeight = 28;
    public int LogicalWidth { get => logicalWidth; init { logicalWidth = value; Fit(); } }
    public int LogicalHeight { get => logicalHeight; init { logicalHeight = value; Fit(); } }
    /// <summary>Corner radius; 0 = fully rounded ends, like a chip</summary>
    public int Radius { get; init; }
    /// <summary>Filled with this colour (a text field); otherwise just an outline</summary>
    public Color? Fill { get; init; }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Size = new Size(logicalWidth > 0 ? Px(logicalWidth) : Width, Px(logicalHeight));
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

    // Text box centred vertically, clear of the rounded ends
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Controls.Count == 0) return;
        var c = Controls[0];
        c.Bounds = new Rectangle(Px(12), (Height - c.Height) / 2, Width - Px(24), c.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius > 0 ? Px(Radius) : Height / 2f);
        if (Fill is { } fill) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        var focused = Controls.Count > 0 && Controls[0].Focused;
        using var pen = new Pen(focused ? Theme.Brand : Theme.Secondary);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// One audio track drawn as a waveform with its volume line on top. Dragging up or down changes the volume;
/// double-clicking adds a point so the volume can change over time; right-clicking a point removes it.
/// </summary>
sealed class VolumeLane : Control
{
    static readonly Font Small = new("Segoe UI", 8f);

    /// <summary>Loudest sample in each slice of the track, 0..1; null until measured</summary>
    public float[]? Peaks;
    public double Duration = 1, Start, End = 1, Current;
    /// <summary>False for a muted track (drawn grey, left out of the mix)</summary>
    public bool Active = true;
    /// <summary>The track's name, shown on a badge at the left that also mutes and unmutes it</summary>
    public string Label = "";
    /// <summary>Nothing audible in this track; said on the badge</summary>
    public bool Silent;
    /// <summary>The badge was clicked: Active has flipped</summary>
    public event Action? MuteToggled;
    /// <summary>Volume while there are no points: 1 = unchanged, 0 = silent, 2 = double</summary>
    public double Volume = 1;
    /// <summary>Volume points in time order; empty = the same volume throughout</summary>
    public readonly List<(double T, double V)> Keys = [];
    /// <summary>The volume changed and the mouse was let go</summary>
    public event Action? Committed;

    int dragKey = -1;
    bool dragFlat;
    int grabY;
    double grabVolume;
    int pixelWidth = 300;

    public VolumeLane()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.SizeNS;
    }

    /// <summary>Width in real pixels (the form decides it from the room it has); the height follows the display scaling like Pill</summary>
    public int PixelWidth { get => pixelWidth; set { pixelWidth = value; Fit(); } }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Size = new Size(pixelWidth, Px(34));
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }
    int Pad => Px(4);
    float X(double t) => (float)(t / Duration * Width);
    double T(int x) => Math.Clamp(x / (double)Math.Max(1, Width) * Duration, 0, Duration);
    float Y(double v) => Pad + (float)((2 - v) / 2 * (Height - 2 * Pad));
    // Sticks to 100% when close, so "unchanged" is easy to hit
    static double Snap(double v) { v = Math.Clamp(v, 0, 2); return Math.Abs(v - 1) < 0.06 ? 1 : Math.Round(v, 2); }
    double V(int y) => Snap(2 - (y - Pad) / (double)Math.Max(1, Height - 2 * Pad) * 2);

    public double GainAt(double t)
    {
        if (Keys.Count == 0) return Volume;
        if (t <= Keys[0].T) return Keys[0].V;
        for (var i = 1; i < Keys.Count; i++)
            if (t < Keys[i].T)
            {
                var (a, b) = (Keys[i - 1], Keys[i]);
                return a.V + (b.V - a.V) * (t - a.T) / Math.Max(0.001, b.T - a.T);
            }
        return Keys[^1].V;
    }

    int HitKey(Point p)
    {
        for (var i = 0; i < Keys.Count; i++)
            if (Math.Abs(p.X - X(Keys[i].T)) <= Px(7) && Math.Abs(p.Y - Y(Keys[i].V)) <= Px(9)) return i;
        return -1;
    }

    string BadgeText => Label + (Silent ? " · silent" : "");
    Rectangle Badge => new(Px(4), Px(4), Px(22) + TextRenderer.MeasureText(BadgeText, Small, Size.Empty, TextFormatFlags.NoPrefix).Width, Px(18));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled) return;
        if (e.Button == MouseButtons.Left && Badge.Contains(e.Location))
        {
            Active = !Active;
            Invalidate();
            MuteToggled?.Invoke();
            return;
        }
        var k = HitKey(e.Location);
        if (e.Button == MouseButtons.Right && k >= 0)
        {
            Keys.RemoveAt(k);
            Invalidate();
            Committed?.Invoke();
        }
        else if (e.Button == MouseButtons.Left)
        {
            dragKey = k;
            dragFlat = k < 0 && Keys.Count == 0;
            grabY = e.Y;
            grabVolume = Volume;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragKey >= 0)
        {
            // A point can't pass its neighbours
            var lo = dragKey > 0 ? Keys[dragKey - 1].T + 0.05 : 0;
            var hi = dragKey < Keys.Count - 1 ? Keys[dragKey + 1].T - 0.05 : Duration;
            Keys[dragKey] = (Math.Clamp(T(e.X), lo, Math.Max(lo, hi)), V(e.Y));
            Invalidate();
        }
        else if (dragFlat)
        {
            // Relative to where the drag began, so a plain click never jumps the volume
            Volume = Snap(grabVolume + (grabY - e.Y) / (double)Math.Max(1, Height - 2 * Pad) * 2);
            Invalidate();
        }
        else Cursor = Badge.Contains(e.Location) ? Cursors.Hand : HitKey(e.Location) >= 0 ? Cursors.SizeAll : Keys.Count == 0 ? Cursors.SizeNS : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var changed = dragKey >= 0 || (dragFlat && Volume != grabVolume);
        dragKey = -1;
        dragFlat = false;
        if (changed) Committed?.Invoke();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!Enabled || e.Button != MouseButtons.Left || HitKey(e.Location) >= 0 || Badge.Contains(e.Location)) return;
        var t = T(e.X);
        var i = Keys.FindIndex(k => k.T > t);
        Keys.Insert(i < 0 ? Keys.Count : i, (t, GainAt(t)));
        Invalidate();
        Committed?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Px(6));
        using (var bg = new SolidBrush(Theme.Bg3)) g.FillPath(bg, shape);
        g.SetClip(shape);

        float mid = Height / 2f, half = Height / 2f - 2;
        if (Peaks is { Length: > 0 } peaks)
        {
            var loudest = Math.Max(peaks.Max(), 0.02f);
            using var inside = new Pen(Active ? Theme.Brand : Theme.Secondary);
            using var outside = new Pen(Color.FromArgb(90, Active ? Theme.Brand : Theme.Secondary));
            for (var x = 0; x < Width; x++)
            {
                var t = x / (double)Width * Duration;
                // Half height at 100% so there's room to show a boost
                var h = (float)Math.Min(1, peaks[Math.Min(peaks.Length - 1, x * peaks.Length / Width)] / loudest * 0.5 * GainAt(t)) * half;
                if (h >= 0.5f) g.DrawLine(t >= Start && t <= End ? inside : outside, x + 0.5f, mid - h, x + 0.5f, mid + h);
            }
        }

        // Volume line and its points
        var line = Color.FromArgb(Active ? 235 : 110, Color.White);
        var pts = new List<PointF> { new(0, Y(GainAt(0))) };
        pts.AddRange(Keys.Select(k => new PointF(X(k.T), Y(k.V))));
        pts.Add(new PointF(Width, Y(GainAt(Duration))));
        using (var pen = new Pen(line, 1.5f)) g.DrawLines(pen, pts.ToArray());
        using (var dot = new SolidBrush(line))
            foreach (var k in Keys) g.FillEllipse(dot, X(k.T) - Px(4), Y(k.V) - Px(4), Px(8), Px(8));

        using (var head = new Pen(Color.FromArgb(160, Color.White))) g.DrawLine(head, X(Current), 0, X(Current), Height);
        g.ResetClip();

        // Badge: speaker (struck through when muted) and the track's name. Click it to mute or unmute.
        var badge = Badge;
        using (var back = Theme.Round(badge, Px(4)))
        using (var fill = new SolidBrush(Color.FromArgb(215, Theme.Bg2))) g.FillPath(fill, back);
        var ink = Active ? Theme.Text : Theme.Muted;
        float s = Px(10), ix = badge.X + Px(5), iy = badge.Y + (badge.Height - s) / 2f;
        using (var speaker = new SolidBrush(ink))
            g.FillPolygon(speaker, [new PointF(ix, iy + s * 0.3f), new PointF(ix + s * 0.35f, iy + s * 0.3f), new PointF(ix + s * 0.8f, iy), new PointF(ix + s * 0.8f, iy + s), new PointF(ix + s * 0.35f, iy + s * 0.7f), new PointF(ix, iy + s * 0.7f)]);
        if (!Active)
            using (var strike = new Pen(Theme.Danger, Math.Max(1.5f, Px(2)))) g.DrawLine(strike, ix - 1, iy + s, ix + s, iy);
        TextRenderer.DrawText(g, BadgeText, Small, new Rectangle(badge.X + Px(18), badge.Y, badge.Width, badge.Height), ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var label = dragKey >= 0 ? $"{Keys[dragKey].V * 100:0}%" : Keys.Count == 0 ? $"{Volume * 100:0}%" : "";
        TextRenderer.DrawText(g, label, Small, new Rectangle(0, Px(2), Width - Px(6), Height), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// The video preview. With a crop box set, it's drawn over the frame (everything outside dimmed) and can be
/// moved by dragging inside it and resized by its corners.
/// </summary>
sealed class CropView : Control
{
    enum Grab { None, Move, TopLeft, TopRight, BottomLeft, BottomRight }

    Image? image;
    RectangleF? box;
    public Image? Image { get => image; set { image = value; Invalidate(); } }
    /// <summary>Width / height of the video, so the box maps to the picture and not the black bars</summary>
    public double FrameAspect = 16 / 9.0;
    /// <summary>Crop box as fractions of the frame (0..1 each way); null = no crop</summary>
    public RectangleF? Box { get => box; set { box = value; Invalidate(); } }
    /// <summary>Width / height the box must keep, in those same fractions; 0 = any shape</summary>
    public double BoxAspect;
    /// <summary>The box is being dragged to this</summary>
    public event Action<RectangleF>? BoxChanged;
    /// <summary>The drag ended</summary>
    public event Action? BoxCommitted;

    Grab grab;
    PointF grabAt;
    RectangleF grabBox;

    public CropView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Black;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    /// <summary>Where the picture sits inside the control (fitted, centred)</summary>
    RectangleF Picture
    {
        get
        {
            var scale = Math.Min(Width / FrameAspect, Height);
            float w = (float)(scale * FrameAspect), h = (float)scale;
            return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
        }
    }

    RectangleF ToScreen(RectangleF b) { var p = Picture; return new RectangleF(p.X + b.X * p.Width, p.Y + b.Y * p.Height, b.Width * p.Width, b.Height * p.Height); }
    PointF ToFrame(Point at) { var p = Picture; return new PointF(Math.Clamp((at.X - p.X) / p.Width, 0, 1), Math.Clamp((at.Y - p.Y) / p.Height, 0, 1)); }

    Grab HitTest(Point at)
    {
        if (box is not { } b || !Enabled) return Grab.None;
        var r = ToScreen(b);
        var reach = Px(10);
        bool left = Math.Abs(at.X - r.Left) <= reach, right = Math.Abs(at.X - r.Right) <= reach, top = Math.Abs(at.Y - r.Top) <= reach, bottom = Math.Abs(at.Y - r.Bottom) <= reach;
        if (top && left) return Grab.TopLeft;
        if (top && right) return Grab.TopRight;
        if (bottom && left) return Grab.BottomLeft;
        if (bottom && right) return Grab.BottomRight;
        return r.Contains(at) ? Grab.Move : Grab.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || box is not { } b) return;
        grab = HitTest(e.Location);
        grabAt = ToFrame(e.Location);
        grabBox = b;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (grab == Grab.None)
        {
            Cursor = HitTest(e.Location) switch { Grab.TopLeft or Grab.BottomRight => Cursors.SizeNWSE, Grab.TopRight or Grab.BottomLeft => Cursors.SizeNESW, Grab.Move => Cursors.SizeAll, _ => Cursors.Default };
            return;
        }
        var at = ToFrame(e.Location);
        if (grab == Grab.Move)
        {
            var x = Math.Clamp(grabBox.X + at.X - grabAt.X, 0, 1 - grabBox.Width);
            var y = Math.Clamp(grabBox.Y + at.Y - grabAt.Y, 0, 1 - grabBox.Height);
            BoxChanged?.Invoke(new RectangleF(x, y, grabBox.Width, grabBox.Height));
            return;
        }
        // Resizing: the opposite corner stays put
        bool fromLeft = grab is Grab.TopLeft or Grab.BottomLeft, fromTop = grab is Grab.TopLeft or Grab.TopRight;
        float ax = fromLeft ? grabBox.Right : grabBox.Left, ay = fromTop ? grabBox.Bottom : grabBox.Top;
        float roomW = fromLeft ? ax : 1 - ax, roomH = fromTop ? ay : 1 - ay;
        float w = Math.Clamp(fromLeft ? ax - at.X : at.X - ax, 0.05f, roomW), h = Math.Clamp(fromTop ? ay - at.Y : at.Y - ay, 0.05f, roomH);
        if (BoxAspect > 0)
        {
            // Keep the shape: follow whichever direction was dragged further, then fit inside the frame
            w = (float)Math.Max(w, h * BoxAspect);
            if (w > roomW) w = roomW;
            h = (float)(w / BoxAspect);
            if (h > roomH) { h = roomH; w = (float)(h * BoxAspect); }
        }
        BoxChanged?.Invoke(new RectangleF(fromLeft ? ax - w : ax, fromTop ? ay - h : ay, w, h));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (grab == Grab.None) return;
        grab = Grab.None;
        BoxCommitted?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var pic = Picture;
        if (image != null)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(image, pic);
        }
        if (box is not { } b) return;
        var r = Rectangle.Round(ToScreen(b));
        var all = Rectangle.Round(pic);
        using (var dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
        {
            g.FillRectangle(dim, all.Left, all.Top, all.Width, r.Top - all.Top);
            g.FillRectangle(dim, all.Left, r.Bottom, all.Width, all.Bottom - r.Bottom);
            g.FillRectangle(dim, all.Left, r.Top, r.Left - all.Left, r.Height);
            g.FillRectangle(dim, r.Right, r.Top, all.Right - r.Right, r.Height);
        }
        using (var frame = new Pen(Color.White, Math.Max(1, Px(2)))) g.DrawRectangle(frame, r);
        var hs = Px(9);
        foreach (var c in new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom) })
            g.FillRectangle(Brushes.White, c.X - hs / 2, c.Y - hs / 2, hs, hs);
    }
}

/// <summary>
/// Thin track under the timeline showing the crop's key points as diamonds. Double-click adds one, dragging
/// moves it in time, right-click removes it, clicking jumps to it.
/// </summary>
sealed class KeyLane : Control
{
    public double Duration = 1, Current;
    /// <summary>Times of the key points, in order</summary>
    public IReadOnlyList<double> Keys = [];
    public event Action<double>? AddRequested;
    public event Action<int>? RemoveRequested, Picked;
    public event Action<int, double>? MoveRequested;

    static readonly Font SmallFont = new("Segoe UI", 8f);
    int dragKey = -1;
    bool moved;

    public KeyLane()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Height = Px(18);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

    float X(double t) => (float)(t / Duration * Width);
    double T(int x) => Math.Clamp(x / (double)Math.Max(1, Width) * Duration, 0, Duration);

    int HitKey(int x)
    {
        for (var i = 0; i < Keys.Count; i++) if (Math.Abs(x - X(Keys[i])) <= Px(8)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled) return;
        var k = HitKey(e.X);
        if (e.Button == MouseButtons.Right && k >= 0) RemoveRequested?.Invoke(k);
        else if (e.Button == MouseButtons.Left) { dragKey = k; moved = false; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragKey < 0) { Cursor = HitKey(e.X) >= 0 ? Cursors.SizeWE : Cursors.Default; return; }
        moved = true;
        MoveRequested?.Invoke(dragKey, T(e.X));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragKey >= 0 && !moved) Picked?.Invoke(dragKey);
        dragKey = -1;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (Enabled && e.Button == MouseButtons.Left && HitKey(e.X) < 0) AddRequested?.Invoke(T(e.X));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Px(5)))
        using (var bg = new SolidBrush(Theme.Bg3)) g.FillPath(bg, track);
        float mid = Height / 2f, d = Height / 2f - Px(2);
        TextRenderer.DrawText(g, "Crop", SmallFont, new Rectangle(Px(6), 0, Width, Height), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Keys.Count > 1)
            using (var line = new Pen(Color.FromArgb(150, Theme.Brand), Math.Max(1, Px(2)))) g.DrawLine(line, X(Keys[0]), mid, X(Keys[^1]), mid);
        using (var head = new Pen(Color.FromArgb(160, Color.White))) g.DrawLine(head, X(Current), 0, X(Current), Height);
        foreach (var t in Keys)
        {
            var x = Math.Clamp(X(t), d, Width - d);
            // White when the playhead is on it: that's the point the box on the preview edits
            using var fill = new SolidBrush(Math.Abs(t - Current) < 0.02 ? Color.White : Theme.Brand);
            g.FillPolygon(fill, [new PointF(x, mid - d), new PointF(x + d, mid), new PointF(x, mid + d), new PointF(x - d, mid)]);
        }
    }
}
