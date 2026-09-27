namespace RadSharp.Designer;

/// <summary>
/// Delphi's Alignment Palette / Visual Studio's Format menu: align edges or centres to the primary
/// selection (the last-selected control, as VS's white/hollow handle marks it), make controls the same
/// size, space them evenly, or center a control in its parent. All of it is a command layer over
/// <see cref="Control.SetBounds"/> and <see cref="DesignDocument.Snapshot"/> — no new model concepts.
/// </summary>
public partial class DesignerForm
{
    /// <summary>Free-standing selected controls (docked ones ignore Left/Top/Width/Height, so they're excluded).</summary>
    List<Control> Movable => Document is { } doc ? [.. doc.Selection.Where(c => c.Align == Align.None)] : [];

    /// <summary>Aligns every free-standing selected control's edge (or centre line) to the primary selection's.</summary>
    void AlignEdges(Func<Control, int> edge, Action<Control, int> move)
    {
        var targets = Movable;
        if (Document is not { } doc || targets.Count < 2 || !targets.Contains(doc.Primary)) return;
        int reference = edge(doc.Primary);
        doc.Snapshot();
        foreach (var c in targets) move(c, reference);
        doc.NotifyChanged();
    }

    void mnuAlignLefts_Click(object sender) => AlignEdges(c => c.Left, (c, v) => c.SetBounds(v, c.Top, c.Width, c.Height));
    void mnuAlignRights_Click(object sender) => AlignEdges(c => c.Left + c.Width, (c, v) => c.SetBounds(v - c.Width, c.Top, c.Width, c.Height));
    void mnuAlignTops_Click(object sender) => AlignEdges(c => c.Top, (c, v) => c.SetBounds(c.Left, v, c.Width, c.Height));
    void mnuAlignBottoms_Click(object sender) => AlignEdges(c => c.Top + c.Height, (c, v) => c.SetBounds(c.Left, v - c.Height, c.Width, c.Height));
    void mnuAlignCenters_Click(object sender) => AlignEdges(c => c.Left + c.Width / 2, (c, v) => c.SetBounds(v - c.Width / 2, c.Top, c.Width, c.Height));
    void mnuAlignMiddles_Click(object sender) => AlignEdges(c => c.Top + c.Height / 2, (c, v) => c.SetBounds(c.Left, v - c.Height / 2, c.Width, c.Height));

    /// <summary>Sizes every free-standing selected control to the primary selection's width and/or height.</summary>
    void MakeSameSize(bool width, bool height)
    {
        var targets = Movable;
        if (Document is not { } doc || targets.Count < 2 || !targets.Contains(doc.Primary)) return;
        var primary = doc.Primary;
        doc.Snapshot();
        foreach (var c in targets) c.SetBounds(c.Left, c.Top, width ? primary.Width : c.Width, height ? primary.Height : c.Height);
        doc.NotifyChanged();
    }

    void mnuSameWidth_Click(object sender) => MakeSameSize(width: true, height: false);
    void mnuSameHeight_Click(object sender) => MakeSameSize(width: false, height: true);
    void mnuSameBoth_Click(object sender) => MakeSameSize(width: true, height: true);

    /// <summary>Redistributes the middle controls so the gaps between all of them are equal; the two end
    /// controls (by position) stay put.</summary>
    void SpaceEvenly(bool horizontal)
    {
        var targets = Movable;
        if (Document is not { } doc || targets.Count < 3) return;
        doc.Snapshot();
        var sorted = horizontal ? targets.OrderBy(c => c.Left).ToList() : targets.OrderBy(c => c.Top).ToList();
        int span = horizontal
            ? sorted[^1].Left + sorted[^1].Width - sorted[0].Left
            : sorted[^1].Top + sorted[^1].Height - sorted[0].Top;
        int sumSizes = horizontal ? sorted.Sum(c => c.Width) : sorted.Sum(c => c.Height);
        double gap = (double)(span - sumSizes) / (sorted.Count - 1);
        double pos = horizontal ? sorted[0].Left + sorted[0].Width + gap : sorted[0].Top + sorted[0].Height + gap;
        for (int i = 1; i < sorted.Count - 1; i++)
        {
            var c = sorted[i];
            if (horizontal) c.SetBounds((int)Math.Round(pos), c.Top, c.Width, c.Height);
            else c.SetBounds(c.Left, (int)Math.Round(pos), c.Width, c.Height);
            pos += (horizontal ? c.Width : c.Height) + gap;
        }
        doc.NotifyChanged();
    }

    void mnuSpaceHorizontally_Click(object sender) => SpaceEvenly(horizontal: true);
    void mnuSpaceVertically_Click(object sender) => SpaceEvenly(horizontal: false);

    /// <summary>Centers every free-standing selected control within its own parent (Delphi/VS "Center in Form",
    /// generalised to any container since a selection isn't always direct children of the form).</summary>
    void CenterInParent(bool horizontal, bool vertical)
    {
        var targets = Movable.Where(c => c.Parent != null).ToList();
        if (Document is not { } doc || targets.Count == 0) return;
        doc.Snapshot();
        foreach (var c in targets)
        {
            var p = c.Parent!;
            int left = horizontal ? (p.Width - c.Width) / 2 : c.Left;
            int top = vertical ? (p.Height - c.Height) / 2 : c.Top;
            c.SetBounds(left, top, c.Width, c.Height);
        }
        doc.NotifyChanged();
    }

    void mnuCenterHorizontally_Click(object sender) => CenterInParent(horizontal: true, vertical: false);
    void mnuCenterVertically_Click(object sender) => CenterInParent(horizontal: false, vertical: true);
}
