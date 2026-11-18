# Control reference

Generated from the component metadata by `tools/RadSharp.DocGen`; do not edit by hand.
Every control also has the [common properties and events](#common-to-every-control) listed at the end.
Defaults are the class defaults at 100 % scale; only values that differ are written to form files.

- [Button](#button)
- [CheckBox](#checkbox)
- [CheckListBox](#checklistbox)
- [ColorBox](#colorbox)
- [ComboBox](#combobox)
- [DrawGrid](#drawgrid)
- [Edit](#edit)
- [FolderDialog](#folderdialog) (non-visual)
- [Form](#form)
- [GroupBox](#groupbox)
- [Image](#image)
- [ImageList](#imagelist) (non-visual)
- [Label](#label)
- [ListBox](#listbox)
- [ListView](#listview)
- [MainMenu](#mainmenu)
- [Memo](#memo)
- [OpenDialog](#opendialog) (non-visual)
- [PageControl](#pagecontrol)
- [PaintBox](#paintbox)
- [Panel](#panel)
- [PopupMenu](#popupmenu) (non-visual)
- [ProgressBar](#progressbar)
- [PropertyGrid](#propertygrid)
- [RadioButton](#radiobutton)
- [SaveDialog](#savedialog) (non-visual)
- [ScrollBar](#scrollbar)
- [ScrollBox](#scrollbox)
- [ScrollView](#scrollview)
- [SpinEdit](#spinedit)
- [Splitter](#splitter)
- [StatusBar](#statusbar)
- [StringGrid](#stringgrid)
- [TabSheet](#tabsheet)
- [Timer](#timer) (non-visual)
- [ToolBar](#toolbar)
- [ToolButton](#toolbutton)
- [ToolSeparator](#toolseparator)
- [TrackBar](#trackbar)
- [TreeView](#treeview)

## Button


| Property | Type | Default | Category |
|---|---|---|---|
| `cancel` | bool | `false` | Behavior |
| `caption` | string | "" | Appearance |
| `color` | color | "" | Appearance |
| `default` | bool | `false` | Behavior |
| `flat` | bool | `false` | Appearance |
| `fontColor` | color | "" | Appearance |
| `hoverColor` | color | "" | Appearance |
| `imageIndex` | int | `-1` | Appearance |
| `images` | string | "" | Appearance |
| `modalResult` | enum: none, ok, cancel, abort, retry, ignore, yes, no, close | `none` | Behavior |
| `primary` | bool | `false` | Appearance |

## CheckBox

Inherits from `ToggleControl` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |
| `checked` | bool | `false` | Behavior |
| `imageIndex` | int | `-1` | Appearance |
| `images` | string | "" | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## CheckListBox

Inherits from [ListBox](#listbox).

A `ListBox` with a check box per item. Clicking the box toggles it and also selects the row; clicking the text only selects. Space toggles every selected item. `checked` (indices, saved only when non-empty), `allowGrayed`, `onClickCheck`. Runtime: `Checked[i]`, `States[i]`, `CheckedIndices`, `ClearChecks()`. Items keep their check states when `Items` is reassigned (checks past the new end are dropped).

| Property | Type | Default | Category |
|---|---|---|---|
| `allowGrayed` | bool | `false` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `checked` | ints | "" | Behavior |
| `color` | color | "" | Appearance |
| `extendedSelect` | bool | `true` | Behavior |
| `itemHeight` | int | `24` | Appearance |
| `itemIndex` | int | `-1` | Behavior |
| `items` | strings | "" | Behavior |
| `multiSelect` | bool | `false` | Behavior |
| `style` | enum: fixed, variable | `fixed` | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onClickCheck` | `(Control sender)` |
| `onDblClick` | `(Control sender)` |
| `onDrawItem` | `(Control sender, Canvas canvas, int index, Rect rect, bool selected)` |
| `onMeasureItem` | `(Control sender, MeasureItemEventArgs e)` |

## ColorBox

Inherits from [ComboBox](#combobox).

| Property | Type | Default | Category |
|---|---|---|---|
| `autoComplete` | bool | `true` | Behavior |
| `dropDownCount` | int | `8` | Behavior |
| `itemIndex` | int | `0` | Behavior |
| `items` | strings | `Black\nMaroon\nGreen\nOlive\nNavy\nPurple\nTeal\nGray\nSilver\nRed\nLime\nYellow\nBlue\nFuchsia\nAqua\nWhite` | Behavior |
| `selected` | color | `#000000` | Behavior |
| `style` | enum: dropDownList, dropDown | `dropDownList` | Behavior |
| `text` | string | "" | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## ComboBox


`style` = `dropDown` makes the text part an editable `Edit` (`EditPart`); `text` is saved, `Text` is the live text and `ItemIndex` follows an exact match. `autoComplete` (default on) completes typed text from the items, selecting the added part. Backspace, Delete and paste don't complete, and nothing completes past an item that already equals the typed text. Alt+Down or F4 opens the list; Up/Down cycle the items. A drop-down-list combo (the default) type-aheads on a typed letter.

| Property | Type | Default | Category |
|---|---|---|---|
| `autoComplete` | bool | `true` | Behavior |
| `dropDownCount` | int | `8` | Behavior |
| `itemIndex` | int | `-1` | Behavior |
| `items` | strings | "" | Behavior |
| `style` | enum: dropDownList, dropDown | `dropDownList` | Behavior |
| `text` | string | "" | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## DrawGrid

Inherits from `CustomGrid` (abstract; its members are listed here).

An owner-drawn grid (Delphi's TDrawGrid): `onDrawCell` paints each visible cell after the grid drew its background, selection and lines. Editing and copying get their text from `onGetEditText`. Rows share one height (`defaultRowHeight`); columns have their own widths (`ColWidths[i]`, at 100 %) or come from the `Columns` collection (`GridColumn`: `Title`, `Width`, `Alignment`, `MinWidth`), which gives the first fixed row its titles and sets `colCount` to its count. `Columns` is saved in form files as a `"columns"` array (one object per grid column, only non-default values; loaded after the other members, so file order never matters) and is edited in the designer from the Object Inspector's `columns` row. `sortColumn` is kept as set, but shrinking `colCount` to or below it resets it to -1. Fixed rows/columns stay put while the rest scrolls; painting visits only the cells in view. Mouse: click, drag, Shift+click and (with `multiSelect`) Ctrl/⌘+click select cells and ranges; with `rangeSelect` a header click selects its column, a row header its row, the corner everything; with `autoSort` a header click sets `sortColumn`/`sortDescending` (the arrow) instead; `colSizing` lets header borders be dragged. Keys: arrows, Home/End, Ctrl+Home/End, PageUp/PageDown, Tab/Shift+Tab (`wantTabs`); Shift extends; Ctrl/⌘+A selects all; Ctrl/⌘+C copies the selection as tab-separated text (`SelectionAsText()`). With `editing`, Enter, F2, a double click or typing opens an in-place `Edit` (`InplaceEditor`): Enter commits, Esc cancels, Up/Down/Tab commit and move; `onGetEditText` supplies or refuses the text, `onSetEditText` stores or refuses it, `onSelectCell` can refuse a cell. From code: `Col`/`Row`, `FocusCell`, `CellRect`, `CellAt`, `TopRow`, `SelectedRanges`, `IsCellSelected`, `ScrollCellIntoView`, and `Selection`, the `CellSelection` model (its coordinates leave out the fixed cells).

| Property | Type | Default | Category |
|---|---|---|---|
| `autoSort` | bool | `false` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `colCount` | int | `5` | Behavior |
| `colSizing` | bool | `false` | Behavior |
| `color` | color | "" | Appearance |
| `defaultColWidth` | int | `64` | Appearance |
| `defaultRowHeight` | int | `24` | Appearance |
| `editing` | bool | `false` | Behavior |
| `fixedCols` | int | `1` | Behavior |
| `fixedRows` | int | `1` | Behavior |
| `gridLines` | bool | `true` | Appearance |
| `multiSelect` | bool | `false` | Behavior |
| `rangeSelect` | bool | `true` | Behavior |
| `rowCount` | int | `5` | Behavior |
| `rowSelect` | bool | `false` | Behavior |
| `sortColumn` | int | `-1` | Appearance |
| `sortDescending` | bool | `false` | Appearance |
| `wantTabs` | bool | `false` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onColumnClick` | `(Control sender, int column)` |
| `onDblClick` | `(Control sender)` |
| `onDrawCell` | `(Control sender, Canvas canvas, int col, int row, Rect rect, GridDrawState state)` |
| `onGetEditText` | `(Control sender, GridTextEventArgs e)` |
| `onSelectCell` | `(Control sender, SelectCellEventArgs e)` |
| `onSetEditText` | `(Control sender, GridTextEventArgs e)` |

## Edit

Inherits from `EditBase` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `alignment` | enum: left, center, right | `left` | Appearance |
| `autoSelect` | bool | `true` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `color` | color | "" | Appearance |
| `fontColor` | color | "" | Appearance |
| `hideSelection` | bool | `true` | Behavior |
| `maxLength` | int | `0` | Behavior |
| `passwordChar` | string | "" | Behavior |
| `placeholder` | string | "" | Appearance |
| `readOnly` | bool | `false` | Behavior |
| `text` | string | "" | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## FolderDialog

Inherits from `FileDialog` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `fileName` | string | "" | Behavior |
| `filter` | string | "" | Behavior |
| `initialDir` | string | "" | Behavior |
| `title` | string | "" | Behavior |

## Form


| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: sizable, single, dialog, tool, none | `sizable` | Window |
| `caption` | string | "" | Appearance |
| `color` | color | "" | Appearance |
| `darkTitleBar` | bool | `false` | Window |
| `icon` | string | "" | Window |
| `keyPreview` | bool | `false` | Behavior |
| `resizable` | bool | `true` | Window |
| `startPosition` | enum: default, centerScreen, centerOwner, manual | `default` | Window |
| `topMost` | bool | `false` | Window |
| `windowState` | enum: normal, maximized, minimized, fullScreen | `normal` | Window |

| Event | Handler parameters |
|---|---|
| `onClose` | `(Control sender)` |
| `onCloseQuery` | `(Control sender, CloseQueryEventArgs e)` |
| `onShow` | `(Control sender)` |

## GroupBox


| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |

## Image


| Property | Type | Default | Category |
|---|---|---|---|
| `picture` | string | "" | Appearance |
| `scaling` | enum: none, stretch, fit, center | `fit` | Appearance |

## ImageList

Inherits from `Component` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `images` | strings | "" | Appearance |

## Label


| Property | Type | Default | Category |
|---|---|---|---|
| `alignment` | enum: left, center, right | `left` | Appearance |
| `caption` | string | "" | Appearance |
| `ellipsis` | bool | `false` | Appearance |
| `focusControl` | string | "" | Behavior |
| `fontBold` | bool | `false` | Appearance |
| `fontColor` | color | "" | Appearance |
| `fontSize` | int | `0` | Appearance |
| `layout` | enum: top, center, bottom | `top` | Appearance |
| `muted` | bool | `false` | Appearance |
| `showAccelChar` | bool | `true` | Appearance |
| `wordWrap` | bool | `false` | Appearance |

## ListBox

Inherits from [ScrollView](#scrollview).

Keys: Up/Down, PageUp/PageDown and Home/End move the current item (`ItemIndex`) and scroll it into view; typing a letter moves to the next item starting with it. With `multiSelect` and `extendedSelect` (the default): a click selects one item, Ctrl/⌘+click toggles one, Shift+click and Shift+arrows select the range from the anchor, dragging sweeps a range, Ctrl/⌘+arrows move the focus alone, Ctrl/⌘+Space toggles it, Ctrl/⌘+A selects all. With `multiSelect` alone, each click or Space toggles one item and the arrows move the focus. From code: `IsSelected(i)`/`SetSelected(i, bool)`, `SelectedIndices`, `SelectedItems`, `SelectedCount`, `SelectAll()`, `ClearSelection()`, `AddItem`/`InsertItem`/`DeleteItem` with an optional tag (`GetItemTag`), and `Selection`, the `ItemSelection` model (selected ranges, current item, anchor). `style` = `variable` makes row heights per item: handle `onMeasureItem` (set `e.Height`, at 100%) and call `InvalidateItemHeights()` after the data it reads changes. The default `fixed` is uniform `itemHeight` rows with O(1) lookups. Runtime: `Count`.

| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `color` | color | "" | Appearance |
| `extendedSelect` | bool | `true` | Behavior |
| `itemHeight` | int | `24` | Appearance |
| `itemIndex` | int | `-1` | Behavior |
| `items` | strings | "" | Behavior |
| `multiSelect` | bool | `false` | Behavior |
| `style` | enum: fixed, variable | `fixed` | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onDblClick` | `(Control sender)` |
| `onDrawItem` | `(Control sender, Canvas canvas, int index, Rect rect, bool selected)` |
| `onMeasureItem` | `(Control sender, MeasureItemEventArgs e)` |

## ListView

Inherits from [ScrollView](#scrollview).

A report-style list (Delphi's TListView, vsReport) on `ScrollView`: both scroll bars; the header stays at the top and scrolls sideways with the rows. `Columns` is a `GridColumns` collection (`GridColumn`: `Title`, `Width` (0: 100), `Alignment`, `MinWidth`), saved as a `"columns"` array after the other members (old `"Title:width"` strings still load) and edited from the Object Inspector's `columns` row; `Columns = ["Name:120", "Kind"]` works in code. Rows come from `Items` (call `ItemsChanged()` after editing it) or, in virtual mode, from an `onGetItem` handler asked only for the rows being painted, with `ItemCount` as the row count. A header click raises `onColumnClick`; with `autoSort` it first sets `sortColumn` (again: flips `sortDescending`) and sorts `Items` stably (numbers by value, else text ignoring case; `onCompare` overrides; `SortItems(col, descending)` from code), keeping the selection on its items; in virtual mode the handler sorts. `sortColumn` is kept as set, but removing columns past it resets it to -1. Dragging a header border resizes a column (down to `MinWidth`, default 20). `checkboxes` adds a check box per row (`ListViewItem.Checked`; a click on it or Space toggles it, raising `onItemChecked(index, isChecked)`); `images` names an ImageList for `ListViewItem.ImageIndex`. Selection: `multiSelect` (Ctrl/⌘+click toggles, Shift+click and Shift+arrows select ranges, dragging sweeps, Ctrl/⌘+A selects all, Ctrl/⌘+Space toggles). Keys: Up/Down, PageUp/PageDown, Home/End; Left/Right scroll sideways; Enter raises `onDblClick`; typing jumps to the next caption starting with the typed text. In virtual mode that search is bounded: an `onFindItem` handler (`e.Text`, `e.StartIndex`; set `e.Index`, -1 for none) answers it, and without one at most 1000 rows after the current one are examined per key, so 100 000 rows never stall a keypress. From code: `ItemIndex`, `Selected`, `IsSelected`/`SetSelected`, `SelectedIndices`, `ScrollIntoView(i)`, `ItemRect`, `TopIndex`, and `Selection`, the `ItemSelection` model.

| Property | Type | Default | Category |
|---|---|---|---|
| `autoSort` | bool | `false` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `checkboxes` | bool | `false` | Appearance |
| `color` | color | "" | Appearance |
| `images` | string | "" | Appearance |
| `multiSelect` | bool | `false` | Behavior |
| `sortColumn` | int | `-1` | Appearance |
| `sortDescending` | bool | `false` | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onColumnClick` | `(Control sender, int column)` |
| `onCompare` | `(Control sender, ListViewCompareEventArgs e)` |
| `onDblClick` | `(Control sender)` |
| `onFindItem` | `(Control sender, ListViewFindEventArgs e)` |
| `onGetItem` | `(Control sender, ListViewItemEventArgs e)` |
| `onItemChecked` | `(Control sender, int index, bool isChecked)` |

## MainMenu


## Memo

Inherits from `EditBase` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `color` | color | "" | Appearance |
| `fontColor` | color | "" | Appearance |
| `hideSelection` | bool | `true` | Behavior |
| `maxLength` | int | `0` | Behavior |
| `placeholder` | string | "" | Appearance |
| `readOnly` | bool | `false` | Behavior |
| `text` | string | "" | Behavior |
| `wantReturns` | bool | `true` | Behavior |
| `wantTabs` | bool | `false` | Behavior |
| `wordWrap` | bool | `true` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## OpenDialog

Inherits from `FileDialog` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `fileName` | string | "" | Behavior |
| `filter` | string | "" | Behavior |
| `initialDir` | string | "" | Behavior |
| `multiSelect` | bool | `false` | Behavior |
| `title` | string | "" | Behavior |

## PageControl


| Property | Type | Default | Category |
|---|---|---|---|
| `activePageIndex` | int | `0` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## PaintBox


| Property | Type | Default | Category |
|---|---|---|---|
| `color` | color | "" | Appearance |

| Event | Handler parameters |
|---|---|
| `onPaint` | `(Control sender, Canvas canvas)` |

## Panel


| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `caption` | string | "" | Appearance |
| `color` | color | "" | Appearance |

## PopupMenu

Inherits from `Component` (abstract; its members are listed here).

| Event | Handler parameters |
|---|---|
| `onPopup` | `(Control sender)` |

## ProgressBar


| Property | Type | Default | Category |
|---|---|---|---|
| `max` | int | `100` | Behavior |
| `min` | int | `0` | Behavior |
| `position` | int | `0` | Behavior |

## PropertyGrid


| Property | Type | Default | Category |
|---|---|---|---|
| `arrangement` | enum: declared, alphabetical, categorized | `declared` | Behavior |
| `filter` | string | "" | Behavior |
| `mode` | enum: properties, events | `properties` | Behavior |
| `nameWidth` | int | `120` | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## RadioButton

Inherits from `ToggleControl` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |
| `checked` | bool | `false` | Behavior |
| `imageIndex` | int | `-1` | Appearance |
| `images` | string | "" | Appearance |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## SaveDialog

Inherits from `FileDialog` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `fileName` | string | "" | Behavior |
| `filter` | string | "" | Behavior |
| `initialDir` | string | "" | Behavior |
| `title` | string | "" | Behavior |

## ScrollBar


| Property | Type | Default | Category |
|---|---|---|---|
| `kind` | enum: horizontal, vertical | `horizontal` | Appearance |
| `largeChange` | int | `10` | Behavior |
| `max` | int | `100` | Behavior |
| `min` | int | `0` | Behavior |
| `pageSize` | int | `10` | Behavior |
| `position` | int | `0` | Behavior |
| `smallChange` | int | `1` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## ScrollBox


| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `color` | color | "" | Appearance |

## ScrollView


A base for custom-drawn content larger than the control (override `PaintContent`). A tab stop; while focused, Up/Down and Left/Right scroll a line, PageUp/PageDown a viewport, Home/End to the top/bottom and Ctrl+Home/Ctrl+End to the corners. The wheel scrolls three lines (Shift+wheel: horizontally). Subclasses that use keys for a cursor override `HandleKey`; `LineStepX`/`LineStepY`/`PageStepY`, `MeasureContent`, `ScrollBy` and `ScrollIntoView` are the hooks. Lists and grids keep their selection in the UI-free `ItemSelection` (rows: none, single, multi, extended) and `CellSelection` (cells: single, range, multi-range) models, which store ranges, not per-item flags.

| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `color` | color | "" | Appearance |

## SpinEdit

Inherits from [Edit](#edit).

| Property | Type | Default | Category |
|---|---|---|---|
| `alignment` | enum: left, center, right | `left` | Appearance |
| `autoSelect` | bool | `true` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `color` | color | "" | Appearance |
| `fontColor` | color | "" | Appearance |
| `hideSelection` | bool | `true` | Behavior |
| `increment` | int | `1` | Behavior |
| `maxLength` | int | `0` | Behavior |
| `maxValue` | int | `0` | Behavior |
| `minValue` | int | `0` | Behavior |
| `passwordChar` | string | "" | Behavior |
| `placeholder` | string | "" | Appearance |
| `readOnly` | bool | `false` | Behavior |
| `text` | string | `0` | Behavior |
| `value` | int | `0` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onValueChange` | `(Control sender)` |

## Splitter


| Property | Type | Default | Category |
|---|---|---|---|
| `minSize` | int | `30` | Behavior |

## StatusBar


| Property | Type | Default | Category |
|---|---|---|---|
| `simpleText` | string | "" | Appearance |

## StringGrid

Inherits from `CustomGrid` (abstract; its members are listed here).

A grid of text cells (Delphi's TStringGrid): `Cells[col, row]` holds the text in memory. Virtual mode: an `onGetCell` handler supplies each cell's text as it is painted or copied, so 100 000 rows need no storage, and edits go to `onSetEditText` only. With `autoSort` a header click sorts the rows in memory (stable; numbers by value, else text ignoring case; `onCompare` overrides; `SortRows(col, descending)` from code); in virtual mode the `onColumnClick` handler sorts the data. Rows share one height (`defaultRowHeight`); columns have their own widths (`ColWidths[i]`, at 100 %) or come from the `Columns` collection (`GridColumn`: `Title`, `Width`, `Alignment`, `MinWidth`), which gives the first fixed row its titles and sets `colCount` to its count. `Columns` is saved in form files as a `"columns"` array (one object per grid column, only non-default values; loaded after the other members, so file order never matters) and is edited in the designer from the Object Inspector's `columns` row. `sortColumn` is kept as set, but shrinking `colCount` to or below it resets it to -1. Fixed rows/columns stay put while the rest scrolls; painting visits only the cells in view. Mouse: click, drag, Shift+click and (with `multiSelect`) Ctrl/⌘+click select cells and ranges; with `rangeSelect` a header click selects its column, a row header its row, the corner everything; with `autoSort` a header click sets `sortColumn`/`sortDescending` (the arrow) instead; `colSizing` lets header borders be dragged. Keys: arrows, Home/End, Ctrl+Home/End, PageUp/PageDown, Tab/Shift+Tab (`wantTabs`); Shift extends; Ctrl/⌘+A selects all; Ctrl/⌘+C copies the selection as tab-separated text (`SelectionAsText()`). With `editing`, Enter, F2, a double click or typing opens an in-place `Edit` (`InplaceEditor`): Enter commits, Esc cancels, Up/Down/Tab commit and move; `onGetEditText` supplies or refuses the text, `onSetEditText` stores or refuses it, `onSelectCell` can refuse a cell. From code: `Col`/`Row`, `FocusCell`, `CellRect`, `CellAt`, `TopRow`, `SelectedRanges`, `IsCellSelected`, `ScrollCellIntoView`, and `Selection`, the `CellSelection` model (its coordinates leave out the fixed cells).

| Property | Type | Default | Category |
|---|---|---|---|
| `autoSort` | bool | `false` | Behavior |
| `borderStyle` | enum: none, single | `single` | Appearance |
| `colCount` | int | `5` | Behavior |
| `colSizing` | bool | `false` | Behavior |
| `color` | color | "" | Appearance |
| `defaultColWidth` | int | `64` | Appearance |
| `defaultRowHeight` | int | `24` | Appearance |
| `editing` | bool | `false` | Behavior |
| `fixedCols` | int | `1` | Behavior |
| `fixedRows` | int | `1` | Behavior |
| `gridLines` | bool | `true` | Appearance |
| `multiSelect` | bool | `false` | Behavior |
| `rangeSelect` | bool | `true` | Behavior |
| `rowCount` | int | `5` | Behavior |
| `rowSelect` | bool | `false` | Behavior |
| `sortColumn` | int | `-1` | Appearance |
| `sortDescending` | bool | `false` | Appearance |
| `wantTabs` | bool | `false` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onColumnClick` | `(Control sender, int column)` |
| `onCompare` | `(Control sender, GridCompareEventArgs e)` |
| `onDblClick` | `(Control sender)` |
| `onDrawCell` | `(Control sender, Canvas canvas, int col, int row, Rect rect, GridDrawState state)` |
| `onGetCell` | `(Control sender, GridTextEventArgs e)` |
| `onGetEditText` | `(Control sender, GridTextEventArgs e)` |
| `onSelectCell` | `(Control sender, SelectCellEventArgs e)` |
| `onSetEditText` | `(Control sender, GridTextEventArgs e)` |

## TabSheet


| Property | Type | Default | Category |
|---|---|---|---|
| `caption` | string | "" | Appearance |

## Timer

Inherits from `Component` (abstract; its members are listed here).

| Property | Type | Default | Category |
|---|---|---|---|
| `enabled` | bool | `true` | Behavior |
| `interval` | int | `1000` | Behavior |

| Event | Handler parameters |
|---|---|
| `onTimer` | `(Control sender)` |

## ToolBar


| Property | Type | Default | Category |
|---|---|---|---|
| `images` | string | "" | Appearance |

## ToolButton

Inherits from [Button](#button).

| Property | Type | Default | Category |
|---|---|---|---|
| `allowToggle` | bool | `false` | Behavior |
| `cancel` | bool | `false` | Behavior |
| `caption` | string | "" | Appearance |
| `color` | color | "" | Appearance |
| `default` | bool | `false` | Behavior |
| `down` | bool | `false` | Behavior |
| `flat` | bool | `false` | Appearance |
| `fontColor` | color | "" | Appearance |
| `hoverColor` | color | "" | Appearance |
| `imageIndex` | int | `-1` | Appearance |
| `images` | string | "" | Appearance |
| `modalResult` | enum: none, ok, cancel, abort, retry, ignore, yes, no, close | `none` | Behavior |
| `primary` | bool | `false` | Appearance |

## ToolSeparator


## TrackBar


| Property | Type | Default | Category |
|---|---|---|---|
| `frequency` | int | `1` | Appearance |
| `max` | int | `10` | Behavior |
| `min` | int | `0` | Behavior |
| `orientation` | enum: horizontal, vertical | `horizontal` | Appearance |
| `position` | int | `0` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |

## TreeView

Inherits from [ScrollView](#scrollview).

A tree (Delphi's TTreeView) on `ScrollView`, with both scroll bars. Nodes come from code (`Nodes`, a `TreeNodes` list; `TreeNode.Add`) or the form file's `"nodes"` array (nested; `text`, `checked`, `expanded`, `hasChildren`, `imageIndex`, `nodes`), edited in the designer from the Object Inspector's `nodes` row. The rows are the visible (expanded) nodes and the selection is an `ItemSelection` over them: expanding inserts rows, collapsing removes them (a hidden current node gives way to the collapsed one). `onChange` for a code-made tree change (a removed or hidden current node) never runs inside layout or painting: it fires at the next call that reads the tree, or from the UI-thread queue once painting has found it, once per change. `onExpanding`/`onCollapsing` can cancel; for lazy loading set `TreeNode.HasChildren` and add the children in `onExpanding` (an expand that brings none removes the arrow). `checkboxes` adds a check box per node (click or Space; `onNodeChecked`); `images` names an ImageList for `TreeNode.ImageIndex`. With `readOnly` off (it defaults to true), F2 or a slow second click on the selected node renames it in an in-place `Edit`: `onEditing` can refuse or change the start text, `onEdited` can change the text or refuse it (the editor stays open); Enter commits, Esc cancels. Keys: Up/Down, PageUp/PageDown, Home/End; Right expands, then goes to the first child; Left collapses, then goes to the parent; `+`/`-` expand/collapse, `*` expands everything below; typing jumps to the next visible node starting with the typed text. With `multiSelect`: Ctrl/⌘+click, Shift+click, Shift+arrows, Ctrl/⌘+A. From code: `Selected`, `SelectedNodes`, `VisibleNodes()`, `NodeAt`, `RowOf`, `Toggle`, `FullExpand`/`FullCollapse`, `BeginEdit`/`EndEdit`, `ScrollIntoView`.

| Property | Type | Default | Category |
|---|---|---|---|
| `borderStyle` | enum: none, single | `single` | Appearance |
| `checkboxes` | bool | `false` | Appearance |
| `color` | color | "" | Appearance |
| `images` | string | "" | Appearance |
| `multiSelect` | bool | `false` | Behavior |
| `readOnly` | bool | `true` | Behavior |

| Event | Handler parameters |
|---|---|
| `onChange` | `(Control sender)` |
| `onCollapsing` | `(Control sender, TreeNodeCancelEventArgs e)` |
| `onDblClick` | `(Control sender)` |
| `onEdited` | `(Control sender, TreeEditEventArgs e)` |
| `onEditing` | `(Control sender, TreeEditEventArgs e)` |
| `onExpanding` | `(Control sender, TreeNodeCancelEventArgs e)` |
| `onNodeChecked` | `(Control sender, TreeNode node)` |

## Common to every control

| Property | Type | Default | Category |
|---|---|---|---|
| `align` | enum: none, top, bottom, left, right, client | `none` | Layout |
| `anchors` | anchors | `left, top` | Layout |
| `cursor` | enum: default, iBeam, hand, sizeWE, sizeNS, sizeNWSE, sizeNESW, sizeAll, wait, notAllowed, crosshair | `default` | Appearance |
| `dragMode` | enum: manual, automatic | `manual` | Behavior |
| `enabled` | bool | `true` | Behavior |
| `font` | font | "" | Appearance |
| `height` | int | `30` | Layout |
| `hint` | string | "" | Behavior |
| `left` | int | `0` | Layout |
| `margin` | struct | `0, 0, 0, 0` | Layout |
| `maxHeight` | int | `0` | Layout |
| `maxWidth` | int | `0` | Layout |
| `minHeight` | int | `0` | Layout |
| `minWidth` | int | `0` | Layout |
| `padding` | struct | `0, 0, 0, 0` | Layout |
| `popupMenu` | string | "" | Behavior |
| `tabOrder` | int | `-1` | Behavior |
| `tabStop` | bool | `false` | Behavior |
| `top` | int | `0` | Layout |
| `visible` | bool | `true` | Behavior |
| `width` | int | `100` | Layout |

| Event | Handler parameters |
|---|---|
| `onBlur` | `(Control sender)` |
| `onClick` | `(Control sender)` |
| `onDragDrop` | `(Control sender, DragEventArgs e)` |
| `onDragOver` | `(Control sender, DragEventArgs e)` |
| `onFocus` | `(Control sender)` |
| `onKeyDown` | `(Control sender, KeyEventArgs e)` |
| `onKeyUp` | `(Control sender, KeyEventArgs e)` |
| `onMouseDown` | `(Control sender, MouseEventArgs e)` |
| `onMouseEnter` | `(Control sender)` |
| `onMouseLeave` | `(Control sender)` |
| `onMouseMove` | `(Control sender, MouseEventArgs e)` |
| `onMouseUp` | `(Control sender, MouseEventArgs e)` |
| `onMouseWheel` | `(Control sender, MouseWheelEventArgs e)` |
| `onResize` | `(Control sender)` |
| `onTextInput` | `(Control sender, TextInputEventArgs e)` |

