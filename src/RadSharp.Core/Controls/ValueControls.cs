using System.Globalization;
using RadSharp.Drawing;

namespace RadSharp;

public enum Orientation { Horizontal, Vertical }

/// <summary>A slider (Delphi's TTrackBar).</summary>
public class TrackBar : Control
{
    int _min, _max = 10, _position;
    bool _dragging;

    public TrackBar()
    {
        SetDefaultSize(160, 32);
        TabStop = true;
    }

    [Published(Category = "Behavior")] public int Min { get => _min; set { _min = value; Position = _position; Invalidate(); } }
    [Published(Category = "Behavior")] public int Max { get => _max; set { _max = value; Position = _position; Invalidate(); } }

    [Published(Category = "Behavior")]
    public int Position
    {
        get => _position;
        set
        {
            value = Math.Clamp(value, Math.Min(_min, _max), Math.Max(_min, _max));
            if (value == _position) return;
            _position = value;
            Invalidate();
            Change?.Invoke(this);
        }
    }

    /// <summary>A tick mark every this many positions; 0 hides ticks.</summary>
    [Published(Category = "Appearance")] public int Frequency { get; set; } = 1;
    [Published(Category = "Appearance")] public Orientation Orientation { get; set; }
    [Published] public event NotifyEvent? Change;

    bool Vertical => Orientation == Orientation.Vertical;
    int ThumbSize => Px(16);
    int TrackLength => Math.Max(1, (Vertical ? Height : Width) - ThumbSize);

    /// <summary>Pixel offset (along the axis, thumb centre) of a position.</summary>
    public int OffsetOf(int position) =>
        ThumbSize / 2 + (_max == _min ? 0 : (int)Math.Round((double)(position - _min) * TrackLength / (_max - _min)));

    int PositionAt(int along) =>
        _max == _min ? _min : _min + (int)Math.Round((double)(along - ThumbSize / 2) * (_max - _min) / TrackLength);

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButton.Left) return;
        _dragging = true;
        Position = PositionAt(Vertical ? e.Y : e.X);
    }

    protected internal override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Position = PositionAt(Vertical ? e.Y : e.X);
    }

    protected internal override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        Position += (int)Math.Round(e.DeltaY);
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None) return;
        int page = Math.Max(1, Math.Abs(_max - _min) / 10);
        int? p = e.Key switch
        {
            Key.Left or Key.Down => _position - 1,
            Key.Right or Key.Up => _position + 1,
            Key.PageDown => _position - page,
            Key.PageUp => _position + page,
            Key.Home => _min,
            Key.End => _max,
            _ => null,
        };
        if (p is { } v) { Position = v; e.Handled = true; }
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        bool enabled = IsEffectivelyEnabled;
        int thick = Math.Max(2, canvas.Px(4));
        int cross = (Vertical ? Width : Height) / 2;
        int start = ThumbSize / 2, end = start + TrackLength;
        int pos = OffsetOf(_position);
        Rect Along(int a, int b, int c, int size) => Vertical ? new Rect(c - size / 2, a, size, b - a) : new Rect(a, c - size / 2, b - a, size);

        canvas.FillRoundRect(Along(start, end, cross, thick), thick / 2f, t.ScrollTrack);
        canvas.FillRoundRect(Along(start, pos, cross, thick), thick / 2f, enabled ? t.Accent : t.TextDisabled);
        if (Frequency > 0 && _max != _min)
            for (int v = Math.Min(_min, _max); v <= Math.Max(_min, _max); v += Frequency)
            {
                int o = OffsetOf(v);
                var tick = Vertical ? new Rect(cross + ThumbSize / 2 + canvas.Px(2), o, canvas.Px(4), canvas.Px(1))
                                    : new Rect(o, cross + ThumbSize / 2 + canvas.Px(2), canvas.Px(1), canvas.Px(4));
                canvas.FillRect(tick, t.InputBorder);
            }
        var thumb = Vertical ? new Rect(cross - ThumbSize / 2, pos - ThumbSize / 2, ThumbSize, ThumbSize)
                             : new Rect(pos - ThumbSize / 2, cross - ThumbSize / 2, ThumbSize, ThumbSize);
        canvas.FillEllipse(thumb, t.InputBackground);
        canvas.DrawEllipseBorder(thumb, enabled ? (IsHovered || _dragging ? t.Accent : t.InputBorder) : t.TextDisabled, canvas.Px(1));
        if (Focused && OwningForm is { ShowFocusCues: true })
            canvas.DrawEllipseBorder(Canvas.Inflate(thumb, canvas.Px(2)), t.FocusRing, Math.Max(1, canvas.Px(2)));
    }
}

/// <summary>A progress indicator (Delphi's TProgressBar).</summary>
public class ProgressBar : Control
{
    int _min, _max = 100, _position;

    public ProgressBar() => SetDefaultSize(160, 16);

