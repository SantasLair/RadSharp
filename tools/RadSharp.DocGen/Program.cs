using System.Text;
using RadSharp;

// The control reference, generated from the same metadata the form reader, writer and inspector use.
var output = args.Length > 0 ? args[0] : Path.Combine("docs", "controls.md");
var control = ComponentRegistry.Find(typeof(Control))!;
var classes = ComponentRegistry.All
    .Where(c => c.Type.Assembly == typeof(Control).Assembly && c.CanCreate && c.Type != typeof(Control))
    .OrderBy(c => c.Name, StringComparer.Ordinal)
    .ToList();

var sb = new StringBuilder();
sb.AppendLine("# Control reference");
sb.AppendLine();
sb.AppendLine("Generated from the component metadata by `tools/RadSharp.DocGen`; do not edit by hand.");
sb.AppendLine("Every control also has the [common properties and events](#common-to-every-control) listed at the end.");
sb.AppendLine("Defaults are the class defaults at 100 % scale; only values that differ are written to form files.");
sb.AppendLine();
foreach (var c in classes)
    sb.AppendLine($"- [{c.Name}](#{c.Name.ToLowerInvariant()}){(typeof(Component).IsAssignableFrom(c.Type) ? " (non-visual)" : "")}");
sb.AppendLine();

void Table(ComponentClass cls, IEnumerable<PropertyMeta> props, IEnumerable<EventMeta> events)
{
    var p = props.ToList();
    if (p.Count > 0)
    {
        sb.AppendLine("| Property | Type | Default | Category |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var m in p)
        {
            var type = m.Type == PropertyType.Enum ? "enum: " + string.Join(", ", m.Values!) : m.Type.ToString().ToLowerInvariant();
            var def = m.FormatValue(cls.Prototype).Replace("\n", "\\n").Replace("|", "\\|");
            sb.AppendLine($"| `{m.Name}` | {type} | {(def.Length == 0 ? "\"\"" : $"`{def}`")} | {m.Category} |");
        }
        sb.AppendLine();
    }
    var e = events.ToList();
    if (e.Count > 0)
    {
        sb.AppendLine("| Event | Handler parameters |");
        sb.AppendLine("|---|---|");
        foreach (var m in e) sb.AppendLine($"| `{m.Name}` | `({m.Parameters})` |");
        sb.AppendLine();
    }
}

// Behaviour the metadata can't show (keyboard, selection), kept here so the file stays generated.
var grid =
    "Rows share one height (`defaultRowHeight`); columns have their own widths (`ColWidths[i]`, at 100 %) or come from the " +
    "`Columns` collection (`GridColumn`: `Title`, `Width`, `Alignment`, `MinWidth`), which gives the first fixed " +
    "row its titles and sets `colCount` to its count. `Columns` is saved in form files as a `\"columns\"` array (one " +
    "object per grid column, only non-default values; loaded after the other members, so file order never matters) " +
    "and is edited in the designer from the Object Inspector's `columns` row. `sortColumn` is kept as set, but " +
    "shrinking `colCount` to or below it resets it to -1. Fixed rows/columns stay put while the rest scrolls; painting " +
    "visits only the cells in view. Mouse: click, drag, Shift+click and (with `multiSelect`) Ctrl/⌘+click select cells " +
    "and ranges; with `rangeSelect` a header click selects its column, a row header its row, the corner everything; " +
    "with `autoSort` a header click sets `sortColumn`/`sortDescending` (the arrow) instead; `colSizing` lets header " +
    "borders be dragged. Keys: arrows, Home/End, Ctrl+Home/End, PageUp/PageDown, Tab/Shift+Tab (`wantTabs`); Shift " +
    "extends; Ctrl/⌘+A selects all; Ctrl/⌘+C copies the selection as tab-separated text (`SelectionAsText()`). With " +
    "`editing`, Enter, F2, a double click or typing opens an in-place `Edit` (`InplaceEditor`): Enter commits, Esc " +
    "cancels, Up/Down/Tab commit and move; `onGetEditText` supplies or refuses the text, `onSetEditText` stores or " +
    "refuses it, `onSelectCell` can refuse a cell. From code: `Col`/`Row`, `FocusCell`, `CellRect`, `CellAt`, `TopRow`, " +
    "`SelectedRanges`, `IsCellSelected`, `ScrollCellIntoView`, and `Selection`, the `CellSelection` model (its " +
    "coordinates leave out the fixed cells).";
