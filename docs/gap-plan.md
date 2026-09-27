# Plan: closing the gaps for 1.0

> Written 2026-09-26. Turns [gap-analysis.md](gap-analysis.md) into milestones (M10 onwards, continuing
> `RADSharp-roadmap.md`). Same rules as the roadmap: ordered by dependency, not by date; sizes are rough
> (S ≈ days, M ≈ 1–2 weeks, L ≈ several weeks); every milestone has a **done when** that a test can check.
> Web (WASM) is **not** part of 1.0; only the no-regret items from gap-analysis §5.0 are (they sit in M11).
>
> **Updated 2026-09-27** to add M9, and to extend M16 and M18, from
> [designer-gap-analysis.md](designer-gap-analysis.md) — a deeper, designer-specific pass against Delphi and
> Visual Studio (WinForms) that the original gap-analysis only covered in one summary bullet (its old gap 23).

## How every milestone is done

The roadmap's guiding principles apply unchanged, made concrete:

- **Tests through real input** (`form.Simulate`, or a fake `IPlatformServices` for host behaviour), never only through
  the model.
- **Golden images** for anything visual: new controls, states, and 100 %/150 % scales; regenerated only on purpose.
- **NativeAOT publish** of a sample that uses the feature (CI already publishes all samples).
- **Docs move with the code:** `docs/spec.md` for form-file/format changes, `docs/controls.md` regenerates via
  `tools/RadSharp.DocGen`, `getting-started.md` for workflow changes.
- **Form-file changes are versioned** (`"MAJOR.MINOR"`) with a serialization vector each in `spec/vectors/serialization/`.

## Order and dependencies

```
M9 designer ergonomics (parallel, no deps) ───────────────────────────────────────────────────────────────────┐
                                                                                                                │
M10 trust CI ─▶ M11 robust runtime ─┬▶ M12 drawing + fonts ───────────┐                                        │
                                    ├▶ M13 windows, DPI, input ───────┤                                        ▼
                                    └▶ M14 data controls ─▶ M15 reuse + data ─▶ M16 layout + designer polish ─▶ M17 ship ─▶ 1.0
                                                                                            (M18 decision gate: bidi, accessibility, designer shell)
```

M9, M12, M13 and M14 are independent of each other and can be interleaved or split between people. M9 touches only
`RadSharp.Design`/`RadSharp.Designer`, never `RadSharp.Core` or the SDL host, so it can start immediately — even
before M10. M10 otherwise comes first among the rest because every later golden image and layout test assumes the
pixel promise holds off Windows.

---

## M9: Designer ergonomics, no framework dependency (S)

*Closes [designer-gap-analysis.md](designer-gap-analysis.md) §3.2 (multi-select property editing), §3.4 (context
menu), §3.5 (property grid categories/filter), §3.10 (lock controls), §3.12 (project view), and the low-risk part
of §3.13 (rename reach).* These are day-to-day IDE ergonomics the deeper designer-specific pass found that the
original gap analysis's one-line designer bullet (its old gap 23) didn't call out individually. None of it touches
`RadSharp.Core` or `RadSharp.Platform.Sdl`, so it has no dependency on M10–M15 and nothing later depends on it
either — safe to do first, last, or interleaved.

1. **Right-click context menu on `DesignSurface`.** Wrap Cut/Copy/Paste/Delete, Bring to Front/Send to Back, Tab
   Order, and Menu Editor (when applicable) behind the right mouse button; today `OnMouseDown` returns immediately
   for anything but `MouseButton.Left`. *Test:* right-clicking a selected control opens a menu whose items match
   the existing Edit/Component menu commands and act on the current selection; right-clicking empty canvas offers
   Paste and Select All only.
2. **Multi-select shared property editing in `PropertyGrid`.** Today `DesignerForm.UpdateInspector` always sets
   `gridProps.Target` to `doc.Primary` — the single last-selected control — so editing a property while several
   controls are selected only changes one of them. Add a multi-target mode: show properties common to every
   selected control's `ComponentClass`, blank/mixed where values differ, and apply an edit to all of them.
   *Test:* selecting five buttons and setting `FontBold` changes all five; a property only some of the selected
   types have doesn't appear.
3. **Property grid categorized view.** Group rows by the `Category` metadata already on `PropertyMeta` (used
   throughout the framework's own `[Published(Category = ...)]` attributes today, but never read by
   `PropertyGrid.Rebuild`/`Paint`), with a categorized/alphabetical toggle and a text filter box. *Test:* a
   control with 20+ properties renders under several category headers; the toggle re-sorts flat; typing in the
   filter hides non-matching rows.
