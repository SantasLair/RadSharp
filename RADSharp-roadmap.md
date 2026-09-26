# RADSharp roadmap

> **RADSharp is the working title.** The released product name must be vetted before release (M9).

> A cross-platform, Delphi-like RAD tool for C#: custom-drawn controls, a visual form designer, and
> NativeAOT executables. Written 2026-09-26. Background and alternatives: `future-framework-brainstorm.md`.
> This is a fun project: milestones are ordered by dependency, not by date. Sizes are rough (S/M/L/XL).

## Decisions so far

| Area | Decision |
|---|---|
| Language | C# (.NET, latest LTS) |
| Controls | Our own custom-drawn VCL/FMX-style control hierarchy |
| Window, input, IME, clipboard, dialogs | SDL3 (via SDL3-CS bindings) |
| Rendering | **Pixel-perfect Skia:** SkiaSharp CPU raster surface → SDL3 texture; dirty-rect redraw |
| Text | Bundled fonts drawn by Skia with fixed settings (grayscale AA, fixed hinting, no LCD subpixel) |
| Layout | Delphi Align + Anchors (brainstorm, Appendix B) |
| Form files | JSON (Appendix A), embedded as a resource and streamed at runtime, like `.dfm` |
| Code-behind | Source generator: form JSON → `partial class` with typed fields and compile-time handler binding |
| Published properties | `[Published]` attribute + source generator emitting metadata (no reflection; AOT-safe) |
| Designer | Built with RadSharp itself; runs on JIT; F9 = Roslyn in-process compile + collectible `AssemblyLoadContext` |
| Code editor | External (VS Code / Rider / Visual Studio); the designer inserts stubs and opens `file:line` |
| Shipping | `dotnet publish` with NativeAOT; one CI job per OS for cross-platform builds |

## Guiding principles

1. **The spec is the product.** Form format, layout rules and conformance vectors are versioned in the repo and tested on all three OSes.
2. **Same pixels everywhere.** Golden-image tests run in CI on Windows, macOS and Linux and must match exactly.
3. **AOT from day one.** Every milestone ends with a NativeAOT publish of a sample; nothing may depend on runtime reflection.
4. **Tests through real input.** `Simulate.Click/Drag/Key` feed the same code paths as SDL events (the GMForms approach).
5. **Dogfood.** The designer is a RadSharp app; if the designer is hard to build, the framework is missing something.

---

## M0: Spec and foundation (S)

Fix the spec issues from the review, then set up the repo so every later milestone lands on green CI.

- ✅ Spec fixes drafted in the brainstorm appendices: rounding mode, zero-size guard, constraint order, explicit rects
  for the 300 × 50 test, saved bounds for aligned controls, design-size saving, JSON determinism, camelCase throughout,
  `tabOrder` sorting, `Mod+` shortcuts, splitter wording and max size.
- ✅ Neutral form format ID `"radform"` (survives a product rename), `"generator"` field, `"MAJOR.MINOR"` version.
- ✅ Appendix D as **JSON test vectors** (`spec/vectors/d1–d4`). Still to do: serialization vectors (D.5).
- ✅ Repo layout (so far `src/RadSharp.Core`, `tests/RadSharp.Core.Tests`; planned: `RadSharp.Controls`, `RadSharp.Platform.Sdl`, `RadSharp.Generators`, `RadSharp.Designer`).
- ✅ GitHub Actions matrix (Windows, macOS, Linux): build + test (not yet run: no remote).

**Done when:** the spec reads unambiguously, the vectors exist, and CI is green on three OSes.

## M1: Layout core, headless (M)

- `Control` tree: parent/children, bounds, visibility, dirty flags, absolute positions.
- Align + Anchors, margins/padding, constraints, rounding, invalidation, `OnResize`, Splitter logic.
- xUnit runner that executes every JSON vector.
- ✅ Layout core, a minimal form reader (hand-written property reading until M4) and the vector runner are in;
  D.1–D.4 pass locally on Windows.

**Done when:** all D.1–D.4 vectors pass on three OSes, with no drawing code involved.

## M2: Pixel-perfect rendering (M)

