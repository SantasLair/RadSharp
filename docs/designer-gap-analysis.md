# The form designer vs. Delphi and Visual Studio (WinForms): a gap analysis

> Written 2026-09-27 from a read of `src/RadSharp.Design/` (`DesignSurface.cs`, `DesignDocument.cs`, `Editors.cs`),
> `src/RadSharp.Design.Code/` (`CodeIntegration.cs`, `RadProject.cs`, `LiveRunner.cs`, `ExternalEditor.cs`,
> `DesignerSettings.cs`), `src/RadSharp.Designer/` (`DesignerForm.cs`, `DesignerForm.Code.cs`, `Dialogs.cs`,
> `DesignerForm.radform.json`), and `src/RadSharp.Core/Controls/PropertyGrid.cs` — not from running the app.
> Delphi comparisons are from general knowledge of the RAD Studio / VCL Form Designer across versions; Visual
> Studio comparisons are from the WinForms designer specifically (not WPF/XAML). [gap-analysis.md](gap-analysis.md)
> already covers the framework at large (control set, canvas, threading, etc.); this document is scoped to the
> **designer application itself** — the thing a Delphi or WinForms developer would open first and judge the
> project by.

## 1. What the designer already gets right

Worth stating up front, because the rest of this document is gaps: the core "click a control, see it move,
double-click for a handler" loop is solid and the fundamentals are in the right places.

- **A live, real `Form` instance is the canvas**, not a synthetic drawing of one (`DesignSurface.Document.Form`,
  painted through the same `Canvas.DrawForm` the app uses). WYSIWYG is exact by construction, not by a second
  rendering path that can drift from runtime — an advantage over toolkits that draw a designer-only approximation.
- **Grid snap, guide-line snap to sibling edges/centres, Alt to go free-form, arrow-key nudge, Shift+arrow
  resize** (`DesignSurface.cs:81,339-373,454-467`) — the Delphi/VS core interaction set.
- **Multi-select with rubber-band and Shift-click**, move-together with one leader snapping and the rest following
  by the same delta (`MoveSelection`, `DesignSurface.cs:313-333`).
- **Undo/redo** as whole-form JSON snapshots that also restore the selection (`DesignDocument.cs:78-113`) — simple
  and correct, at the cost of granularity (see §3.3).
- **Copy/paste as a portable JSON fragment** through the system clipboard (`CopySelection`/`Paste`,
  `DesignDocument.cs:181-200`), with automatic renaming of clashing names and children brought along — this is
  arguably *nicer* than Delphi's internal clipboard format, since it round-trips through any text editor.
- **Compile-time handler wiring with Roslyn**: double-clicking a control creates the conventionally-named handler
  in the real code-behind file, or jumps to it if it exists (`CodeIntegration.EnsureHandler`,
  `DesignerForm.Code.cs:137-154`); renaming a control renames its field and its `Name_Event` handlers in the code
  too (`RenameInCode`, `DesignerForm.Code.cs:157-179`). A missing handler is a **compile error**, not a silent
  runtime lookup failure — stricter than both Delphi (RTTI-bound, fails at runtime if missing) and WinForms
  (`InitializeComponent` wires a delegate that throws only when raised).
- **F9 live run compiles the in-memory (unsaved) state of every open form** via Roslyn + a collectible
  `AssemblyLoadContext` (`LiveRunner.cs`, `DesignerForm.Code.cs:187-220`) — genuinely close to Delphi's "press F9,
  see it now" loop, and faster than WinForms + Visual Studio's build-and-launch.
- **A menu editor and a tab-order editor** exist as dedicated small tools (`Editors.cs`), matching Delphi's Menu
  Designer and Edit ▸ Tab Order.
- **External-editor integration is IDE-agnostic**: VS Code, Rider or Visual Studio can all be the code editor,
  auto-detected, opened at the right file and line (`ExternalEditor.cs`). This is a genuinely different (not
  worse, just different) model from either reference IDE — see §4.

## 2. Head-to-head by area