4. **Lock controls.** A per-control locked flag (or a per-document set), a View/Edit menu toggle, and a check in
   `DesignSurface.OnMouseDown`'s move/resize branches. *Test:* a locked control's rubber-band, drag, and resize
   handles are inert; it stays selectable and still editable in the property grid.
5. **Project tab shows every project file.** Extend the Project tab from `RadProject.FormFiles` to
   `RadProject.SourceFiles` (already enumerated, just unused in the UI), opening non-form `.cs` files in the
   external editor on double-click via the same path as `mnuToggleCode_Click`. *Test:* a plain utility class added
   to the project folder appears in the list and opens.
6. **Widen rename reach.** `RenameInCode`/`CodeIntegration.RenameIdentifier` today rewrite only the active form's
   own code-behind file. Extend the identifier substitution across every file in `RadProject.SourceFiles`. Still
   textual, not semantic — document the limit explicitly (it can't distinguish an unrelated identifier that
   happens to match; a true solution-wide rename needs a real Roslyn `Workspace`/`Solution`, which this project
   doesn't build today). *Test:* renaming a control referenced from a second file (e.g. a partial class split
   across files) updates that reference too.

**Done when:** all six land with their own tests, and none of the changes touch `RadSharp.Core`,
`RadSharp.Platform.Sdl`, or files owned by any other milestone.

