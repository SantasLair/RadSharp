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

## M7: Designer MVP (XL)

- Design canvas: a live form instance whose input is intercepted; selection, move/resize handles, multi-select,
  snapping to grid and guides, keyboard nudge.
- Component palette (driven by the class registry) and component tray for non-visual components.
- Object Inspector driven by published metadata: property editors for enum, bool, colour, font, image, anchors, margins, events.
- Structure view (control tree), menu editor, tab-order editor.
- Undo/redo via JSON snapshots; open/save `.radform.json`.

**Done when:** the tile-editor form can be designed from scratch in the designer and saves byte-identically to M6's hand-written version.

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

## M9: 1.0 release (M)

- **Vet and settle the product name** (NuGet, GitHub, domain, trademark) and rename packages/namespaces if needed.
- Designer shipped as a self-contained app per OS; the SDK requirement checked and explained on first run.
- Generated GitHub Actions workflow for users' cross-OS builds; notes on macOS signing/notarization and Windows SmartScreen.
- Documentation: getting started, control reference (generated from metadata), form format spec.
- Samples: calculator, text editor, tile editor, settings dialog.
- NuGet packages: `RadSharp`, `RadSharp.Controls`, `RadSharp.Generators`, `dotnet new` templates.

---

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
