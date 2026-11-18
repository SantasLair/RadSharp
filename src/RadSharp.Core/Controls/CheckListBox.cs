using RadSharp.Drawing;

namespace RadSharp;

/// <summary>The state of one item of a <see cref="CheckListBox"/> (Delphi's TCheckBoxState).</summary>
public enum CheckState { Unchecked, Checked, Grayed }

/// <summary>
/// A <see cref="ListBox"/> with a check box in front of every item (Delphi's TCheckListBox). Clicking the box toggles the
/// item, clicking its text only selects it; Space toggles every selected item (or the current one). The check state is
/// separate from the selection, and sparse, so a big list costs only its checked items.
/// </summary>
/// <remarks>
/// With <see cref="AllowGrayed"/> a toggle goes Unchecked, Checked, Grayed, Unchecked; otherwise Unchecked and Checked.
/// <see cref="ClickCheck"/> fires once per user action, after all states changed. Setting <see cref="ListBox.Items"/> keeps the
/// check states of the items that remain (a form file loads <c>checked</c> before <c>items</c>); use <see cref="ClearChecks"/> to reset them. Plain
/// Space always toggles checks here, so a plain multi-select list that wants Space for selection uses Ctrl+Space.
/// </remarks>
public class CheckListBox : ListBox
{
    readonly Dictionary<int, CheckState> _states = [];

    public CheckListBox()
    {
        Checked = new CheckFlags(this);
        States = new CheckStates(this);
    }

    /// <summary>Per-item checked flag (Delphi's <c>Checked[i]</c>): true only for <see cref="CheckState.Checked"/>.</summary>
    public CheckFlags Checked { get; }

    /// <summary>Per-item state (Delphi's <c>State[i]</c>), including <see cref="CheckState.Grayed"/>.</summary>
    public CheckStates States { get; }

    /// <summary>Allow the third, grayed state when toggling (Delphi's AllowGrayed).</summary>
    [Published(Category = "Behavior")] public bool AllowGrayed { get; set; }

    /// <summary>
    /// The indices of the checked items, ascending: the form file's <c>"checked"</c> array. Setting it replaces every state
    /// (indices below 0 are ignored; ones past the last item wait for items to arrive).
    /// </summary>
    [Published("checked", Category = "Behavior")]
    public int[] CheckedIndices
    {
        get => [.. _states.Where(p => p.Value == CheckState.Checked && p.Key < Count).Select(p => p.Key).Order()];
        set
        {
            _states.Clear();
            foreach (int i in value ?? []) if (i >= 0) _states[i] = CheckState.Checked;
            Invalidate();
        }
    }

    /// <summary>Raised after the user toggled one or more check boxes (mouse or Space), once per action.</summary>
    [Published] public event NotifyEvent? ClickCheck;

    /// <summary>Unchecks every item.</summary>
    public void ClearChecks()
    {
        if (_states.Count == 0) return;
        _states.Clear();
        Invalidate();
    }

    /// <summary>How many items are <see cref="CheckState.Checked"/>.</summary>
    public int CheckedCount => _states.Count(p => p.Value == CheckState.Checked && p.Key < Count);

    CheckState GetState(int index) => _states.GetValueOrDefault(index);

    void SetState(int index, CheckState state)
    {
        if (index < 0 || index >= Count) return;
        if (state == CheckState.Unchecked) _states.Remove(index);
        else _states[index] = state;
        Invalidate();
    }

    CheckState Next(CheckState state) => state switch
    {
        CheckState.Unchecked => CheckState.Checked,
        CheckState.Checked => AllowGrayed ? CheckState.Grayed : CheckState.Unchecked,
        _ => CheckState.Unchecked,
    };

    /// <summary>Toggles the items as a user action and raises <see cref="ClickCheck"/>.</summary>
    void UserToggle(IEnumerable<int> indices)
    {
        bool any = false;
        foreach (int i in indices)
        {
            if (i < 0 || i >= Count) continue;
            SetState(i, Next(GetState(i)));
            any = true;
        }
        if (any) ClickCheck?.Invoke(this);
    }

    /// <summary>Toggles one item as the user would (a click on its box) and raises <see cref="ClickCheck"/>.</summary>
    public void Toggle(int index) => UserToggle([index]);

    // ---- Items keep their states ----

    protected override void OnItemsReplaced()
    {
        // The states stay with their indices, but a shorter list drops the ones past its end (they would reappear on a longer one).
        foreach (var key in _states.Keys.Where(k => k >= Count).ToList()) _states.Remove(key);
    }