- SDL3 window; SkiaSharp CPU raster surface copied to an SDL texture; dirty-rect redraw.
- `Canvas` abstraction over Skia with pixel-snapping helpers (half-pixel strokes, whole-pixel fills).
- Bundled fonts, fixed text settings, text measurement API.
- Theme tokens (colours, metrics, fonts) and one default theme.
- Controls: Form, Panel, Label, Button (visual only).
- Scale factor (design at 100 %, round after scaling); strict whole-number mode.
- **Headless render → PNG** and golden-image comparison in CI.
- **NativeAOT smoke test:** publish a sample on each OS, native libraries included.

**Done when:** a hand-written form renders, reflows on resize, and the golden images match on three OSes; the AOT sample runs.

**Status:** done on Windows (x64); CI will show whether the golden images match on macOS and Linux.
- Text: HarfBuzz shapes, glyph outlines come from our own TrueType `glyf` reader (`TrueTypeOutlines`), and Skia fills
  the paths. No OS font engine is involved. Bundled faces: Inter Regular/Bold 4.1 (OFL).
- `Canvas` (crisp non-AA rects and borders, AA round rects and text), `Theme` tokens, `FormRenderer`, dirty-rect tracking,
  load-time scaling; Form, Panel, Label, Button and Splitter paint themselves.
- `RadSharp.Platform.Sdl`: `Application.Initialize/Scale/Run`; CPU frame uploaded to an SDL texture (dirty region only).
- `samples/Hello` publishes with NativeAOT with no warnings (2.9 MB exe + SDL3, Skia and HarfBuzz native libraries);
  its screenshot is byte-identical to the JIT build's. On Windows, `vswhere.exe` must be on PATH (it isn't in Git Bash).
- Controls live in `RadSharp.Core` for now; splitting out `RadSharp.Controls` can wait until the control set grows.
- Deferred: strict whole-number scale mode.

## M3: Input, focus and popups (M)

- Hit testing, mouse capture, click rules, hover, wheel bubbling.
- Focus, Tab/Shift+Tab order, keyboard routing, text input events (kept separate from key presses).
- Shortcut routing with `Mod+` (Ctrl on Windows/Linux, ⌘ on macOS).
- Popup layer (Appendix C rules), context menus.
- `Simulate` API for headless UI tests.
- Controls: CheckBox, RadioButton, GroupBox, ScrollBar, ScrollBox.

**Done when:** the Appendix C behaviours are covered by simulated-input tests.

**Status:** done. `Form` routes all input (hit testing with popups first, capture, click rule, hover, wheel
bubbling, focus, Tab order, shortcuts with `Mod+`, Enter/Escape to default/cancel buttons) and hosts the popup layer;
`form.Simulate` drives the same entry points as the SDL host. New controls: CheckBox, RadioButton, GroupBox, ScrollBar,
ScrollBox (plus mouse-draggable Splitter and a keyboard-operable Button). Context menus arrive with PopupMenu in M6.
C# events use Delphi-style signatures (`NotifyEvent(Control sender)`, `MouseEvent(Control sender, MouseEventArgs e)`).

## M4: Form streaming and code generation (L)

- `[Published]` attribute + generator emitting per-class metadata (name, type, default, values, range, category).
- Deterministic JSON reader/writer driven by that metadata; class registry.
- **Form generator:** `*.radform.json` (MSBuild `AdditionalFiles`) → `partial class` with typed fields and `Bind()`.
  A missing handler is a compile error.
- Form JSON embedded as a resource; `InitializeComponent()` streams it in.
- `dotnet new radsharp-app` template.

**Done when:** a template app with a hand-written form and `MainForm.cs` runs with F5 in VS Code and publishes with
NativeAOT; save → load → save is byte-identical.

**Status:** done.
- `RadSharp.Generators` (analyzer, shipped inside the RadSharp.Core package):
  - `MetadataGenerator`: for every control class, a module initializer registers a `ComponentClass` with a factory and
    AOT-safe accessors for its `[Published]` properties and events. Defaults come from a prototype instance, so
    constructor defaults (Button 80 × 28) need no annotations. Unsupported property types are error RAD0002.
  - `FormGenerator`: `X.radform.json` → `partial class X` with typed `internal` fields, `DesignedClassName` and
    `InitializeComponent()` (streams the embedded file in, binds handlers with `+=`). Diagnostics point into the JSON
    file: RAD1000 invalid file, **RAD1001 missing handler (error, with the stub signature)**, RAD1002 unknown class,
    RAD1003 unknown event, RAD1004 invalid name, RAD1005 duplicate name.
