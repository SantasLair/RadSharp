using RadSharp.Drawing;
using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Designer;

/// <summary>
/// The Call Stack tool window: the stopped thread's frames, top first, each with its method and file:line. The
/// selected frame (marked with an arrow) is the context of Locals, Watch and hover; double-click or Enter on a row
/// selects that frame through <see cref="FrameActivated"/>, which also shows its line in the editor.
/// </summary>
public sealed class CallStackWindow : Control
{
    readonly RowList _list = new() { Name = "lstCallStack", ItemHeight = 22, BorderStyle = BorderStyle.None, Align = Align.Client };
    IReadOnlyList<StackFrame> _frames = [];
    int _current = -1;

    public CallStackWindow()
    {
        Name = "pnlCallStackWindow";
        TabStop = false;
        Add(_list);
        _list.DrawItem += DrawRow;
        _list.DoubleClick += _ => Activate(_list.ItemIndex);
        _list.EnterPressed += () => Activate(_list.ItemIndex);
    }

    /// <summary>A frame was chosen (index into <see cref="Frames"/>).</summary>
    public event Action<int>? FrameActivated;

    public IReadOnlyList<StackFrame> Frames => _frames;
    public int CurrentIndex => _current;
    public ListBox List => _list;

    /// <summary>Shows a stop's frames with <paramref name="current"/> marked; no frames clears it.</summary>
    public void SetFrames(IReadOnlyList<StackFrame> frames, int current)
    {
        _frames = frames;
        _current = current;
        _list.Items = [.. frames.Select(Describe)];
        _list.ItemIndex = current;
        Invalidate();
    }

    public static string Describe(StackFrame f) =>
        f.Source?.Path is { } p ? $"{f.Name}  {Path.GetFileName(p)}:{f.Line}" : $"{f.Name}  [external code]";

    public void Activate(int index)
    {
        if (index >= 0 && index < _frames.Count) FrameActivated?.Invoke(index);
    }

    void DrawRow(Control sender, Canvas canvas, int index, Rect row, bool selected)
    {
        if (index >= _frames.Count) return;
        var t = canvas.Theme;
        var f = _frames[index];
        if (selected) canvas.FillRect(row, _list.Focused ? t.Selection : t.SelectionInactive);
        int pad = canvas.Px(6), mark = canvas.Px(14);
        if (index == _current)
        {
            float cy = row.Y + row.Height / 2f, x = row.X + pad, s = mark * 0.4f;
            canvas.FillPolygon([new PointF(x, cy - s), new PointF(x + s * 1.6f, cy), new PointF(x, cy + s)], Color.FromHex("#E8A200"));
        }
        var font = _list.EffectiveFont;
        int x0 = row.X + pad + mark, right = row.X + row.Width - pad, placeW = canvas.Px(200);
        bool external = f.Source?.Path == null;
        var place = external ? "[external code]" : $"{Path.GetFileName(f.Source!.Path!)}:{f.Line}";
        canvas.DrawText(place, font, t.TextDisabled, new Rect(right - placeW, row.Y, placeW, row.Height), TextAlign.Right, VerticalAlign.Center, ellipsis: true);
        canvas.DrawText(f.Name, font, external ? t.TextDisabled : t.Text, new Rect(x0, row.Y, Math.Max(0, right - placeW - x0 - pad), row.Height), TextAlign.Left, VerticalAlign.Center, ellipsis: true);
    }
}