**Status:** done (2026-09-27). All six items shipped with tests, 14 new (`DesignSurface.ContextMenuRequested` +
`DesignerForm.ShowSurfaceContextMenu`; `PropertyGrid.SetTargets`/`Targets` for multi-select; `PropertyGrid.Arrangement`
+ `Filter` with the designer's categorized/alphabetical toolbar; `DesignSurface.Locked` plus the Edit-menu and
context-menu toggle; the Project tab now lists `RadProject.SourceFiles`; `RenameInCode` now scans every project
source file). Full suite: 184 tests passing (was 153 at the roadmap's last count, then 170 before this milestone).

**Addendum (2026-09-27, same day):** M16's Anchors editor (below) got pulled forward once a real Delphi screenshot
showed it doesn't need the popup "kite" widget originally planned — `PropertyGrid`'s existing row-list machinery
(from item 3 above) already fit an expand-to-checkboxes model. +2 tests. Full suite: **186** passing.

---

## M10: Trust the foundation (S)

*Closes gap 10.* Nothing else is worth building on until CI has actually run.

- Create the remote, push, and run the matrix (Windows x64, macOS arm64, Linux x64; all three are different CPU/OS
  pairs, so this is also the ARM-vs-x64 test).
- Triage every golden-image diff: identical, sub-threshold, or a real difference. Root-cause any difference (Skia SIMD
  path, font metrics, PNG encoding) before choosing a policy.
- Decide the golden policy and write it in `docs/spec.md`: one set for all OSes (preferred; the promise), or, if a
  platform genuinely differs, per-arch goldens **plus** a documented reason. Do not add a tolerance silently.
- Fix the README/roadmap contradiction ("run in CI" vs "not yet run"); record the outcome in the roadmap.
- Add `dotnet test` result upload and the NativeAOT publish artifacts for all three OSes (already scripted).

**Done when:** CI is green on three OSes with the policy above, and a deliberate 1-pixel change fails it on all three.

## M11: A runtime you can trust (M)

*Closes gaps 1, 2, 12, 14 (part), 21, 25 and web items 5.0 #1–#3.* Small, high-value fixes; do them as one milestone
because they all touch the host boundary.

1. **UI-thread marshalling.** Add `PostToUiThread(Action)` to `IPlatformServices` (Core), `Form.Queue(Action)` and
   `Application.Queue` on top of it; the SDL host installs a `SynchronizationContext` so `await` continues on the UI
   thread. Keep `Application.Post` as an alias. *Test:* with a fake host, an `await Task.Delay(…)` in a handler resumes
   on the UI thread; off-thread `Queue` runs on the next tick.
2. **Exception safety.** Wrap dispatch, ticks, timers and queued actions in one guard. Add
   `Application.ThreadException` (an event; default shows a custom-drawn error dialog with Continue/Quit and the
   message and stack). Unhandled exceptions from `async void` go through the same path. *Test:* a throwing click
   handler shows the dialog and the app keeps running; a subscriber replaces the default.
3. **Async modals and file handles** (the one API-shape change): `ShowModalAsync()`, `Dialogs.MessageDlgAsync`,
   `FileDialog.ExecuteAsync()`; file dialogs return `FileResult { Path?, OpenRead(), OpenWrite() }`. Sync forms stay
   (documented as desktop-only). Convert samples to the async style. *Test:* async modal completes with the right
   `ModalResult` under simulated input.
4. **Tooltips:** render `Control.Hint` in the popup layer after a hover delay (via `Form.AddTicker`), hidden on
   press/leave/key. *Test:* hover → popup after delay; leave → gone; golden image.
5. **Edit/Memo context menu:** built-in Undo/Cut/Copy/Paste/Delete/Select All, overridable by `PopupMenu`.
6. **Idle loop:** replace the fixed 16 ms wake with "next deadline" (caret blink, timers, tooltips); repaint only when
   dirty. *Test:* an idle form causes no wake-ups beyond the next deadline (count iterations with a fake clock).
7. **AOT safety net:** enable AOT/trim analyzers in the template and designer-created projects so F9-works/Build-fails
   surprises appear at compile time; add a short "AOT-safe stack" page to the docs.

**Done when:** a sample downloads a URL with `HttpClient` and updates a `Label` after `await`, throws from a handler
without dying, opens an async modal and file dialog, and idles at ~0 % CPU; NativeAOT publish stays warning-free.

## M12: Drawing and fonts (M)

*Closes gaps 5, 6, 19 (part).*

- **Canvas completeness:** `DrawLine`, `DrawPolyline`, `Path` (builder wrapper), `DrawArc`, `FillPolygon`, stroke
  width/caps/joins/dashes, linear/radial gradients, alpha and blend, rounded clip, and offscreen `Bitmap` (create,
  paint with a `Canvas`, draw with source and destination rects, `EncodePng`). Keep `SKCanvas` private: expose our own
  wrapper types so Core stays host- and backend-neutral. Document the pixel-snapping rules for each primitive
  (half-pixel strokes, whole-pixel fills) so custom drawing stays crisp.
- **`Font` on every control:** a published `Font` (family, size, bold, italic, underline) with Delphi-style
  *ParentFont* inheritance (`null` = inherit, root = theme). Spec: a `"font"` struct in the form format. Add a
  `FontFace` registry (`FontFace.Register(family, files)`) and bundle Inter Italic + Bold Italic; underline drawn by the
  canvas. `Label.FontSize/FontBold/FontColor` become sugar over it (keep reading old files).
- **Images:** `ImageIndex` on `Button`/`CheckBox`-style controls (not only `ToolButton`). SVG: **spike first** (see
  Decisions) and ship rasterised-at-scale SVG only if it is AOT-clean and deterministic.

**Done when:** a `PaintBox` sample draws a line chart with axes, gradient fill and antialiased polyline; golden images
at 100 % and 150 %; a form with mixed fonts round-trips byte-identically.

## M13: Windows, DPI and input (M–L)

*Closes gaps 7, 8, 13, 17 and 18 (partly).*

1. **Window model:** `Form.WindowState` (Normal/Minimized/Maximized/FullScreen), `BorderStyle`
   (Sizable/Single/Dialog/Tool/None), `Position` (Designed/CenterScreen/CenterOwner/Remembered), `Icon`, `TopMost`,
   min/max size to the OS (from `MinWidth/MaxWidth…`), `Screen` (displays, work area, DPI). Save/restore helper for
   window placement. Modal dialogs default to Dialog style and CenterOwner.
2. **Runtime scale change.** Spike first: today `ScaleLengths` rounds in place at load, so re-scaling would drift.
   Options: keep design-time lengths per control and recompute (preferred), or re-stream from the embedded form JSON.
   Then handle `SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED`/`PIXEL_DENSITY_CHANGED`. *Test:* 100 % → 150 % → 100 % gives the
   same layout as a fresh load at 100 %, and the 150 % golden matches a fresh load at 150 %.
3. **Mnemonics:** `&` in captions for Label (with `FocusControl`), Button, CheckBox, RadioButton, GroupBox, TabSheet and
   menus; Alt+letter activates; underline shown on Alt (Windows/Linux); documented as spec Appendix C additions.
4. **Files dropped from the desktop:** `Form.FilesDropped` (paths, drop position). In-app drag and drop
   (`DragMode`/`OnDragOver`/`OnDragDrop`) is **stretch**; do it only if the reference app (M17) needs it.
5. **Application basics:** `Application.Title`, `OnIdle`, `Terminate`, exit code.
6. **Input model:** add `PointerKind` (mouse/touch/pen) to mouse events, no gestures for now; keeps touch taps working
   on tablets and leaves room for the web.

**Done when:** a form starts maximized on a chosen monitor, remembers its placement, survives being dragged between
monitors of different scale, opens dialogs centered on their owner, activates buttons with Alt+letter, and accepts a
dropped file.

## M14: Data controls (L; the largest single piece)

*Closes gaps 3 and 4.* Build one shared scrolling/selection foundation first, then rebuild the controls on it.

1. **Shared foundation:** a `ScrollView`-style base (vertical + horizontal scroll bars from the existing
   `ScrollBarDriver`, wheel/shift-wheel, page/keyboard scrolling, `ScrollIntoView`), a selection model (single,
   multi with Ctrl/Shift, keyboard range), and virtual item painting. Extract from `ListBox`/`ListView`/`TreeView`
   (all three currently reimplement scrolling, and only `ListBox` has a bar).
2. **ListBox v2:** items with `Tag` (keep `string[]` published), multi-select, `ItemHeight` per item via draw event.
3. **ListView v2:** scroll bars and horizontal scroll, column sort (click, indicator, `OnCompare`), multi-select,
   checkboxes, per-item image (`ImageList`), owner data / virtual mode (`OnGetItem`), keyboard type-ahead,
   `Columns` gain alignment and min width.
4. **TreeView v2:** scroll bars, images, checkboxes, multi-select optional, inline rename, lazy expand
   (`OnExpanding`), and nodes definable in the form file (`"nodes"`, like menu items).
5. **ComboBox:** editable style (text + list), autocomplete; **CheckListBox**.
6. **Grid:** `DrawGrid` (owner-drawn) and `StringGrid` on the same base: fixed rows/columns, cell selection and ranges,
   in-place editing with the existing `Edit`, column resize, virtual mode (`OnGetCell`), copy to clipboard as
   TSV, keyboard navigation. Designer: `Columns` editor (like the menu editor).
7. **Stretch, decided by the reference app:** `DateTimePicker` + `Calendar`, `MaskEdit`.

**Done when:** a 100 000-row virtual grid scrolls smoothly with a mouse wheel, a keyboard and its scroll bars, sorts on
a column click, edits a cell in place, and selects ranges; ListView/TreeView pass the same selection and scrolling test
suite as the grid; every new control has a golden image and appears in `controls.md`.

## M15: Reuse and data (L)

*Closes gaps 9 and 11.*

1. **Frames / user controls:** allow a `*.radform.json` whose root is a container (`"class": "Frame"` or `Panel`);
   the generator emits a `partial class` deriving from it, with fields and `InitializeComponent`. A frame can be dropped
   on any form from the palette (project classes appear in the palette). Embedded frames get their own generated
   fields and events. *Test:* a form hosting two instances of one frame; renaming survives save/load.
2. **Visual inheritance** (form derives from another form file): decide after frames; only if the reference app needs it.
3. **Data binding, design first.** Write the design in `docs/spec.md` before code: syntax (`"text": "{Model.Name}"` or a
   `"bindings"` block), one-way vs two-way, `INotifyPropertyChanged` for change notification, converters and format
   strings, list binding for `ListBox/ComboBox/ListView/Grid`, all resolved by the source generator (no reflection,
   AOT-safe). Then implement the smallest useful subset: property-to-property, two-way for `Edit/CheckBox/SpinEdit`,
   and item-source for lists.
4. **`Action` component** (light `TAction`): caption, shortcut, hint, enabled/checked, `OnExecute`; buttons, menu items
   and tool buttons link to it. Cuts duplicated handlers and enable/disable code.
5. **Localization:** pull strings out of form files by key (`"caption": "@Save"`), a resource lookup with a current
   culture, and a designer switch for a locale. Minimal; no runtime language editor.

**Done when:** a master/detail form (list bound to a collection, edit controls bound to the selection, a `Save` action
enabled by a dirty flag) is written with no manual copy-in/copy-out code, in one form plus one frame, and works
under NativeAOT.

## M16: Layout panels and designer polish (M)

*Closes gaps 16 and 23.*

- **Layout panels:** `FlowPanel` (wrap, gap, direction), `GridPanel` (rows/columns with absolute/percent/auto sizes).
  Both are just containers that arrange children; specify in `docs/spec.md` with conformance vectors like D.1–D.4.
- **AutoSize** for `Label`/`Button` (preferred size from the text and padding) and `Control.GetPreferredSize`.
- **Designer:** alignment and sizing tools (align lefts/tops/centres, same width/height, equal spacing), zoom, editing
  pages of a `PageControl` in place, menu-editor fields (`altShortcut`, separators), the new Columns and Nodes
  editors, palette entries for project frames, NuGet component libraries usable in live runs (F9).
- **Generic collection editor** (closes [designer-gap-analysis.md](designer-gap-analysis.md) §3.11): a reusable
  "list of items, each with its own mini property panel" editor, generalising the small-editor-form pattern
  `MenuEditor`/`TabOrderEditor` already establish in `Editors.cs`. Build the Columns and Nodes editors above, and
  the menu editor's separator field, on top of it instead of one bespoke editor per control.
- ~~**Visual Anchors editor**~~ **(done, 2026-09-27, pulled forward from M16)**: closes §3.6. Turned out not to need
  a popup "kite" widget at all — a screenshot of the real Delphi Object Inspector showed `TAnchors` expands in
  place as four checkbox rows (akLeft/akTop/akRight/akBottom), no graphics. `PropertyGrid.ToggleAnchorsExpanded`/
  `IsAnchorFlag`/`SetAnchorFlag` implement exactly that, reusing the row-list mechanism M9's category headers
  already added; works under multi-select for free.
- **Component palette v2** (closes §3.7): category grouping, icons, and a filter box for `lstPalette`, which today
  is one flat, text-only, alphabetical list. Drag-from-palette placement (as an alternative to today's
  click-then-draw) is stretch, not required. Unlike the Anchors/property-grid work, there's no existing
  per-`ComponentClass` category metadata to reuse — default to a cheap heuristic (group by declaring source file)
  rather than adding a new `[Component(Category = ...)]` attribute across every control class up front.
- **Debugging story:** make F5-in-the-IDE the documented way, add a launch profile to created projects, and write the
  "attach to the live run" instructions; a VS Code extension is post-1.0.

**Done when:** a localized form that grows with its text lays out correctly in the flow/grid panels at 100 % and
150 %; the designer's alignment tools are covered by a scripted-rebuild test like `RebuildTileEditorTests`; the
categorized/filterable palette is exercised by a designer UI test (the Anchors editor already is, see M9's status
note — its tests landed in `RadSharp.Core.Tests` since `PropertyGrid` lives in `RadSharp.Core`).

## M17: Ship it (M)

*Closes gap 26 and the roadmap's "left for a person" list.*

- **Reference app (the 1.0 acceptance test):** a small but real application, e.g. a contacts/invoices manager: grid, tree,
  tabs, dialogs, settings persistence, async loading, a bound master/detail form, mnemonics, per-monitor DPI, drag a
  file in. Built from a new project using only the designer plus code. Anything that needs a workaround becomes a bug.
- **Packaging:** exe icon and version resource (Windows), `.app` bundle creation (macOS: `Info.plist`, icns), AppImage or
  tarball plus a `.desktop` file (Linux), all produced by the generated GitHub workflow; signing and notarization steps
  present but gated on repository secrets.
- **Sizes and start-up:** trim the designer (currently 230 MB), measure cold start of the samples, document the numbers.
- **Human decisions/tasks:** product name and licence, nuget.org publishing, certificates, hand-testing IME and the
  designer on macOS/Linux.
- Docs: a "from Delphi" migration page, the AOT-safe stack, and a known-limits page (bidi, accessibility, web).

**Done when:** the reference app builds signed installers-in-artifact form on all three OSes from one push, and a
person who has never seen the project builds and runs it from the getting-started page alone.

## M18: Decision gate: bidi, accessibility, and the designer shell (L each)

Not scheduled for 1.0 by default, because each is large and depends on the target market (bidi, accessibility) or
on whether the reference app's own development actually missed it (designer shell). Decide after M14 (bidi,
accessibility) or M9/M16 (designer shell):

