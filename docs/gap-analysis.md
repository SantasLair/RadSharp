# Capability review, real gaps, and a web (WASM) target

> Written 2026-09-26 from a read of the source (`src/`, `docs/`, roadmap, tests, CI), not from running the app.
> Claims about behaviour come from the code; where I inferred something it says so. Delphi comparisons are from
> general knowledge of VCL/FMX/RAD Studio, not from a licensed copy.
>
> Scope, as requested: what stops RadSharp being used to build **real cross-platform applications**, with no bells
> and whistles. Then: what a browser (WebAssembly) target needs, and which decisions have to be made first.
>
> **Web is an eventual goal, not a 1.0 requirement.** Sections 2–4 are the 1.0 material. Section 5 is post-1.0
> planning; its only 1.0 impact is a short list of *no-regret* decisions (5.0) that keep the door open cheaply.
>
> **Updated 2026-09-28:** this document had drifted from the code. Gaps 1, 2, 5, 6 (P0) and 12, 21, part of 14
> (P1) were already fixed by M11/M12 in commits that predate this update — landed between when this doc was
> written (2026-09-26) and now, never reflected back into it. Gap 17 (drag and drop, both halves) is closed this
> session — see [gap-plan.md](gap-plan.md)'s M13 status note. Checked directly against the current code
> (grep for the relevant types/members), not just against `gap-plan.md`'s own milestone notes, since those can
> drift too — see each corrected row below for what to look for.

> **Updated 2026-11-10 (branch `bcf-port-gaps`):** porting a real app (a custom-painted grid, flat dark buttons,
> hints on regions of a drawn control, a maximised main window) closed these, each with tests: window state, minimum
> size, start position and non-resizable windows (gap 7, partly); `Form.ApplyScale` for code-built forms; text
> ellipsis, word wrap and `Label.WordWrap`; `Control.GetHintAt` region tooltips plus a fix for clicks being swallowed
> while a tooltip was open (gap 12); `Button.Flat/Color/FontColor/HoverColor`; a public `ScrollBarDriver` and a
> `ScrollView` base for custom-drawn scrolling content; `Control.Tag`, `Application.OpenUrl`, `Form.KeyPreview`.
> See the rows below and section 4 item 4 for what is still open.

## 1. What is already solid

The foundation is better than its age suggests, and most of the gaps below are *missing breadth*, not a weak core.

- **Architecture.** `RadSharp.Core` (controls, layout, rendering, form format) is separate from the host
  (`RadSharp.Platform.Sdl`). The seams are already interfaces: `IPlatformServices`, `IClipboard`, host-driven
  `Form.ProcessTick`, and the `Form.Process*` input entry points. This is what makes a second host (web) realistic.
- **Layout and forms.** Delphi Align + Anchors, margins, constraints, deterministic JSON form files, compile-time
  handler binding (missing handler = compile error RAD1001), AOT-safe metadata through source generators.
- **Rendering.** Own glyph outlines + HarfBuzz + Skia CPU raster, golden-image tests, fractional-DPI rounding rules.
- **Text editing.** Grapheme-aware caret, undo, IME composition inline, clipboard, Edit and Memo.
- **The loop.** Designer → handler stub → F9 live run → NativeAOT build → generated cross-OS CI workflow.
- **Tests.** 153 at the time this doc was written; **210** now (2026-09-28), the difference mostly M11/M12 and this
  session's designer/drag-and-drop work. Layout vectors, golden images, simulated input, designer-drives-designer.

## 2. Gaps that block real applications

Ordered by how badly a typical business or utility app is hurt. **P0** = an ordinary app cannot be finished
without a workaround; **P1** = painful but workable; **P2** = polish.

### P0

