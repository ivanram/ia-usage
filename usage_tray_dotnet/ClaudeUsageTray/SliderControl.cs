using System.Drawing.Drawing2D;

namespace ClaudeUsageTray;

/// <summary>
/// A custom-drawn slider so its background actually matches the card it
/// sits in — the native WinForms TrackBar paints its own opaque background
/// that ignores BackColor and always looks like a mismatched white box.
/// </summary>
public sealed class SliderControl : Control
{
    private int _minimum = 1;
    private int _maximum = 60;
    private int _value = 1;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    public int Minimum { get => _minimum; set { _minimum = value; Invalidate(); } }
    public int Maximum { get => _maximum; set { _maximum = value; Invalidate(); } }

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, _minimum, _maximum);
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Color TrackColor { get; set; } = Color.FromArgb(255, 224, 224, 228);
    public Color FillColor { get; set; } = Color.FromArgb(255, 0, 103, 192);
    public Color ThumbColor { get; set; } = Color.White;
    public Color ThumbBorderColor { get; set; } = Color.FromArgb(255, 0, 103, 192);

    private const int ThumbRadius = 8;

    public SliderControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 24;
        TabStop = true;
    }

    private Rectangle TrackRect => new(ThumbRadius, Height / 2 - 2, Math.Max(1, Width - ThumbRadius * 2), 4);

    private float ValueFraction() => _maximum == _minimum ? 0 : (_value - _minimum) / (float)(_maximum - _minimum);
    private int ThumbX() => TrackRect.X + (int)(ValueFraction() * TrackRect.Width);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = TrackRect;

        using var trackPath = RoundedRect(track, track.Height / 2);
        using (var trackBrush = new SolidBrush(TrackColor)) g.FillPath(trackBrush, trackPath);

        var filledWidth = ThumbX() - track.X;
        if (filledWidth > 0)
        {
            var oldClip = g.Clip;
            g.SetClip(trackPath, CombineMode.Intersect);
            using var fillBrush = new SolidBrush(FillColor);
            g.FillRectangle(fillBrush, new Rectangle(track.X, track.Y, filledWidth, track.Height));
            g.Clip = oldClip;
        }

        var thumbRect = new Rectangle(ThumbX() - ThumbRadius, Height / 2 - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);
        using (var thumbBrush = new SolidBrush(ThumbColor)) g.FillEllipse(thumbBrush, thumbRect);
        using var thumbPen = new Pen(ThumbBorderColor, 2f);
        g.DrawEllipse(thumbPen, thumbRect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragging = true;
        Focus();
        SetValueFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetValueFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    private void SetValueFromX(int x)
    {
        var track = TrackRect;
        var frac = Math.Clamp((x - track.X) / (float)track.Width, 0f, 1f);
        Value = _minimum + (int)Math.Round(frac * (_maximum - _minimum));
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Value -= 1;
        else if (e.KeyCode == Keys.Right) Value += 1;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) { path.AddRectangle(bounds); return path; }
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