var notes = new Dictionary<string, string>
{
    ["DrawGrid"] =
        "An owner-drawn grid (Delphi's TDrawGrid): `onDrawCell` paints each visible cell after the grid drew its " +
        "background, selection and lines. Editing and copying get their text from `onGetEditText`. " + grid,
    ["StringGrid"] =
        "A grid of text cells (Delphi's TStringGrid): `Cells[col, row]` holds the text in memory. Virtual mode: an " +
        "`onGetCell` handler supplies each cell's text as it is painted or copied, so 100 000 rows need no storage, and " +
        "edits go to `onSetEditText` only. With `autoSort` a header click sorts the rows in memory (stable; numbers by " +
        "value, else text ignoring case; `onCompare` overrides; `SortRows(col, descending)` from code); in virtual mode " +
        "the `onColumnClick` handler sorts the data. " + grid,
    ["ListBox"] =
        "Keys: Up/Down, PageUp/PageDown and Home/End move the current item (`ItemIndex`) and scroll it into view; typing " +
        "a letter moves to the next item starting with it. With `multiSelect` and `extendedSelect` (the default): a click " +
        "selects one item, Ctrl/⌘+click toggles one, Shift+click and Shift+arrows select the range from the anchor, " +
        "dragging sweeps a range, Ctrl/⌘+arrows move the focus alone, Ctrl/⌘+Space toggles it, Ctrl/⌘+A selects all. With " +
        "`multiSelect` alone, each click or Space toggles one item and the arrows move the focus. From code: " +
        "`IsSelected(i)`/`SetSelected(i, bool)`, `SelectedIndices`, `SelectedItems`, `SelectedCount`, `SelectAll()`, " +
        "`ClearSelection()`, `AddItem`/`InsertItem`/`DeleteItem` with an optional tag (`GetItemTag`), and `Selection`, " +
        "the `ItemSelection` model (selected ranges, current item, anchor). `style` = `variable` makes row heights per item: " +
        "handle `onMeasureItem` (set `e.Height`, at 100%) and call `InvalidateItemHeights()` after the data it reads changes. " +
        "The default `fixed` is uniform `itemHeight` rows with O(1) lookups. Runtime: `Count`.",
    ["ComboBox"] = """`style` = `dropDown` makes the text part an editable `Edit` (`EditPart`); `text` is saved, `Text` is the live text and `ItemIndex` follows an exact match. `autoComplete` (default on) completes typed text from the items, selecting the added part. Backspace, Delete and paste don't complete, and nothing completes past an item that already equals the typed text. Alt+Down or F4 opens the list; Up/Down cycle the items. A drop-down-list combo (the default) type-aheads on a typed letter.""",
    ["CheckListBox"] = """A `ListBox` with a check box per item. Clicking the box toggles it and also selects the row; clicking the text only selects. Space toggles every selected item. `checked` (indices, saved only when non-empty), `allowGrayed`, `onClickCheck`. Runtime: `Checked[i]`, `States[i]`, `CheckedIndices`, `ClearChecks()`. Items keep their check states when `Items` is reassigned (checks past the new end are dropped).""",
    ["ListView"] = """A report-style list (Delphi's TListView, vsReport) on `ScrollView`: both scroll bars; the header stays at the top and scrolls sideways with the rows. `Columns` is a `GridColumns` collection (`GridColumn`: `Title`, `Width` (0: 100), `Alignment`, `MinWidth`), saved as a `"columns"` array after the other members (old `"Title:width"` strings still load) and edited from the Object Inspector's `columns` row; `Columns = ["Name:120", "Kind"]` works in code. Rows come from `Items` (call `ItemsChanged()` after editing it) or, in virtual mode, from an `onGetItem` handler asked only for the rows being painted, with `ItemCount` as the row count. A header click raises `onColumnClick`; with `autoSort` it first sets `sortColumn` (again: flips `sortDescending`) and sorts `Items` stably (numbers by value, else text ignoring case; `onCompare` overrides; `SortItems(col, descending)` from code), keeping the selection on its items; in virtual mode the handler sorts. `sortColumn` is kept as set, but removing columns past it resets it to -1. Dragging a header border resizes a column (down to `MinWidth`, default 20). `checkboxes` adds a check box per row (`ListViewItem.Checked`; a click on it or Space toggles it, raising `onItemChecked(index, isChecked)`); `images` names an ImageList for `ListViewItem.ImageIndex`. Selection: `multiSelect` (Ctrl/⌘+click toggles, Shift+click and Shift+arrows select ranges, dragging sweeps, Ctrl/⌘+A selects all, Ctrl/⌘+Space toggles). Keys: Up/Down, PageUp/PageDown, Home/End; Left/Right scroll sideways; Enter raises `onDblClick`; typing jumps to the next caption starting with the typed text. In virtual mode that search is bounded: an `onFindItem` handler (`e.Text`, `e.StartIndex`; set `e.Index`, -1 for none) answers it, and without one at most 1000 rows after the current one are examined per key, so 100 000 rows never stall a keypress. From code: `ItemIndex`, `Selected`, `IsSelected`/`SetSelected`, `SelectedIndices`, `ScrollIntoView(i)`, `ItemRect`, `TopIndex`, and `Selection`, the `ItemSelection` model.""",
    ["TreeView"] = """A tree (Delphi's TTreeView) on `ScrollView`, with both scroll bars. Nodes come from code (`Nodes`, a `TreeNodes` list; `TreeNode.Add`) or the form file's `"nodes"` array (nested; `text`, `checked`, `expanded`, `hasChildren`, `imageIndex`, `nodes`), edited in the designer from the Object Inspector's `nodes` row. The rows are the visible (expanded) nodes and the selection is an `ItemSelection` over them: expanding inserts rows, collapsing removes them (a hidden current node gives way to the collapsed one). `onChange` for a code-made tree change (a removed or hidden current node) never runs inside layout or painting: it fires at the next call that reads the tree, or from the UI-thread queue once painting has found it, once per change. `onExpanding`/`onCollapsing` can cancel; for lazy loading set `TreeNode.HasChildren` and add the children in `onExpanding` (an expand that brings none removes the arrow). `checkboxes` adds a check box per node (click or Space; `onNodeChecked`); `images` names an ImageList for `TreeNode.ImageIndex`. With `readOnly` off (it defaults to true), F2 or a slow second click on the selected node renames it in an in-place `Edit`: `onEditing` can refuse or change the start text, `onEdited` can change the text or refuse it (the editor stays open); Enter commits, Esc cancels. Keys: Up/Down, PageUp/PageDown, Home/End; Right expands, then goes to the first child; Left collapses, then goes to the parent; `+`/`-` expand/collapse, `*` expands everything below; typing jumps to the next visible node starting with the typed text. With `multiSelect`: Ctrl/⌘+click, Shift+click, Shift+arrows, Ctrl/⌘+A. From code: `Selected`, `SelectedNodes`, `VisibleNodes()`, `NodeAt`, `RowOf`, `Toggle`, `FullExpand`/`FullCollapse`, `BeginEdit`/`EndEdit`, `ScrollIntoView`.""",
    ["ScrollView"] =
        "A base for custom-drawn content larger than the control (override `PaintContent`). A tab stop; while focused, " +
        "Up/Down and Left/Right scroll a line, PageUp/PageDown a viewport, Home/End to the top/bottom and Ctrl+Home/" +
        "Ctrl+End to the corners. The wheel scrolls three lines (Shift+wheel: horizontally). Subclasses that use keys for " +
        "a cursor override `HandleKey`; `LineStepX`/`LineStepY`/`PageStepY`, `MeasureContent`, `ScrollBy` and " +
        "`ScrollIntoView` are the hooks. Lists and grids keep their selection in the UI-free `ItemSelection` (rows: " +
        "none, single, multi, extended) and `CellSelection` (cells: single, range, multi-range) models, which store " +
        "ranges, not per-item flags.",
};