- **Bidirectional text:** HarfBuzz shapes RTL, but the caret, selection, and Unicode bidi ordering are ours. .NET's
  base library has no bidi algorithm. Spike: implement UAX #9 or bind a library, then adapt `TextLayout`/`EditBase`.
- **Accessibility:** AccessKit spike: a tree of nodes derived from the control tree, focus, names, roles, actions;
  per-OS adapters. Start with Windows UI Automation, then macOS, then Linux (AT-SPI).
- **Designer shell rework:** two structural changes flagged, not committed, in
  [designer-gap-analysis.md](designer-gap-analysis.md) — a fixed-position **component tray** for non-visual
  components instead of painting their icons at their own X/Y on the form (§3.8), and **dockable/floatable IDE
  panels** with saved layouts instead of the current fixed-`Splitter` shell (§3.9). Both are real Delphi/Visual
  Studio parity items and both are large relative to everything else in `designer-gap-analysis.md`; neither blocks
  a functioning "light" designer, so build the reference app (M17) first and decide from what its own development
  actually missed.

If any of these are needed for 1.0, each becomes its own milestone and the release moves accordingly. If not,
document them as known limits on the M17 page.

---

## Decisions I need from you

| Decision | Options | My default |
|---|---|---|
| Golden policy if platforms differ | one set (fix the cause) / per-arch sets | fix the cause; per-arch only with a documented reason |
| Async modals | add alongside sync / replace sync | add alongside; sync desktop-only |
| SVG | Skia SVG module / third-party / skip for 1.0 | spike; skip if it isn't AOT-clean and deterministic |
| Data-binding syntax | inline `"{path}"` / separate `"bindings"` block | inline, as the brainstorm proposed; design in spec first |
| Bidi and accessibility in 1.0 | yes / no / decide at M18 | decide at M18 from the target market |
| In-app drag and drop, DatePicker, MaskEdit | 1.0 / after | only if the reference app needs them |
| Component tray rework | keep icons on-canvas (current) / move to a fixed tray strip | decide at M18, from the reference app |
| Dockable IDE panels | fixed `Splitter` shell (current) / full docking + floating + saved layouts | decide at M18; only if the reference app's own development shows a real need |
| Name and licence | (yours) | unblock M17; nothing earlier depends on it |