| # | Gap | Evidence | Delphi/RAD Studio has |
|---|---|---|---|
| 1 | ~~**No UI-thread marshalling in Core, no `SynchronizationContext`.**~~ **Fixed** (M11, predates this doc's last real read). `SdlHost` installs a real `SynchronizationContext` (`SynchronizationContext.SetSynchronizationContext`) so `await` in a handler resumes on the UI thread; `Application.Post`/`PostToUiThread` still work as the explicit form. | `SdlHost.cs`: `_uiContext`, `UiSynchronizationContext` | `TThread.Synchronize/Queue`, `Application.ProcessMessages` |
| 2 | ~~**An exception in any event handler is unhandled.**~~ **Fixed** (M11). `Application.ThreadException` (`ThreadExceptionEventArgs`) reports an unhandled exception with a default path if nothing subscribes. | `Application.cs`: `ThreadException`, `ReportException` | `Application.OnException`, default error dialog |
| 3 | **No grid.** No StringGrid/DrawGrid equivalent, and `ListView` is a minimal report list (below). Tabular data is the core of most business apps. | roadmap "After 1.0" lists it; still true — only `PropertyGrid` exists among `*Grid` classes | `TStringGrid`, `TDrawGrid`, data-aware grids |
| 4 | **List controls are too thin to hold data.** `ListView`: no scroll bars (wheel only), no horizontal scroll (columns wider than the view are unreachable), no icons, checkboxes, multi-select, sorting, or virtual mode. `TreeView`: no scroll bars, images, checkboxes, multi-select; nodes only from code (not in the form file — though a leaf can now carry a drawn icon via `TreeNode.Icon`, added this session for the designer's palette, not for data). `ListBox`: `string[]` items only (no `Tag`/objects), no multi-select. `ComboBox` publishes only Items/ItemIndex/DropDownCount (no editable style or autocomplete as far as I could see). | `ViewControls.cs`, `ListControls.cs` | `TListView` (icons, checks, `OwnerData`), `TCheckListBox`, `TTreeView` state images |
| 5 | ~~**The `Canvas` can't draw lines, paths, gradients or arcs.**~~ **Fixed** (M12). `DrawLine`, `DrawPolyline`, `DrawArc`, `FillPolygon`, `Fill(GraphicsPath, …)`, and gradient overloads of `FillRect`/`FillRoundRect`/`FillEllipse`/`Fill` all exist now. | `Drawing/Canvas.cs` | `TCanvas` (LineTo, Polygon, Arc, Draw…) |
| 6 | ~~**No per-control font.**~~ **Fixed** (M12). Every control has `EffectiveFont` (own `Font`, else the parent's, else the theme's — Delphi's *ParentFont* model), propagated into every control's `Paint()`. | `Control.cs`: `EffectiveFont`, `_font` | `Font` on every control |
| 7 | **Windows are under-specified.** ~~`WindowState`, start position, min size, non-resizable~~ **Closed 2026-11-10:** `Form.WindowState` (Normal/Maximized/Minimized, written back when the user changes it, `WindowStateChanged`), `Form.StartPosition` (Default/CenterScreen/CenterOwner/Manual), `Form.Resizable`, and the window minimum size from `Control.MinWidth`/`MinHeight`, all applied through SDL3 and re-applied while the window is open. **Still open:** fullscreen, other border styles (tool, none), window icon, maximum size, always-on-top, remembered position, a screen/monitor API. Not hand-verified on macOS/Linux. | `Form.cs` (`WindowState`, `StartPosition`, `Resizable`), `SdlHost.cs` (`FormWindow`) | `Position`, `WindowState`, `BorderStyle`, `BorderIcons`, `Icon`, `Screen` |
| 8 | **Scale is fixed at form load.** `Form.DefaultScale` is read once at `Initialize`; dragging a window to a monitor with a different DPI (or, on the web, zooming the page) doesn't re-layout at the new scale. (Inferred: pixel density is tracked per window for pixels ↔ points, but lengths are scaled once at load.) Code-built forms can now get the same load-time scaling with `Form.ApplyScale()` (2026-11-10); re-scaling on a monitor change is still open. | `Application.Initialize`, `Form.Scale` | per-monitor-v2 DPI, `ScaleBy` |
| 9 | **Reusable UI.** No frames / user controls (a form file whose root is a `Panel` embeddable in other forms), no visual form inheritance. Every real app needs shared composite pieces. | roadmap "After 1.0" | `TFrame`, visual inheritance |
| 10 | **Cross-platform claim partially verified.** ~~Linux x64 half~~ **Verified (2026-09-28), via local WSL2 Ubuntu 24.04, .NET 10.0.101 SDK.** Full solution builds clean (including `RadSharp.Platform.Sdl`'s `libSDL3.so` native link), and all 210 tests across all 5 test projects pass — including every golden-image test in `RadSharp.Core.Tests`, run against the same Windows-generated goldens with no pixel diffs. macOS remains unverified (no CI matrix run, no local Apple Silicon box) and is deprioritized for now. | roadmap M0, M2 status; `README.md`; local WSL2 run 2026-09-28 | n/a |

### P1

| # | Gap | Notes |
|---|---|---|
| 11 | **Data binding.** Listed for after 1.0. Without it every app copies model ↔ controls by hand. Smallest useful version: a generator-emitted, AOT-safe binding of a property path to a control property with change notification. | Delphi: DataSource / LiveBindings |
| 12 | ~~**Tooltips.**~~ **Fixed** (M11), **extended 2026-11-10.** `Form` shows a hovered control's `Hint` after a delay. Now also `Control.GetHintAt(x, y, out Rect region)` for hints on regions of a custom-drawn control (re-shown when the region changes, sized to multi-line text, placed by the pointer), and a bug fix: the tooltip lived in the popup layer, so a click while it was open was swallowed as a "press outside popup". | trivial to fix; users expect it |
| 13 | **Keyboard mnemonics.** No `&File` / Alt+letter in captions or menus. Menus have shortcuts but not accelerator letters. Still true. | accessibility and speed for keyboard users |
| 14 | **Text-field basics.** ~~No built-in context menu~~ — **fixed** (M11): Edit/Memo now build a default Cut/Copy/Paste/Select All `ContextMenu`, overridable by an explicit one. No masked edit; no bidirectional text (Arabic/Hebrew unusable); IME unverified by hand on macOS/Linux — all still open. | bidi is a hard blocker for those markets |
| 15 | **Accessibility.** Custom-drawn controls are invisible to screen readers. AccessKit is planned after 1.0. Some customers cannot buy software without it. | |
| 16 | **Layout depth.** Only Align + Anchors: no flow/grid/stack panels, no auto-size labels or buttons, no preferred-size. Localized or DPI-scaled text overflows fixed rects. | Delphi has `TFlowPanel`, `TGridPanel`, `AutoSize`; FMX has flow/grid layouts |
| 17 | **Drag and drop.** Both halves fixed, on one unified mechanism: `Control.DragMode`/`OnDragOver`/`OnDragDrop`, driven by `Form.BeginDrag` for an in-app drag (Delphi-style: inspect `DragEventArgs.Source` for what's being dragged, no separate payload type), and by `Form.ProcessFilesDropped` — wired from SDL's `SDL_EVENT_DROP_BEGIN`/`_FILE`/`_COMPLETE` — for a file dropped from the desktop, which sets `DragEventArgs.Files`/`IsFileDrop` instead. Either way the same `OnDragOver`/`OnDragDrop` on a specific control fires, so e.g. an image viewer can accept a file dropped on *it* directly, not just somewhere on the form. First real in-app use: dragging a component from the designer's palette onto a form. Unit-tested at the `Form`/`Control` API level; not hand-verified against a real OS drag on any platform. | |
| 18 | **Input beyond mouse and keyboard.** No touch, pen or gesture events in the input model (SDL emulates mouse from touch by default, which covers taps). Matters for tablets and for the web on phones. | |
| 19 | **Images.** ~~No image on plain buttons~~ **fixed**: `Button`/`CheckBox`/`RadioButton`/`ToolButton` all publish `ImagesName`/`ImageIndex` now, resolved through a shared `ImageList.Resolve` helper. **SVG deferred post-1.0, raster `ImageList` only** — spiked `Svg.Skia` (2026-09-28): it publishes and runs under NativeAOT for a basic case, but its dependencies (`Svg.Custom`, `ExCSS`) emit real `IL2104`/`IL3053` trim/AOT warnings, not spurious ones, so it fails the "AOT-clean" bar the milestone set. No animated images. Icons at several DPIs still need multi-resolution `ImageList` entries. | |
| 20 | **Clipboard is text only.** No images or file lists. | |
| 21 | ~~**Idle cost.**~~ **Fixed** (M11). `Form.NextWakeMs` computes the next real deadline (caret blink, timers, tooltips); `SDL_WaitEventTimeout` uses it instead of a fixed 16 ms. | |
| 22 | **Localization.** No mechanism; strings live in the form file. Still true. | Delphi: resource strings, ITE |
| 23 | **Designer.** Mostly fixed this session — see [designer-gap-analysis.md](designer-gap-analysis.md) for the full, since-superseded picture: alignment/size tools, a real component palette (categories, icons, filter, drag-and-drop), the property grid (categories, filter, multi-select, an Anchors editor), a right-click context menu, Lock Controls, a full project view, and wider rename reach are all done. Still open: zoom, PageControl page editing only by clicking tabs, menu-editor altShortcut/separator fields, a generic collection editor (needed for future Columns/Nodes editors), component libraries from NuGet not yet used in live runs, a component tray, and dockable panels. | |
| 24 | **Debugging.** External IDE only; F9 runs inside the designer with no debugger story. F5 in the IDE works since the project is ordinary .NET, but design edits need the designer. A VS Code/Rider extension is the natural fix. | Delphi's integrated debugger is part of "the Delphi loop" |
| 25 | **AOT gap between F9 and Build.** F9 runs on the JIT; Build is NativeAOT. Reflection-based code (many JSON, DI, ORM libraries) works in F9 and fails or trims away in the published exe. ~~Turn on the AOT/trim analyzers in the template and list an AOT-safe stack in the docs.~~ **Fixed** (M11, 2026-09-28): the `dotnet new` template and `samples/Robustness` already had `EnableAotAnalyzer`/`EnableTrimAnalyzer`; added to the designer's own new-project scaffold (`RadProject.Create`) too, plus `docs/aot-safe-stack.md`. | |
| 26 | **Distribution.** No `.app` bundling, no exe icon or version resource, no installer/MSIX/AppImage tooling; three native libraries ship next to the exe; the designer is 230 MB and building needs the SDK plus C++ tools, which strains "light". Signing is documented but not automated. | |

### P2 (API polish)

- `Timer` collides with `System.Threading.Timer` under implicit usings; `Form.Color` shadows `RadSharp.Drawing.Color`
  (the roadmap already flags this one).
- No printing, no sound/media, no settings/INI helper (`Environment.GetFolderPath` covers most of it).
- `MessageDlg`, `OpenDialog.Execute()` and `ShowModal()` are synchronous (nested loops). Fine on desktop; a problem
  for the web (section 3).

## 3. Comparison with Delphi / RAD Studio, by area

| Area | Delphi VCL/FMX | RadSharp today | Verdict |
|---|---|---|---|
| Form format + code-behind | .dfm/.fmx + .pas | `.radform.json` + partial class, compile-time binding | **On par** (arguably stricter) |
| Layout | Align, Anchors, plus Flow/Grid/Stack panels, AutoSize | Align, Anchors, constraints | Missing flow/grid/auto-size |
| Standard controls | ~40 common, plus grids, calendar, date picker, rich edit, masked edit, check-list, up-down | ~35 (no grid, date/calendar, rich/masked edit, check-list) | Missing data-entry set |
| Data | DataSets, FireDAC, LiveBindings, data-aware controls | none | Missing (bindings are a start) |
| Custom drawing | Full canvas | rects/ellipses/lines/paths/gradients/text/images | **On par** (fixed, M12) |
| Actions | `TActionList` (one command, many UI surfaces) | none (menu items and buttons share handlers) | Missing; a light `Action` would help |
| Reuse | Frames, inheritance, packages, DataModules | none yet | Missing |
| Windows/App | full window model, `Screen`, `Application` events, tray | Show/ShowModal/Close only | Missing basics |
| Threading | `TThread`, `Synchronize`, `TTask` | `SynchronizationContext` installed, `Application.Post`/`PostToUiThread`, `Application.ThreadException` | **Fixed** (M11); no `TThread`/`TTask` equivalent, but `await` and `Task.Run` + marshal-back both work |
| Localization/RTL | yes | none / none | Missing |
| Accessibility | UIA/MSAA (VCL); FMX partial | none | Missing |
| IDE | inspector, structure, tab order, align tools, code editor, debugger, refactoring | inspector, structure, tab order, align/size/space tools, a categorized palette, external editor, Roslyn rename (project-wide, textual) | Fine for "light"; debugger via the IDE — see [designer-gap-analysis.md](designer-gap-analysis.md) |
| Output | one exe (VCL), mobile, macOS, Linux | NativeAOT exe + 3 libs, Win/mac/Linux | **Ahead** on code protection; behind on mobile |
| Web | none native (third parties transpile to JS) | not yet | Opportunity |
| Pixel identity across OS | not a goal (VCL native; FMX styles) | goal, tested | **Ahead**, if CI confirms it |

## 4. Suggested order of work (native)

**Status as of 2026-09-28:** items 1 and 3 are done (M11/M12, predating this update); drag-and-drop and file drop
(part of item 8) are done this session; mnemonics (also item 8), CI (item 2), window model (item 4), lists/grid
(item 5), frames (item 6), data binding (item 7), layout panels (item 8), SVG (item 8), and bidi/accessibility
(item 9) are still open.

1. ~~**Robustness (S)**~~ — **done**: UI-thread marshalling (`SynchronizationContext`), `Application.ThreadException`
   with a default dialog, tooltips, Edit/Memo context menu.
2. ~~**Get CI green on macOS and Linux (S–M)**~~ — **done (2026-11-11)**: Windows and Linux (x64) match the goldens
   exactly. macOS on Apple Silicon differed only on anti-aliased edges, by at most 2/255 per channel, so the golden
   comparison now allows that much (see `docs/gap-plan.md`, M10) instead of keeping a second set of images.
3. ~~**Canvas completeness (S)**~~ — **done**: lines, polylines, paths, arcs, gradients, offscreen bitmaps
   (`Bitmap`); per-control `Font` (`EffectiveFont`).
4. **Window model (M):** `WindowState`, border style, position, icon, min/max size, and per-monitor scale change.
   **Partly done (2026-11-10):** window state, minimum size, start position and the resizable flag. Still open:
   border styles beyond resizable/fixed, icon, maximum size, fullscreen, and per-monitor scale change (gap 8).
5. **Lists and grid (L):** scroll bars and horizontal scroll in ListView/TreeView, icons, checkboxes, multi-select,
   sort; then a StringGrid/DrawGrid with virtual mode. Still open.
6. **Frames / user controls (M):** allow a non-Form root in `*.radform.json`. Still open.
7. **Data binding (M–L).** Still open.
8. **Layout panels and auto-size (M), mnemonics (S),** ~~**file drop (S)**~~ **(done)**, ~~**in-app drag-and-drop**~~
   **(done, not originally listed here — see gap 17)**, **SVG (M) still open.**
9. **Bidi, accessibility (L each)**, decided by target market. Still open.

## 5. Running on the web with WebAssembly (post-1.0)

### 5.0 What to do before 1.0 so the web stays cheap

Nothing here needs a browser. These are choices that are cheap now and expensive after more code depends on them.
Do them only where they also make sense on desktop.

1. **Async modal and file APIs.** Add `ShowModalAsync()`, an async `MessageDlg`, and `OpenDialog.ExecuteAsync()`
   next to the synchronous forms, and use the async style in new samples and docs. Sync stays supported on
   desktop. This is the one item that changes public API shape.
2. **File dialogs return a handle, not just a path.** `OpenRead()` / `OpenWrite()` alongside `Path`. Portable
   (and handy for sandboxed macOS and Linux Flatpak apps too).
3. **Keep Core free of desktop assumptions.** No new direct `File`, `Environment` or thread use in `RadSharp.Core`
   outside the platform interfaces; put UI-thread marshalling (P0 #1) behind an interface in Core, not in the SDL
   package.
4. **Keep everything NativeAOT-clean.** Already a project rule; it also keeps the NativeAOT-LLVM option open.
5. **Runtime scale changes** (gap 8) serve monitors moving on desktop and page zoom on the web.
6. **Fonts and text.** Keep the "bundled first, system last" rule; don't add features that only work with system fonts.

Everything else in this section can wait until after 1.0.

### 5.1 What is already in RadSharp's favour

- `Core` needs only: Skia, HarfBuzz, System.Text.Json and file/environment APIs. None of it is OS-specific.
- The host contract is small: `IPlatformServices` (show/close form, modal loop, file dialog), `IClipboard`, and
  `Form.ProcessMouse*/Key*/Text*/Tick`. `Form.TextInputArea` is already published for the IME candidate window;
  a browser host can use the same rectangle to position a hidden `<textarea>`.
- Fonts are bundled (Inter, 0.4 MB per weight) and text is shaped and drawn from our own outlines, so the web build
  needs no browser font engine. **If the same Skia raster runs in WASM, the same golden images should apply.**
- `Form.ProcessTick` is host-driven, so `requestAnimationFrame` can drive it, and rendering only when dirty is
  easier in a browser than the current 60 Hz SDL wake.
- The file dialog API is already callback-based (`Action<string[]?> done`).

### 5.2 What does not carry over

| Area | Today | Problem on the web |
|---|---|---|
| Host | `RadSharp.Platform.Sdl` | SDL3 can target Emscripten, but its model is a browser main-loop callback, not a blocking `while` loop; text/IME support is thin |
| Modal windows | `ShowModal()` runs a nested loop and returns a `ModalResult` | A single-threaded WASM app **cannot block**. Every synchronous modal breaks: `ShowModal`, `MessageDlg`, `OpenDialog.Execute()` |
| File paths | dialogs return `string[]` paths; samples use `File.*` | Browsers give no paths and no real file system, only picked files, `Blob`s and downloads |
| System fonts | `FontFace.SystemFallback` uses `SKFontManager.MatchCharacter` | No system fonts in WASM. CJK, emoji and other scripts must be shipped; `TrueTypeOutlines` reads TrueType `glyf` only (no CFF/OpenType-CFF), which limits which CJK fonts can be bundled |
| Clipboard | synchronous `IClipboard.GetText()` | Browser clipboard is async and gated by user gestures; paste must come from a `paste` event |
| Threads | threadpool used freely (dialog callbacks marshalled) | Default browser build is single-threaded; `Task.Run` runs on the UI thread; threaded WASM needs cross-origin-isolation headers and is experimental |
| Windows | any number of native windows | One page/canvas: secondary forms become in-page layers |
| Touch | mouse and keyboard only | Phones and tablets send touch/pointer events; soft keyboards |
| Designer + F9 + Build | Roslyn, `AssemblyLoadContext`, `dotnet publish` | Not portable. The designer stays a desktop tool; only *apps* target the web |
| Code protection | NativeAOT machine code | Web builds ship .NET assemblies (or AOT-compiled WASM) that a user can download and inspect |

### 5.3 Options

| Option | How | Verdict |
|---|---|---|
| **A. .NET WebAssembly + SkiaSharp WASM + custom browser host** | Reference `SkiaSharp.NativeAssets.WebAssembly` and `HarfBuzzSharp.NativeAssets.WebAssembly` (both exist on NuGet), and wire them as `NativeFileReference` as the [Blazor native-dependencies guide](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-native-dependencies?view=aspnetcore-10.0) describes. Write `RadSharp.Platform.Browser` using JS interop: canvas, pointer/key events, hidden textarea, `requestAnimationFrame`. Present the CPU `SKBitmap` with `putImageData` (dirty rectangles supported). | **Recommended.** Supported path; reuses Core untouched; keeps the pixel-exact CPU pipeline. Cost: a few MB of runtime + Skia, and the interpreter is slower than AOT |
| B. SDL3 through Emscripten | Link SDL3 into the .NET WASM runtime and keep `Platform.Sdl`. | Fights two toolchains, needs the main loop inverted anyway, weak IME. Not recommended |
| C. Draw with the browser's Canvas/CanvasKit from a command list | `Canvas` records commands; JS replays them with Canvas2D or [CanvasKit](https://skia.org/docs/user/modules/canvaskit/). | Smaller download for C#-side, but loses pixel identity (Canvas2D) or adds a second Skia (CanvasKit), and text shaping would need re-implementing. Third-party [SkiaSharpWeb](https://github.com/wieslawsoltes/SkiaSharpWeb) takes this route. Fallback only |
| D. NativeAOT-LLVM to WASM | Keep the "AOT everywhere" promise on the web. | Not production-ready: the .NET runtime is only now merging a stubbed NativeAOT CoreLib for single-threaded browser WASM ([dotnet/runtime#134210](https://github.com/dotnet/runtime/pull/134210)). Keep the code AOT-clean and revisit |
| E. Adopt Avalonia's or Uno's browser target for the web build | They already draw with Skia to a canvas in the browser ([Avalonia](https://avaloniaui.net/avalonia/wasm), [Uno](https://platform.uno/blog/skiasharp-support-for-webassembly-via-uno-platform/)). | Not a path for this project's controls; useful only as a benchmark for size and IME behaviour |

### 5.4 Decisions to make (with a recommendation)

1. **Is web a runtime target only?** Recommend yes. The designer, F9 and Build stay desktop.
2. **Runtime.** A: Mono .NET WASM, interpreter first, optional AOT for speed (AOT WASM is roughly 1.5–2× larger, per
   [Blazor guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/app-download-size?view=aspnetcore-10.0)).
   Keep everything NativeAOT-clean so option D stays open.
3. **Host.** A custom `RadSharp.Platform.Browser`, not SDL/Emscripten.
4. **Surface.** CPU Skia → `ImageData` → 2D canvas. Pixel-exact, dirty-rect friendly. Add WebGL only later, as an
   explicit non-exact option (the roadmap already lists a GPU backend).
5. **Modal API.** Add `ShowModalAsync()` / `MessageDlg` async / `OpenDialog.ExecuteAsync()` returning `Task`, and
   make async the documented cross-platform style. Keep the synchronous forms on desktop (they will throw
   `PlatformNotSupportedException` on the web). This is the biggest API-level change, so decide it before more
   samples use `ShowModal`.
6. **Files.** Change the file dialogs to return a handle with `OpenRead()` / `OpenWrite()` (and keep `Path` where one
   exists). On the web these map to `<input type=file>` or the File System Access API, and save becomes a download.
7. **Fonts.** Ship Inter only by default; treat CJK/emoji/other scripts as optional lazily-downloaded font packs
   (TrueType `glyf` only, or extend `TrueTypeOutlines` to CFF).
8. **Threads.** Single-threaded WASM for v1; document that `Task.Run` doesn't parallelise there. Revisit threaded WASM
   later.
9. **Windows.** Secondary forms become in-canvas layers. On the web, `Show()` and `ShowModal` (async) draw a window
   inside the canvas.
10. **Code protection.** Accept that web builds are inspectable, or use AOT-compiled WASM with IL stripped
    (`WasmStripILAfterAOT`), which is harder to read but not secret.
11. **Input.** Add pointer (touch/pen) events and a soft-keyboard path to `Form`; needed on phones and useful on
    desktop tablets too.
12. **Scale changes.** Fix gap 8 first: browser zoom and moving between monitors change `devicePixelRatio`.

### 5.5 Work outline

| Step | Size | Notes |
|---|---|---|
| Spike: render the Hello form in a `browser-wasm` app with Skia+HarfBuzz WASM and compare against the golden PNG | S | **Do this first.** The result decides whether "same pixels" holds on the web. Watch for SkiaSharp/HarfBuzzSharp native-asset versions that must match the .NET WASM toolchain |
| Canvas presentation (`putImageData`, dirty rect, DPR scaling) | S | |
| Pointer, keyboard, wheel, cursor mapping | S | `Key` enum maps from `KeyboardEvent.code` |
| Hidden textarea: text, IME composition, paste | M | Wire to `Form.ProcessTextInput/Composition`; position from `TextInputArea` |
| Async modals and file service (decisions 5, 6) | M | Touches core API and samples |
| Timers/tick via `requestAnimationFrame` and idle handling | S | |
| Fonts: bundled fallback packs, lazy load | M | |
| Packaging: `dotnet publish` to static files, Brotli, template `radsharp-web`, CI job | M | |
| Multiple forms as in-canvas windows | M | |
| Touch input and soft keyboard | M | |

### 5.6 Risks

- **Skia raster differences in WASM** (scalar vs SIMD paths) could break byte-identical goldens. The spike finds out.
- **Download size and start-up**: .NET runtime + IL + Skia + fonts is several MB before compression; measure in the
  spike and set a budget.
- **Interpreter speed** for text-heavy repaints; AOT may be needed, at the cost of a larger download.
- **Clipboard and IME quirks per browser** (Safari especially); needs manual testing, not just tests.
- The **JS-interop boundary** is the only slow path; keep it to one call per frame.

## 6. Bottom line

**Status as of 2026-09-28:** 1 and 3 below are done (M11/M12); the rest are still the real list.

RadSharp's core is a credible base for real apps. For 1.0 the gaps that hurt most are not exotic:

1. ~~safe UI-thread access and error handling~~ — **done**,
2. a real grid and fuller list/tree controls,
3. ~~a complete canvas and per-control fonts~~ — **done**,
4. a proper window and DPI model,
5. reusable frames,
6. ~~proof that the pixel-identical promise holds on Linux~~ — **done (WSL2 Ubuntu x64, 2026-09-28)**; macOS still unverified, deprioritized.

The web can wait until after 1.0. Its shape is clear and mostly bounded: a new browser host on Mono WASM with
Skia/HarfBuzz WASM, presenting a CPU bitmap. The one decision that reaches into today's API is **synchronous modals
and file paths**; adding async modals and stream-based file results before 1.0 (section 5.0) costs little now and
avoids a breaking change later. When the web work starts, a one-to-two-day spike (section 5.5, first row) removes the
largest uncertainty before committing.

### Sources

- [Blazor WebAssembly native dependencies](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-native-dependencies?view=aspnetcore-10.0)
- [SkiaSharp.NativeAssets.WebAssembly](https://www.nuget.org/packages/SkiaSharp.NativeAssets.WebAssembly)
- [HarfBuzzSharp.NativeAssets.WebAssembly](https://www.nuget.org/packages/HarfBuzzSharp.NativeAssets.WebAssembly/)
- [SDL3 Emscripten README](https://wiki.libsdl.org/SDL3/README-emscripten)
- [dotnet/runtime: NativeAOT WASM CoreLib stub (#134210)](https://github.com/dotnet/runtime/pull/134210)
- [dotnet/runtime: WASM threading tracking issue (#68162)](https://github.com/dotnet/runtime/issues/68162)
- [Blazor download size and AOT](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/app-download-size?view=aspnetcore-10.0)
- [Avalonia on WebAssembly](https://avaloniaui.net/avalonia/wasm), [Uno SkiaSharp WASM](https://platform.uno/blog/skiasharp-support-for-webassembly-via-uno-platform/), [SkiaSharpWeb](https://github.com/wieslawsoltes/SkiaSharpWeb)