var common = control.Properties.Select(p => p.Name).Concat(control.Events.Select(e => e.Name)).ToHashSet();
foreach (var c in classes)
{
    var baseName = c.Base?.Name ?? "";
    sb.AppendLine($"## {c.Name}");
    sb.AppendLine();
    if (baseName.Length > 0 && baseName != "Control")
        sb.AppendLine(classes.Any(x => x.Name == baseName)
            ? $"Inherits from [{baseName}](#{baseName.ToLowerInvariant()})."
            : $"Inherits from `{baseName}` (abstract; its members are listed here).");
    else if (typeof(Component).IsAssignableFrom(c.Type)) sb.AppendLine("A non-visual component (shown in the designer's component tray).");
    sb.AppendLine();
    if (notes.TryGetValue(c.Name, out var note))
    {
        sb.AppendLine(note);
        sb.AppendLine();
    }
    // Own and redeclared members; common Control members are listed once at the end.
    Table(c,
        c.Properties.Where(p => !common.Contains(p.Name) || control.FindProperty(p.Name) != p),
        c.Events.Where(e => !common.Contains(e.Name) || control.FindEvent(e.Name) != e));
}
sb.AppendLine("## Common to every control");
sb.AppendLine();
var sample = ComponentRegistry.Find("Panel")!;
Table(sample, control.Properties, control.Events);

File.WriteAllText(output, sb.ToString().Replace("\r\n", "\n"));
Console.WriteLine($"Wrote {output} ({classes.Count} classes)");