    protected override void OnItemInserted(int index)
    {
        base.OnItemInserted(index);
        if (_states.Count == 0) return;
        var moved = _states.Where(p => p.Key >= index).OrderByDescending(p => p.Key).ToList();
        foreach (var (key, _) in moved) _states.Remove(key);
        foreach (var (key, state) in moved) _states[key + 1] = state;
    }

    protected override void OnItemDeleted(int index)
    {
        base.OnItemDeleted(index);
        if (_states.Count == 0) return;
        _states.Remove(index);
        var moved = _states.Where(p => p.Key > index).OrderBy(p => p.Key).ToList();
        foreach (var (key, _) in moved) _states.Remove(key);
        foreach (var (key, state) in moved) _states[key - 1] = state;
    }

    // ---- Geometry ----

    int BoxSize => Px(14);

    /// <summary>The check box of row <paramref name="index"/> in this control's coordinates.</summary>
    public Rect CheckBoxRect(int index)
    {
        var row = ItemRect(index);
        int s = BoxSize;
        return new Rect(row.X + Px(6), row.Y + (row.Height - s) / 2, s, s);
    }

    // ---- Input ----

    protected internal override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);   // selects the row, like a click on its text
        if (e.Button != MouseButton.Left || IsScrolling || IsOverScrollBar(e.X, e.Y)) return;
        int i = IndexAt(e.X, e.Y);
        if (i < 0) return;
        var box = CheckBoxRect(i);
        if (e.X >= box.X - Px(2) && e.X < box.X + box.Width + Px(2) && e.Y >= box.Y && e.Y < box.Y + box.Height) UserToggle([i]);
    }

    protected override bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.Modifiers == KeyModifiers.None && Count > 0)
        {
            int[] targets = SelectedCount > 0 ? SelectedIndices : ItemIndex >= 0 ? [ItemIndex] : [];
            UserToggle(targets);
            return true;
        }
        return base.HandleKey(e);
    }

    // ---- Painting ----

    protected override void PaintItemContent(Canvas canvas, int index, Rect textRect, Color textColor)
    {
        var t = canvas.Theme;
        bool enabled = IsEffectivelyEnabled;
        var box = CheckBoxRect(index);
        // CheckBoxRect is in control coordinates; PaintContent has translated the canvas to match (see ListBox.PaintContent).
        float radius = canvas.Px(3);
        switch (GetState(index))
        {
            case CheckState.Checked:
                canvas.FillRoundRect(box, radius, enabled ? t.Accent : t.TextDisabled);
                canvas.DrawCheckMark(box, t.OnAccent, Math.Max(1.5f, canvas.Px(2)));
                break;
            case CheckState.Grayed:
                canvas.FillRoundRect(box, radius, t.TextDisabled);
                int bar = Math.Max(2, canvas.Px(2)), inset = canvas.Px(3);
                canvas.FillRect(new Rect(box.X + inset, box.Y + (box.Height - bar) / 2, box.Width - 2 * inset, bar), t.OnAccent);
                break;
            default:
                canvas.FillRoundRect(box, radius, enabled ? t.InputBackground : t.FormBackground);
                canvas.DrawRoundBorder(box, radius, enabled ? t.InputBorder : t.TextDisabled, canvas.Px(1));
                break;
        }
        int gap = box.X + box.Width + canvas.Px(6) - textRect.X;
        base.PaintItemContent(canvas, index, new Rect(textRect.X + gap, textRect.Y, Math.Max(0, textRect.Width - gap), textRect.Height), textColor);
    }

    /// <summary>Indexer access to the checked flag of each item.</summary>
    public sealed class CheckFlags
    {
        readonly CheckListBox _owner;
        internal CheckFlags(CheckListBox owner) => _owner = owner;

        /// <summary>True if the item is <see cref="CheckState.Checked"/>; setting it checks or unchecks (no <see cref="ClickCheck"/>).</summary>
        public bool this[int index]
        {
            get => _owner.GetState(index) == CheckState.Checked;
            set => _owner.SetState(index, value ? CheckState.Checked : CheckState.Unchecked);
        }
    }

    /// <summary>Indexer access to the <see cref="CheckState"/> of each item.</summary>
    public sealed class CheckStates
    {
        readonly CheckListBox _owner;
        internal CheckStates(CheckListBox owner) => _owner = owner;

        public CheckState this[int index]
        {
            get => _owner.GetState(index);
            set => _owner.SetState(index, value);
        }
    }
}