| Area | Delphi (VCL Form Designer) | Visual Studio (WinForms Designer) | RADSharp today | Verdict |
|---|---|---|---|---|
| Canvas fidelity | Native VCL controls painted for real | `.NET Framework` controls painted for real | Real `Form`/`Control` tree via `Canvas.DrawForm` | **On par** |
| Grid snap / guides | Grid snap; no live alignment guides (classic VCL) | Grid snap **and** blue "SnapLines" to siblings, margins, baselines | Grid snap + edge/centre guide lines (no baseline) | **On par with VS**, ahead of classic Delphi |
| Multi-select move | Yes | Yes | Yes, one leader + delta | On par |
| **Align/size/space toolbar** | **Alignment Palette** (View ▸ Alignment Palette): align edges/centres, tab order button | **Format ▸ Align / Make Same Size / Horizontal-Vertical Spacing / Center in Form**, plus a floating toolbar | A **Format** menu with the same Align/Make Same Size/Space Evenly/Center in Form commands, plus Align/Make Same Size on the right-click menu | **Fixed** — see §3.1 |
| **Multi-select property editing** | Shared properties of the whole selection editable at once in the Object Inspector | Same, via `PropertyGrid`'s multi-select support | `PropertyGrid.SetTargets` shows properties common to the selection, blank where values differ; editing applies to all | **Fixed** — see §3.2 |
| **Right-click context menu on the surface** | Yes: Align, Order, Edit menu items, View as Text, Create/Delete handler | Yes: Align, Bring/Send, View Code, Properties, Document Outline | `DesignSurface.ContextMenuRequested` + `DesignerForm.ShowSurfaceContextMenu` | **Fixed** — see §3.4 |
| Property grid layout | Categorized (collapsible bands) or alphabetical, toggle | Categorized or alphabetical, toggle, **search box** (VS2019+) | `PropertyGridArrangement` (Declared/Alphabetical/Categorized) + `Filter`, with a toolbar on the designer's Properties/Events tabs | **Fixed** — see §3.5 |
| Anchors editor | Expandable composite row: akLeft/akTop/akRight/akBottom as checkboxes | Visual "kite" widget (4 clickable bars) | `PropertyGrid.ToggleAnchorsExpanded`/`IsAnchorFlag` — same expand-to-checkboxes model as Delphi | **Fixed** — see §3.6 |
| Component palette | Tabbed by category (Standard, Additional, Data Access, …), icons, drag-drop, search (RAD Studio) | Toolbox: categorized, icons, drag-drop, search box, "General" auto-bucket for new libraries | A `TreeView` grouped by category (`PaletteCategories`) with a filter box, a vector picture per entry (`PaletteIcons`), and drag-from-palette on the general `DragMode`/`OnDragOver`/`OnDragDrop` primitive | **Fixed** — see §3.7 |
| Non-visual components | Dedicated **component tray** strip below the form, fixed icon row | Same (component tray under the designer) | Icons painted *at the component's own X/Y* directly on the form surface | **Different, arguably worse** — see §3.8 |
| Structure/outline view | Object TreeView (separate dockable window) | Document Outline / Solution Explorer (separate windows) | One `TreeView` inside a tab of the same panel as Properties/Events | Workable, less discoverable |
| IDE panel layout | Fully dockable/floatable, savable desktop layouts, multi-monitor | Fully dockable/floatable, savable layouts, multi-monitor | Fixed `Splitter`-based left/right/bottom panels, no floating, no saved layouts | **Missing** — see §3.9 |
| Code editor | **Embedded** in the same IDE (Alt/F12 toggles form ⇄ code in place) | **Embedded**, split-screen with designer, IntelliSense, debugger | **External process** (VS Code/Rider/VS) — the designer has no code view of its own | **Different by design** — see §4 |
| Zoom | No (fixed 100%, or DPI-scaled) | No (fixed, DPI-scaled) | No — `Zoom` tracks host UI scale only, not a user zoom control | On par (neither reference has it either) |
| Lock controls | **Edit ▸ Lock Controls** toggle | Right-click ▸ **Lock Controls** | `DesignSurface.Locked`, an Edit-menu toggle and a context-menu entry | **Fixed** — see §3.10 |
| Component/Columns/Nodes sub-editors | Collection editor (generic), ListView Columns editor, ImageList editor, etc. | Collection editor (generic `CollectionEditor`), same set | Menu editor and Tab Order editor only; no generic collection editor, no Columns/Nodes editor (tracked in `gap-analysis.md` #23 alongside the missing `ListView`/`TreeView` v2 controls) | **Missing** — see §3.11 |
| Project view | Project Manager: all units and forms, non-visual files included | Solution Explorer: every file in every project | "Project" tab lists every `RadProject.SourceFiles` entry; a plain `.cs` file opens in the external editor | **Fixed** — see §3.12 |
| Multi-project workspaces | Project Groups | Solutions (multi-project) | One `RadProject` per designer instance; no grouping concept | Not yet needed at 1.0 scope, note only |
| Component-drop feedback | Cursor changes, live outline while dragging from palette | Same | Crosshair cursor + rubber-band while creating; no while-dragging-from-palette preview (there is no drag) | Matches §3.7 |
| Undo granularity | Property-level; some operations coalesce | Property-level; "Undo Typing 'X'" transaction commit on focus-lose | Whole-form JSON snapshot per `Snapshot()` call — correct outcomes, but memory grows with form size and there's no per-property undo *within* one inline edit | **Minor gap** — see §3.3 |
| Handler wiring | RTTI-published method, resolved at load; missing method = runtime error | Delegate wired in `InitializeComponent`; missing method = compile error only if renamed carelessly | **Compile error** if handler is missing (RAD1001) | **Ahead of both** |
| Live run (F9-equivalent) | Full compile + native run, debugger attached | Build + run (F5), debugger attached | Roslyn in-process compile + collectible `AssemblyLoadContext`, **no debugger** (`gap-analysis.md` #24) | Faster loop, no debugging story |
| Rename refactor | Field renames via RTTI; call sites not rewritten automatically in older Delphi | Full Roslyn-backed rename refactor across solution | `RenameInCode` now rewrites the field and handlers across every project source file (textual, not semantic — no Roslyn `Workspace`) | **Improved, still not semantic** — see §3.13 |
| External IDE choice | N/A (one IDE) | N/A (one IDE) | Auto-detects and opens **any** of VS Code / Rider / Visual Studio / a custom command | Unique strength, not a gap |

## 3. Detail on each gap

### 3.1 No alignment/sizing toolbar (the biggest missing everyday tool) — fixed

`DesignerForm.radform.json`'s **Component** menu used to have exactly four working items: Menu Editor, Tab Order,
Bring to Front, Send to Back. There was no align, make-same-size, space-evenly, or center-in-parent command —
the single most-used non-trivial designer command in both reference IDEs.

**Fixed.** A new **Format** top-level menu (`DesignerForm.Alignment.cs`) adds Align (Lefts/Centers/Rights/
Tops/Middles/Bottoms, matching VS's exact naming), Make Same Size (Width/Height/Both), Space Evenly
(Horizontally/Vertically), and Center in Form (Horizontally/Vertically); Align and Make Same Size are also
available as submenus in the right-click context menu (§3.4), disabled below two selected controls. Alignment
targets the primary selection (the last-selected control, as `DesignDocument.Primary` already tracked) — the same
convention Visual Studio uses. Docked controls (`Align != Align.None`) are excluded, since their position is
computed by layout, not free-form. All of it is a thin command layer over the existing
`Control.SetBounds`/`DesignDocument.Snapshot` primitives, as expected.

### 3.2 No shared property editing across a multi-selection — fixed

`DesignerForm.UpdateInspector()` used to always set `gridProps.Target = doc.Primary` — the single last-selected
control — regardless of how many controls were selected. Selecting five buttons and typing a new `FontColor` in
Delphi or WinForms applies it to all five (properties present on every selected type, showing blank where values
differ). Editing while multiple controls were selected used to change only the primary one.

**Fixed.** `PropertyGrid.SetTargets(IReadOnlyList<Control>)` replaces the single `Target`; `UpdateInspector` passes
the whole `doc.Selection`. Rows show only properties common to every selected type, blank where their values
differ (`PropertyGrid.RowValue`'s `Common` helper), and an edit applies to every target that accepts it
(`PropertyGrid.Apply`). The Name field disables itself during a multi-selection, since renaming needs a unique
target.

### 3.3 Undo is whole-form, not per-property

`DesignDocument.Snapshot()` (`DesignDocument.cs:79-83`) serializes the *entire form* to JSON before each recorded
action. Correct and simple, but:

- Every keystroke inside the property grid's inline text editor is **not** its own undo step (good — it commits
  once on blur/Enter — `PropertyGrid.Apply`), but there is also no way to label what an undo step *was*
  ("Undo Move 'btnSave'") the way Delphi and VS show in their status bar / Edit menu.
- Memory cost scales with form size × undo depth; unbounded stack (`Stack<(string,string[])>` never trimmed) — a
  very large form edited for a long session could accumulate a large history. Not urgent, but worth a cap.

### 3.4 No right-click context menu on the design surface — fixed

`DesignSurface.OnMouseDown` used to return immediately unless `e.Button == MouseButton.Left`; right-clicking a
control did nothing at all. Both reference IDEs make the alignment/order/tab-order/properties command set
available with a right-click at the cursor, which matters more than the menu bar once a project has any depth.

**Fixed.** A right-click now selects like a left click would (arms the clicked control, or clears the selection
over empty canvas, keeping a multi-selection if the click landed inside it) and raises
`DesignSurface.ContextMenuRequested`; `DesignerForm.ShowSurfaceContextMenu` builds a `PopupMenu` from the same
Cut/Copy/Paste/Delete/Bring to Front/Send to Back/Tab Order/Menu Editor/Select All/Lock Controls handlers the
Edit and Component menus already use, with items enabled or disabled to match the current selection. It doesn't
yet include the align/size/space commands from §3.1, since those don't exist yet either — add them here too once
they land.

### 3.5 Property grid: no categories, no alphabetical/categorized toggle, no filter — fixed

`PropertyGrid.Rebuild()` used to walk `_class.Properties` in whatever order the source generator emitted them and
add one row per property — no grouping by `[Published(Category = "...")]` (metadata that already existed —
`Category` is used elsewhere, e.g. `DesignSurface.GridSize`'s own `[Published(Category = "Behavior")]`), no
collapsible bands, and no categorized/alphabetical toggle or filter.

**Fixed.** `PropertyGrid.Arrangement` (`PropertyGridArrangement.Declared`/`Alphabetical`/`Categorized`) and
`PropertyGrid.Filter` group rows into category-header bands, sort alphabetically, or filter by a substring; the
old, unconfigured behaviour is exactly `Declared` (the default), so nothing that never touches the new API changed.
The designer's Properties and Events tabs each got a small toolbar (a combo box and a filter `Edit`,
`DesignerForm.radform.json`) wired to it.

### 3.6 No expandable Anchors editor — fixed

~~Anchors needs a graphical "kite" widget~~ — corrected from a real Delphi screenshot: the classic Object
Inspector doesn't draw anything anchor-shaped at all. `TAnchors` is a composite/set property that expands in
place, like a tree row: a disclosure arrow next to "Anchors" reveals four child rows — akLeft, akTop, akRight,
akBottom — each an ordinary checkbox, edited exactly like any other boolean property. No popup, no custom
graphics (that *is* how WinForms' `PropertyGrid` shows `Anchor`, but not Delphi's, and this project styles itself
after Delphi).

**Fixed.** `PropertyGrid` rows can now be an Anchors property's disclosure row (click its name column, or
Left/Right arrow when selected, to expand/collapse) followed by up to four flag rows named `"<property>.left"` etc.
(`PropertyGrid.IsAnchorFlag`, `ToggleAnchorsExpanded`); each flag row toggles with a single click or Space, exactly
like a `Bool` row, and only that one bit changes (`SetAnchorFlag`) — the control's other anchors are untouched.
Works automatically in the designer (no `DesignerForm` changes needed) and under multi-select (blank when the
selected controls disagree on a flag, like every other row).

### 3.7 Component palette: no categories, no icons, no search, no drag-and-drop — fixed

`lstPalette` used to be populated as `_palette.Select(c => c.Name)` into a plain `ListBox` — every creatable
component/control, alphabetical, as text only, no categories, no icons, no filter. Compare:

- **Delphi**: tool palette tabbed by category (Standard, Additional, Win32, Data Access, dbExpress, …), each
  entry has an icon, and there's a search box (Ctrl+Alt+P-style filter-as-you-type) that's often faster than
  scrolling tabs.
- **VS Toolbox**: categorized (or alphabetical) with icons, a filter box, and — importantly — **drag from the
  toolbox onto the form** as an alternative to click-then-draw.

**Fixed.** The palette is now a `TreeView`
(`tvPalette`) grouped under Standard/Additional/Dialogs/System category nodes, with a filter `Edit`
(`edtPaletteFilter`) above it that narrows the tree as you type and clears any armed placement that no longer
matches. The categories come from `PaletteCategories`, a small hand-maintained name→category lookup in the
designer project — deliberately *not* a new `[Component(Category = ...)]` metadata attribute, since
`ComponentClass` has nothing like `PropertyMeta.Category` to build on, and adding one would mean touching every
control class plus the source generator for a designer-polish item. An uncategorized class defaults to
"Additional" rather than being hidden.

Each entry also gets a toolbox icon (`PaletteIcons.For`): a picture of the control, such as a checked box for
`CheckBox` or a stopwatch for `Timer`, in the flat outline-and-accent style of the Visual Studio icon set. Every
icon is a vector drawing on a 16 × 16 grid made with `Canvas` paths, so there are no image assets and it stays
sharp at any scale. Its colours switch with the theme: dark outlines on light themes, light outlines on dark ones.
Non-visual components on the form (`DesignSurface.PaintComponentIcon`) show the same pictures. A class with no
picture yet falls back to a category-tinted badge with its initials (e.g. "SB"). Getting there needed one small addition to `TreeView` itself: `TreeNode.Icon` (`Action<Canvas, Rect>?`), a per-node custom
paint hook reserving a fixed icon column before the label, mirroring `ListBox`'s existing `ItemPainter`. It's
opt-in and additive — a node that never sets `Icon` (the Structure tab's `tvStructure`, any other `TreeView` user)
paints exactly as before.

Drag-from-palette placement is done too, but on the general primitive rather than a one-off: `Control.DragMode`/
`OnDragOver`/`OnDragDrop` and `Form.BeginDrag` (`gap-analysis.md`'s P1 item 17, previously unbuilt) landed in
`RadSharp.Core` first, matching the API shape `gap-plan.md`'s M13 had already reserved. `tvPalette.DragMode =
DragMode.Automatic` starts a drag once a press moves past a small threshold; `DesignSurface` gained one new public
method, `CreateAt(ComponentClass, x, y)` — otherwise it still has no idea what a `TreeView` or a palette is.
`DesignerForm` is the only place that knows both sides: it resolves the dragged `ComponentClass` from
`DragEventArgs.Source` (`e.Source is TreeView tv ? tv.Selected?.Tag as ComponentClass : null`) and wires the
surface's `DragOver`/`DragDrop` events to accept and call `CreateAt`. The click-then-place gesture
(`DesignSurface.OnMouseDown`, `Mode.Create`) is unchanged and still works exactly as before — drag is an
alternative, not a replacement.

### 3.8 No component tray for non-visual components

Delphi and WinForms place non-visual components (timers, image lists, menus, …) in a fixed strip **below** the
design surface, independent of the form's own coordinate space — deliberately, since these components have no
visual position on the form and shouldn't clutter the canvas. RADSharp instead paints each `Component`'s icon
*at the component's own `Left`/`Top`* directly over the form (`DesignSurface.PaintComponentIcon`,
`DesignSurface.cs:530-541`, and `DesignRect`'s special case for `Component`, line 77). Practically this means a
`MainMenu` or `Timer` icon sits wherever it happened to be created and can visually collide with real controls
underneath it, rather than living in a tray a user never has to route around. Worth reconsidering — it's a
structural design choice, not a small fix, so flagging it rather than prescribing the exact rework.

### 3.9 No dockable/floatable IDE panel layout

The designer's own shell (`DesignerForm.radform.json`) is one `Form` with `Panel`+`Splitter` pairs for palette
(left), inspector (right, itself a `PageControl` combining Properties/Events/Structure/Project), and output
(bottom) — a fixed, single-window layout (`DesignerForm.radform.json:344-451` and surrounding). Both reference
IDEs let every tool window float, dock to any edge, tab with other windows, live on a second monitor, and save/
restore that layout ("desktop") per user. This is real functionality some developers rely on (dual-monitor: form
on one screen, Object Inspector + Structure on the other), and it's a large amount of work relative to the rest
of this document — reasonable to leave out of a "light" 1.0 designer, but worth naming explicitly rather than
letting it hide as "not mentioned."

### 3.10 No "lock controls" toggle — fixed

Small but real: Delphi's Edit ▸ Lock Controls and VS's right-click ▸ Lock Controls prevent accidental drags once a
layout is finalized. Nothing in `DesignSurface` used to support this.

**Fixed.** `DesignSurface.Locked` (a per-surface flag, matching how `ShowGrid` already works) disables the resize
handles, the move-drag, and keyboard nudge for existing controls while still allowing selection, property editing,
and placing new controls from the palette — the form's own resize grip is unaffected, matching Delphi (only
*controls* are locked). An Edit-menu toggle (`mnuLockControls_Click`) and a context-menu entry apply it to every
open form.

### 3.11 No generic collection/sub-object editor

Delphi and WinForms both have a generic **Collection Editor** (add/remove/reorder items of a list-typed property,
each item itself getting its own mini property grid) used for anything from a `TStrings` list to a `ListView`'s
`Columns` to an `ImageList`'s images. RADSharp has exactly two purpose-built editors — `MenuEditor` and
`TabOrderEditor` (`Editors.cs`) — and nothing generic. This directly blocks the `ListView`/`TreeView` v2 work
already planned in [gap-plan.md](gap-plan.md) M14 (Columns editor, Nodes editor) — those will each need either
their own bespoke editor (as menu items got) or a shared generic one built once. Given `MenuEditor` and
`TabOrderEditor` are ~280 lines combined and already establish the "small modal editor form built in code" pattern
(`EditorLayout` helper, `Editors.cs:4-24`), a generic version of that pattern is a natural next step rather than
one more bespoke editor per control.

One related, smaller gap already flagged in `gap-analysis.md` #23: the **menu editor has no field for a
separator** — you can only make a separator by hand-editing the form JSON's `"separator": true`, even though
`MenuItem.IsSeparator` and the tree's `Label()` helper already render `"────"` for one (`Editors.cs:194`). A
"Separator" checkbox or an "Add separator" button belongs next to "Add"/"Add sub" (`Editors.cs:65-67`).

### 3.12 Project view shows only forms, not the whole project — fixed

`lstForms` used to list only `_project.FormFiles` — files matching `*.radform.json` — as "ClassName (main form)"
rows, even though `RadProject.SourceFiles` already enumerated every `.cs` file in the project. A plain utility
class or model file had no representation anywhere in the designer.

**Fixed.** The Project tab now lists `RadProject.SourceFiles`: a form's code-behind still shows as its class name
(`ProjectFileLabel`), and double-clicking any other file opens it in the external editor (`OpenInEditor`) instead
of the designer.

### 3.13 Rename refactor is local to the form, not solution-wide — reach widened, still textual

`RenameInCode` used to fix the renamed field and its conventionally-named handlers inside only the **one
code-behind file** for the active form. A reference from elsewhere — a helper method, a partial class split across
files, another form reaching in via a public field — would silently go stale.

**Improved, not fully fixed.** `RenameInCode` now runs the same substitution across every file in
`RadProject.SourceFiles`, so a reference from a second file follows the rename too. It's still textual, not
semantic — there's no Roslyn `Workspace`/`Solution` here (`CodeIntegration.RenameIdentifier` is a token-level
substitution over one parsed file at a time) — so it can't distinguish an unrelated identifier that happens to
match, the way Visual Studio's solution-wide, semantic rename can. Closing that gap fully needs a real
`Workspace`, which is a materially bigger piece of work than widening the file scan was.

## 4. A structural difference worth naming, not fixing: no embedded code editor

Both reference IDEs put code and design in the *same window* — Delphi toggles the same pane between a form view
and a code view (classically bound to the same Alt-key/F12 gesture RADSharp's own View ▸ Toggle Form/Code menu
item deliberately echoes — `mnuToggleCode_Click`, `DesignerForm.Code.cs:100-105`); Visual Studio can even split
them side by side. RADSharp instead **shells out to a real, separate editor process** (VS Code, Rider, Visual
Studio, or a custom command — `ExternalEditor.cs`) for all code editing, and the designer itself has no source
view, no IntelliSense, no debugger, no breakpoints margin, and (per `gap-analysis.md` #24) no debugging story of
its own — F5-in-the-external-IDE is the documented workaround.

This is called out as *different*, not simply worse: reusing a mature, fully-featured external editor for the one
thing it's already excellent at (editing C#) is a legitimate strategy, and it's the one already chosen deliberately
(see `ExternalEditor.cs`'s multi-IDE detection and the design comment on `DesignerForm.Code.cs:99`, "Delphi's
form/code toggle; here it opens the form's code-behind in the IDE"). The gap that follows from this choice — no
integrated debugging — is tracked in `gap-plan.md` M16 ("Debugging story: make F5-in-the-IDE the documented way").
The one sharp edge specific to the *designer* (as opposed to the debugging story broadly) is that a developer
never sees code and form in the same window at all, even read-only — there's no quick "peek at the handler" without
a window switch. Whether that's worth an embedded read-only preview pane is a product call, not a technical gap.

## 5. Suggested order of work

Ordered by ratio of daily value to effort, using the same S/M/L sizing as `gap-plan.md`. **Status as of
2026-09-28:** items 1–7 and 9 are done (`gap-plan.md` M9, plus items 2, 6 and 7 pulled forward from M16 once
scoped); item 2's rename widening shipped as part of M9 too, though it's listed under §3.13 rather than here.
Only 8 and 10 remain untouched.

1. ~~**(S) Right-click context menu on the design surface**~~ — **done**, see §3.4.
2. ~~**(S–M) Align/size/space toolbar and menu**~~ — **done**, see §3.1. Turned out to be closer to (S): a
   **Format** menu plus two context-menu submenus, all thin wrappers over `Control.SetBounds`.
3. ~~**(S) Property grid categories + alphabetical/categorized toggle**~~ — **done**, see §3.5.
4. ~~**(S) Multi-select shared property editing**~~ — **done**, see §3.2.
5. ~~**(S) Lock controls toggle**~~ — **done**, see §3.10.
6. ~~**(M) Component palette v2**~~ — **done**, see §3.7 (a `TreeView` plus `PaletteCategories`, a hand-maintained
   lookup rather than new `[Component(Category = ...)]` metadata; icons are a drawn colour-coded initial per
   entry via a new `TreeNode.Icon` hook, not real per-control artwork; drag-from-palette landed on a new general
   `DragMode`/`OnDragOver`/`OnDragDrop` primitive in `RadSharp.Core`, pulled forward from M13 rather than built as
   a one-off).
7. ~~**(M) Visual Anchors editor**~~ — **done**, see §3.6 (turned out to be an (S) once the real Delphi behaviour —
   an expandable checkbox row, not a graphical widget — was confirmed against a screenshot).
8. **(M) Generic collection editor** — needed regardless for `gap-plan.md` M14's Columns/Nodes editors; also fixes
   the menu editor's missing separator field for free if built as MenuEditor's generalisation. Still blocked on
   M14 not existing yet, so still open.
9. ~~**(M) Project view shows all source files**~~ — **done**, see §3.12.
10. **(L, decide-later) Component tray for non-visual components** and **(L, decide-later) dockable/floatable IDE
    panels** — both real, both large; worth a deliberate yes/no rather than silently deferring, since they
    change the shell's architecture (docking) or the surface's coordinate model (tray) respectively. Tracked as a
    decision gate in `gap-plan.md` M18.

None of this blocks `gap-plan.md`'s milestones, but #8 (generic collection editor) is a soft prerequisite for a
good M14 (`ListView`/`TreeView`/grid `Columns`/`Nodes` editors), so sequencing it before or alongside M14 avoids
building one bespoke editor per control.