- The hand-written `ReadProperty` overrides are gone; `FormReader` and the new deterministic `FormWriter` are driven
  by the metadata. Serialization vectors live in `spec/vectors/serialization/` (canonical files must round-trip
  byte for byte; `*.input.json` must normalize to their canonical file).
- `build/RadSharp.Core.targets` (build + buildTransitive): every `**/*.radform.json` becomes an AdditionalFile and an
  embedded resource. `RadSharpFormsExclude` / `EnableRadSharpForms=false` opt out.
- `Form.DefaultScale` is what generated code loads at; `Application.Initialize()` sets it to the display scale.
- `templates/radsharp-app`: `dotnet new radsharp-app` (MainForm.radform.json + MainForm.cs + VS Code launch/tasks).
  Verified end to end in isolation: packages from `dotnet pack` → template → build → run → NativeAOT publish (3.1 MB exe).
- Deferred to M9: publishing the template as a NuGet template package.

## M5: Text editing (XL; biggest risk)

- Edit and Memo: caret, selection, mouse/keyboard navigation, clipboard, undo/redo, scrolling.
- IME composition via SDL3 text-editing events; UTF-8/grapheme-aware cursor movement (HarfBuzz shaping).
- Password mode, read-only, max length, `OnChange`.

**Checkpoint:** if this milestone stalls, re-evaluate building the Delphi layer on Avalonia instead of our own controls.

**Done when:** Edit/Memo handle Latin, CJK input through an IME, and emoji, on three OSes.

**Status:** done on Windows; the Avalonia checkpoint was not needed.
- `TextEditor` (pure model): grapheme-safe caret/selection (extended grapheme clusters), word movement and deletion,
  line start/end, a max length counted in graphemes, undo/redo with typing coalesced per word, input sanitizing
  (single-line turns line breaks into spaces).
- `TextLayout`: one line split into font-fallback runs, shaped with HarfBuzz, with caret x at every grapheme boundary
  (ligature clusters are divided evenly) and hit testing. Bundled faces stay pixel-identical; characters they lack
  fall back to `FontFace.AddFallback` faces (bundled, still pixel-identical) and then to system fonts through Skia's
  font manager, shaped by the same HarfBuzz and drawn by Skia, including colour emoji. Latin golden images were
  unchanged by the switch.
- `Edit` (password char, auto-select on Tab, placeholder, read-only, max length) and `Memo` (word wrap, vertical
  scroll bar, WantTabs, WantReturns) share `EditBase`: mouse and keyboard selection, double/triple click,
  Windows and macOS key conventions (Ctrl vs ⌥ for words, ⌘ for line edges), clipboard (Mod+C/X/V, Ctrl+Insert,
  Shift+Insert/Delete), undo/redo, caret blink via `Form.AddTicker`/`ProcessTick`.
- IME: `Form.ProcessTextComposition` shows the composition inline (underlined) without committing it; the focused
  editor publishes `Form.TextInputArea`, which the SDL host passes to `SDL_SetTextInputArea` for the candidate
  window. The host sets `SDL_IME_IMPLEMENTED_UI=composition`, forwards `SDL_EVENT_TEXT_EDITING` and installs the
  system clipboard.
- Not yet: bidirectional text (RTL), IME verified by hand on macOS/Linux, emoji/CJK golden images (fallback fonts
  differ per machine, so they are render-checked only).

## M6: Standard control set v1 (L)

- Lists: ListBox, ComboBox (popup), ListView (report mode), TreeView.
- Containers: PageControl/TabSheet, Splitter (visual), StatusBar, ToolBar.
- Menus: MainMenu, PopupMenu, shortcuts shown in items.
- Values: TrackBar, ProgressBar, SpinEdit, ColorBox.
- Image (PNG/JPEG/SVG via Skia), ImageList.
- Non-visual components: Timer, OpenDialog/SaveDialog/FolderDialog (SDL native dialogs), `MessageDlg`.
- Multiple windows: `Show()`, `ShowModal()`, owner windows.

