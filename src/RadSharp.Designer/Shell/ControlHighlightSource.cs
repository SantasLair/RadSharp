using RadSharp.CodeEditing;

namespace RadSharp.Designer;

/// <summary>
/// The code editor's side of the structure/code sync: an <see cref="IDecorationSource"/> holding highlights the shell computes (the field and
/// handlers of the selected control). It owns no logic; <see cref="Set"/> replaces everything and repaints the editor.
/// </summary>
public sealed class ControlHighlightSource : IDecorationSource
{
    DecorationSet? _set;

    /// <summary>The highlights shown now (against <see cref="DecorationSet.Snapshot"/>; the editor maps them through later edits).</summary>
    public DecorationSet? Current => _set;

    public DecorationSet? GetDecorations(TextSnapshot current) => _set;

    public event EventHandler<SourceChangedEventArgs>? Changed;

    /// <summary>Replaces the highlights (none clears them).</summary>
    public void Set(TextSnapshot snapshot, IReadOnlyList<Decoration> items)
    {
        if (items.Count == 0 && (_set == null || _set.Items.Count == 0)) return;
        _set = items.Count == 0 ? null : new DecorationSet(snapshot, [.. items.OrderBy(i => i.Start)]);
        Changed?.Invoke(this, new SourceChangedEventArgs());
    }
}
