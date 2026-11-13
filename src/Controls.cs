using System.Drawing.Drawing2D;

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
                fill = hover && Enabled ? Theme.BrandHover : Theme.Brand;
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
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
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
        Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = false };
        BackColor = Theme.Bg3;
        ForeColor = Theme.Text;
        ShowImageMargin = false;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(4);
    }

    public ToolStripMenuItem Add(string text, string keys, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, ForeColor = Theme.Text, Padding = new Padding(4, 6, 4, 6) };
        item.Click += (_, _) => onClick();
        Items.Add(item);
        return item;
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

/// <summary>Thumbnail strip with draggable start/end handles, a draggable selection and a playhead. Keys live in MainForm.</summary>
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

    DragKind drag;
    double grabOffset, length;

    public Timeline()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    int HandleW => (int)Math.Round(12 * DeviceDpi / 96f);
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

    DragKind HitTest(int x)
    {
        float s = X(Start), e = X(End), hw = HandleW;
        if (x >= s - 4 && x <= s + hw + 2) return DragKind.Start;
        if (x >= e - hw - 2 && x <= e + 4) return DragKind.End;
        if (x > s && x < e) return DragKind.Range;
        return DragKind.Seek;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        var t = T(e.X);
        drag = HitTest(e.X);
        if (drag == DragKind.Range) { grabOffset = t - Start; length = End - Start; }
        if (drag == DragKind.Seek) Seek(t);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag == DragKind.None)
        {
            Cursor = HitTest(e.X) switch { DragKind.Start or DragKind.End => Cursors.SizeWE, DragKind.Range => Cursors.SizeAll, _ => Cursors.Hand };
            return;
        }
        var t = T(e.X);
        switch (drag)
        {
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

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = DragKind.None; }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        // Thumbnails, cropped to fill each cell
        var n = Thumbs.Length;
        for (var i = 0; i < n; i++)
        {
            var cell = new RectangleF(i * Width / (float)n, 0, Width / (float)n + 1, Height);
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

        float s = X(Start), en = X(End), hw = HandleW, bw = Math.Max(2, HandleW / 4f);
        using (var dim = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        {
            g.FillRectangle(dim, 0, 0, s, Height);
            g.FillRectangle(dim, en, 0, Width - en, Height);
        }
        using (var brand = new SolidBrush(Enabled ? Theme.Brand : Theme.Secondary))
        {
            g.FillRectangle(brand, s, 0, en - s, bw);
            g.FillRectangle(brand, s, Height - bw, en - s, bw);
            g.FillRectangle(brand, s, 0, hw, Height);
            g.FillRectangle(brand, en - hw, 0, hw, Height);
        }
        using (var grip = new Pen(Color.FromArgb(220, Color.White), 2))
        {
            float gh = Height * 0.3f, cy = Height / 2f;
            g.DrawLine(grip, s + hw / 2, cy - gh / 2, s + hw / 2, cy + gh / 2);
            g.DrawLine(grip, en - hw / 2, cy - gh / 2, en - hw / 2, cy + gh / 2);
        }
        using (var head = new Pen(Color.White, 2))
            g.DrawLine(head, X(Current), 0, X(Current), Height);
        if (Focused)
            using (var focus = new Pen(Color.FromArgb(120, Theme.Brand), 2))
                g.DrawRectangle(focus, 1, 1, Width - 2, Height - 2);
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

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Size = new Size(Px(96), Px(28));
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
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height / 2f);
        var focused = Controls.Count > 0 && Controls[0].Focused;
        using var pen = new Pen(focused ? Theme.Brand : Theme.Secondary);
        g.DrawPath(pen, path);
    }
}