**Done when:** the GMForms tile-editor form (`tile_editor.gmform.json`, converted) can be rebuilt in RadSharp by hand.

**Status:** done. `samples/TileEditor` is the GMForms tile editor rebuilt: its form was converted by
`tools/convert_gmform.py` and is used as-is by the form generator; the code-behind paints, selects, undoes, zooms,
opens/saves maps through native dialogs and edits a custom `MapInfo` component in the inspector. NativeAOT: 3.9 MB.
- Lists and values: ListBox (owner draw via `onDrawItem`), ComboBox (drop-down list in the popup layer), ColorBox,
  TrackBar, ProgressBar, SpinEdit.
- Containers: PageControl/TabSheet (inactive sheets hidden through `IsShown`, so `visible` isn't written),
  StatusBar, ToolBar/ToolButton/ToolSeparator, PaintBox (`onPaint`), Image/ImageList (`Picture`: data URI,
  embedded resource or file; SVG not supported yet).
- Menus: `MenuItem` trees under `"items"` (generator binds items by path; named items get fields), MainMenu,
  PopupMenu with right-click context menus (`popupMenu` property), submenus, keyboard navigation, shortcuts.
- Non-visual `Component`s: Timer (form clock), OpenDialog/SaveDialog/FolderDialog (SDL native dialogs; the callback
  is marshalled to the UI thread), `Dialogs.MessageDlg/ShowMessage` (custom-drawn modal form).
- Windows: `IPlatformServices`/`PlatformHost`; the SDL host now runs any number of windows, `Show`, `ShowModal`
  (nested loop; other windows blocked), `Close` with `CloseQuery`, `ModalResult` (+ `Button.ModalResult`).
- PropertyGrid (the Object Inspector for M7): properties or events from the metadata, inline editing in the popup
  layer (text, enum/bool lists, string-list memo), `EventDoubleClick` for handler creation; components show only
  their own members. ListView (report style, resizable columns) and TreeView (runtime data).
- Known wart: inside a `Form` subclass, `Color` means the form's `Color?` property, so the type needs a
  qualified name or alias (`using Rgb = RadSharp.Drawing.Color;`). Worth revisiting (e.g. a `Colors` helper).

## M7: Designer MVP (XL)

- Design canvas: a live form instance whose input is intercepted; selection, move/resize handles, multi-select,
  snapping to grid and guides, keyboard nudge.
- Component palette (driven by the class registry) and component tray for non-visual components.
- Object Inspector driven by published metadata: property editors for enum, bool, colour, font, image, anchors, margins, events.
- Structure view (control tree), menu editor, tab-order editor.
- Undo/redo via JSON snapshots; open/save `.radform.json`.

**Done when:** the tile-editor form can be designed from scratch in the designer and saves byte-identically to M6's hand-written version.

**Status:** done. `RebuildTileEditorTests` designs the tile-editor form from an empty form using only designer
operations (palette creation, the inspector's text for every value, the menu editor's fields, renaming, dragging
component icons) and the saved text is byte-identical to M6's file.
- `src/RadSharp.Design` (library): `DesignDocument` (the designed form at 100 %, selection, whole-form JSON snapshot
  undo/redo that also restores the selection, create/delete/copy/paste/rename) and `DesignSurface` (renders the form
  zoomed to the designer's scale with `Canvas.DrawForm`; select, move, 8-handle resize limited by align, grid snap
  with half-away-from-zero rounding, sibling-edge snapping with guide lines, rubber band, palette creation into the
  deepest accepting container, component icons, arrow-key nudge/resize, clipboard as `FormWriter.SaveFragment`).
  `MenuEditor` and `TabOrderEditor` dialogs.
- `src/RadSharp.Designer` (app): dogfooded — its main window is `DesignerForm.radform.json` + generated class.
  Palette, surface, name field, Properties/Events/Structure tabs, menus and toolbar sharing `(object sender)`
  handlers, open/save through native dialogs, unsaved-changes prompt via `CloseQuery`.
- Generator: control classes are now also found in referenced assemblies (component libraries), and generated
  `InitializeComponent()` runs those assemblies' module constructors first, so their controls are registered
  before the form file is read.
- Not yet: the menu editor has no altShortcut/separator fields, no alignment/size tools (align lefts, same width),
  no multi-page PageControl editing beyond clicking tabs, no zoom control.

## M8: Code integration: the Delphi loop (L)

- Project model: a RadSharp project is an ordinary `.csproj` plus forms; open, new form, add existing form.
- Double-click a control or an event in the inspector → Roslyn inserts the handler stub into `Form.cs` → open the
  external editor at `file:line`.
- Rename sync: renaming a control or handler updates the code (Roslyn rename).
- **F9:** compile in-process with Roslyn, load into a collectible `AssemblyLoadContext`, run the form live; unload on close.
- **Build:** `dotnet publish -p:PublishAot=true` for the host OS, with output shown in the designer.
- Error list from compiler diagnostics, click to open in the editor.

**Done when:** new project → drop a Button → double-click → write a handler → F9 → Build gives a native executable,
without leaving the designer except to type code.

**Status:** done. `CodeIntegrationTests.New_project_drop_button_double_click_handler_live_run` walks the loop up to F9
(the live form's button runs the typed handler); `New_project_builds_a_native_executable` (with
`RADSHARP_SLOW_TESTS=1`, after `dotnet pack`) publishes a designer-created project with NativeAOT (~20 s).
- `src/RadSharp.Design.Code` (Roslyn): `RadProject` (a .csproj + `X.radform.json`/`X.cs` pairs; create project,
  add form), `CodeIntegration` (handler stubs inserted with matching indentation and needed usings, identifier
  renames, Delphi-style default events), `LiveSession` (F9: in-memory compile of the project with RADSharp's
  generators run in-process on the designer's unsaved form text, forms embedded as resources, loaded into a
  collectible `AssemblyLoadContext` that shares the designer's RADSharp assemblies; entry points are skipped;
  `ComponentRegistry.Unregister` on unload), `ProjectBuilder` (`dotnet publish` NativeAOT, streamed output),
  `ExternalEditor` (`code -g file:line`, or `RADSHARP_EDITOR`).
- Designer: File › New project / Open project / New form, a Project tab, Run › Run (F9) / Build (Ctrl+F9), an Output
  list (double-click a compiler message to open it), double-click a control or an event to write and open its
  handler, renames carry over to the field and conventionally named handlers in the code. The designer itself now
  runs on the JIT (it hosts Roslyn); the apps it builds are NativeAOT.
- Not yet: user component libraries from PackageReferences in live runs (only the designer's own assemblies are
  referenced), a built-in code view, a debugger (use the IDE's).

## M9: 1.0 release (M)

- **Vet and settle the product name** (NuGet, GitHub, domain, trademark) and rename packages/namespaces if needed.
- Designer shipped as a self-contained app per OS; the SDK requirement checked and explained on first run.
- Generated GitHub Actions workflow for users' cross-OS builds; notes on macOS signing/notarization and Windows SmartScreen.
- Documentation: getting started, control reference (generated from metadata), form format spec.
- Samples: calculator, text editor, tile editor, settings dialog.
- NuGet packages: `RadSharp`, `RadSharp.Controls`, `RadSharp.Generators`, `dotnet new` templates.

**Status:** done except the steps that need a person (below).
- Samples: Calculator (shared digit/operator handlers, keyboard input), TextEditor (Memo, menus, dialogs, unsaved
  prompt), SettingsDialog (a second form shown with `ShowModal`, OK/Cancel via `ModalResult`), plus Hello and the
  TileEditor. All publish with NativeAOT in CI.
- Designer: self-contained publish per OS in CI (230 MB: the .NET runtime plus Roslyn; trimming is future work);
  checks for a .NET 10 SDK at startup and explains what's missing.
- New projects (designer and template) include `.github/workflows/build.yml`: native builds on Windows, macOS and
  Linux as artifacts. `docs/distribution.md` covers signing (Authenticode) and notarization (macOS).
- Docs: `README.md`, `docs/getting-started.md`, `docs/controls.md` (generated by `tools/RadSharp.DocGen` from the
  metadata), `docs/spec.md` (the specification, moved out of the brainstorm), `docs/distribution.md`.
- Packages: `RadSharp.Core` (with the generators and build targets), `RadSharp.Platform.Sdl`, `RadSharp.Templates`
  (`dotnet pack`, CI artifact). The planned separate `RadSharp.Controls`/`RadSharp.Generators` packages were folded
  into `RadSharp.Core`.
- Added along the way: `Label.FontSize/FontBold/FontColor`; the screenshot hook closes forms without `CloseQuery`.

**After 1.0 — project-centred designer (Delphi/Lazarus parity):** the designer always works on a project. A welcome
page (New Application, Open Project, recent projects) replaces loose-form editing; open forms have tabs, each with
its own surface and undo; File/Project menus as in Delphi (New Form, Reopen, Save All, Close Project, Remove Form,
Set as Main Form, F12 to the code); F9 runs the application from its main form with every open form's unsaved
state. Code goes to the IDE of your choice, Unity style (Tools › Options: VS Code, Visual Studio, Rider — detected —
or a custom command; Project › Open in IDE opens the folder or a generated `.sln`). Dev packages get unique
versions and projects reference `0.1.0-dev*`, so NuGet never serves a stale local package.
Tested by driving the designer form itself (`tests/RadSharp.Designer.Tests`). Follow-up: a Visual Studio helper
(DTE automation, as Unity does) to open handlers at their line.

**After 1.0 — live resizing:** forms reflow while a window border is dragged. Windows (modal size/move loop) and macOS
(live-resize tracking) block the app's own loop during a drag; an SDL3 event watch (`SDL_AddEventWatch`), which SDL calls
as events are generated inside those loops, lays the form out and presents a frame for every
`WINDOW_PIXEL_SIZE_CHANGED`/`WINDOW_EXPOSED` (main thread only, re-entry guarded, exceptions contained). Godot does the
same by iterating its main loop from a timer inside the modal loop. Verified by resizing with `SDL_SetWindowSize` and
capturing the back buffer before any event is pumped (`RADSHARP_SCREENSHOT_RESIZE`): the capture already has the new
size and the reflowed layout.

**Left for a person:** vet and choose the product name (then rename packages and namespaces), choose a licence,
publish packages to nuget.org, get signing certificates, and try the designer by hand on macOS and Linux
(IME in particular).

---

## Hardening plan (M10–M18)

A capability review found gaps that block real applications (threading, exception handling, grids and list
controls, canvas, fonts, window/DPI model, frames, cross-OS verification). They are analysed in
[docs/gap-analysis.md](docs/gap-analysis.md) and scheduled as milestones M10–M18 in
[docs/gap-plan.md](docs/gap-plan.md). Web (WebAssembly) is not part of 1.0; its no-regret prerequisites are in M11.

## After 1.0

- **Data binding** (`"text": "{doc.title}"`) and a DataModule-style non-visual container.
- **Component packages:** third-party controls installed from NuGet appear on the palette.
- **Grid control** (StringGrid / DBGrid equivalent).
- **Accessibility** via AccessKit.
- **Localization** and form inheritance (visual form inheritance, as in Delphi).
- **Themes:** theme editor, dark mode, per-OS-flavoured themes.
- **Optional GPU backend** (Skia GL/Metal/Vulkan) for heavy canvases, without the pixel-exact guarantee.
- **Designer inside VS Code** as an extension.
- Web target (WebAssembly + Skia) as an experiment.

## Risks

| Risk | Mitigation |
|---|---|
| Text editing and IME take far longer than expected | Isolated in M5 with an explicit Avalonia checkpoint |
| Skia output differs across OSes despite CPU raster | Found early by M2 golden-image tests; pin the SkiaSharp version and bundled fonts |
| NativeAOT breaks on some dependency | AOT publish is part of every milestone from M2 |
| Designer ↔ code sync gets out of step | Roslyn-based edits only; the form JSON stays the source of truth for layout |
| "Light" suffers from the .NET SDK and, on Windows, the C++ build tools needed for AOT | Clear first-run checks; F9 needs only the SDK, AOT only at Build time |
| Scope creep | Each milestone has a "done when" test; the list after 1.0 is where new ideas go |

## Open decisions

- Released product name (RADSharp is the working title).
- Form file extension: `.radform.json` or `.radform`.
- Target .NET version and minimum OS versions.
- Licence (MIT or similar) if the project is published.
- Default theme style: neutral/modern or classic Delphi.