    [Published(Category = "Behavior")] public int Min { get => _min; set { _min = value; Invalidate(); } }
    [Published(Category = "Behavior")] public int Max { get => _max; set { _max = value; Invalidate(); } }

    [Published(Category = "Behavior")]
    public int Position
    {
        get => _position;
        set { _position = Math.Clamp(value, Math.Min(_min, _max), Math.Max(_min, _max)); Invalidate(); }
    }

    protected override void Paint(Canvas canvas)
    {
        var t = canvas.Theme;
        var r = new Rect(0, 0, Width, Height);
        float radius = Math.Min(Height / 2f, canvas.Px(4));
        canvas.FillRoundRect(r, radius, t.ScrollTrack);
        if (_max == _min) return;
        int w = (int)Math.Round((double)(_position - _min) * Width / (_max - _min));
        if (w > 0) canvas.FillRoundRect(new Rect(0, 0, w, Height), radius, IsEffectivelyEnabled ? t.Accent : t.TextDisabled);
    }
}

/// <summary>A number box with up/down buttons (Delphi's TSpinEdit).</summary>
public class SpinEdit : Edit
{
    int _value, _minValue, _maxValue;
    bool _syncing;

    public SpinEdit()
    {
        SetDefaultSize(100, 24);
        AutoSelect = true;
        SyncText();
    }

    /// <summary>The number. With both limits 0 there is no limit (as in Delphi).</summary>
    [Published(Category = "Behavior")]
    public int Value
    {
        get => _value;
        set
        {
            value = Clamp(value);
            bool changed = value != _value;
            _value = value;
            SyncText();
            if (changed) ValueChanged?.Invoke(this);
        }
    }

    [Published(Category = "Behavior")] public int MinValue { get => _minValue; set { _minValue = value; Value = _value; } }
    [Published(Category = "Behavior")] public int MaxValue { get => _maxValue; set { _maxValue = value; Value = _value; } }
    [Published(Category = "Behavior")] public int Increment { get; set; } = 1;

    /// <summary>Raised when <see cref="Value"/> changes (after typing, when the box is left or Enter is pressed).</summary>
    [Published("onValueChange")] public event NotifyEvent? ValueChanged;

    int Clamp(int v) => _minValue == 0 && _maxValue == 0 ? v : Math.Clamp(v, _minValue, Math.Max(_minValue, _maxValue));

    void SyncText()
    {
        _syncing = true;
        try { Text = _value.ToString(CultureInfo.InvariantCulture); }
        finally { _syncing = false; }
    }

    /// <summary>Takes the typed number (or restores the last valid one).</summary>
    void Commit()
    {
        if (int.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) Value = v;
        else SyncText();
    }

    protected override void OnChange()
    {
        if (!_syncing) base.OnChange();
    }

    protected override int ReservedRight => Px(18);

    Rect ButtonsRect => new(Width - Px(1) - Px(18), Px(1), Px(18), Math.Max(0, Height - 2 * Px(1)));

    protected internal override void OnTextInput(TextInputEventArgs e)
    {
        // Digits, and a minus sign at the start.
        var filtered = new string([.. e.Text.Where(c => char.IsAsciiDigit(c) || (c == '-' && SelectionStart == 0))]);
        if (filtered.Length == 0) { e.Handled = true; return; }
        base.OnTextInput(new TextInputEventArgs(filtered));
        e.Handled = true;
    }

    protected internal override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Modifiers == KeyModifiers.None && e.Key is Key.Up or Key.Down)
        {
            Commit();
            Value += e.Key == Key.Up ? Increment : -Increment;
            SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter) Commit();  // not handled: Enter still reaches the default button
        base.OnKeyDown(e);
    }

    protected internal override void OnLostFocus()
    {
        Commit();
        base.OnLostFocus();
    }

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        var b = ButtonsRect;
        if (e.Button == MouseButton.Left && e.X >= b.X)
        {
            Commit();
            Value += e.Y < b.Y + b.Height / 2 ? Increment : -Increment;
            return;
        }
        base.OnMouseDown(e);
    }

    protected internal override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        Commit();
        Value += (int)Math.Round(e.DeltaY) * Increment;
        e.Handled = true;
    }

    protected override void PaintOverlay(Canvas canvas)
    {
        var t = canvas.Theme;
        var b = ButtonsRect;
        canvas.FillRect(new Rect(b.X, b.Y, canvas.Px(1), b.Height), t.PanelBorder);
        var color = IsEffectivelyEnabled ? t.Text : t.TextDisabled;
        int half = b.Height / 2;
        canvas.DrawText("˄", canvas.DefaultFont, color, new Rect(b.X, b.Y + canvas.Px(1), b.Width, half), TextAlign.Center, VerticalAlign.Center);
        canvas.DrawText("˅", canvas.DefaultFont, color, new Rect(b.X, b.Y + half - canvas.Px(1), b.Width, half), TextAlign.Center, VerticalAlign.Center);
    }
}