## Risks specific to this plan

| Risk | Mitigation |
|---|---|
| M10 finds real cross-OS pixel differences | it's first on purpose; the fix (or policy) lands before more goldens exist |
| M14 is bigger than it looks (grids are a product of their own) | shared foundation first; ship ListView/TreeView v2 before the grid; virtual mode from day one |
| Runtime rescale (M13) needs a deeper change to how lengths are stored | spike at the start of the milestone; a wrong choice is cheap to reverse then, expensive later |
| Binding design leaks into reflection | binding resolved by the source generator; AOT publish is part of "done" |
| Scope creep from Delphi's long tail | the reference app is the filter: a feature ships in 1.0 only if the app needs it or it's in a P0/P1 row of the gap analysis |

## Summary of what maps where

| Gap (gap-analysis §2) | Milestone |
|---|---|
| 10 cross-OS unverified | M10 |
| 1 threading, 2 exceptions, 12 tooltips, 14 context menu, 21 idle, 25 AOT gap | M11 |
| 5 canvas, 6 fonts, 19 images | M12 |
| 7 windows, 8 DPI changes, 13 mnemonics, 17 drop, 18 touch | M13 |
| 3 grid, 4 list controls | M14 |
| 9 frames, 11 data binding, 22 localization | M15 |
| 16 layout, 23 designer, 24 debugging | M16 |
| 26 distribution | M17 |
| 14 bidi, 15 accessibility, 20 clipboard formats | M18 / after 1.0 |

Finer-grained designer gaps, from [designer-gap-analysis.md](designer-gap-analysis.md) (2026-09-27), not
individually named in the original gap-analysis's single designer bullet (its gap 23, above):

| Designer gap | Milestone | Status |
|---|---|---|
| §3.1 align/size/space toolbar, menu separator/altShortcut fields, Columns/Nodes editors (already in old gap 23) | M16 | open |
| §3.2 multi-select property editing, §3.4 context menu, §3.5 property grid categories/filter, §3.10 lock controls, §3.12 project view, §3.13 rename reach | M9 | **done** |
| §3.6 visual Anchors editor | M16 | **done** (pulled forward) |
| §3.7 component palette v2, §3.11 generic collection editor | M16 | open |
| §3.3 undo granularity (minor, no milestone — revisit only if it causes real memory/UX pain) | none | open |
| §3.8 component tray, §3.9 dockable IDE panels | M18 | open (decision gate) |
| §4 embedded code editor (named as a deliberate structural choice, not a gap to close) | none | not a gap |
