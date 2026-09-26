# Capability review, real gaps, and a web (WASM) target

> Written 2026-09-26 from a read of the source (`src/`, `docs/`, roadmap, tests, CI), not from running the app.
> Claims about behaviour come from the code; where I inferred something it says so. Delphi comparisons are from
> general knowledge of VCL/FMX/RAD Studio, not from a licensed copy.
>
> Scope, as requested: what stops RADSharp being used to build **real cross-platform applications**, with no bells
> and whistles. Then: what a browser (WebAssembly) target needs, and which decisions have to be made first.
>
> **Web is an eventual goal, not a 1.0 requirement.** Sections 2–4 are the 1.0 material. Section 5 is post-1.0
> planning; its only 1.0 impact is a short list of *no-regret* decisions (5.0) that keep the door open cheaply.

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
- **Tests.** 153 tests including layout vectors, golden images, simulated input, designer-drives-designer.

## 2. Gaps that block real applications

Ordered by how badly a typical business or utility app is hurt. **P0** = an ordinary app cannot be finished
without a workaround; **P1** = painful but workable; **P2** = polish.

### P0

| # | Gap | Evidence | Delphi/RAD Studio has |
|---|---|---|---|
| 1 | **No UI-thread marshalling in Core, no `SynchronizationContext`.** `Application.Post` exists only in the SDL package. `await httpClient.GetAsync(...)` in a handler resumes on a thread-pool thread and touches controls off-thread. | `Application.Post` (`Platform.Sdl/Application.cs:97`); no `SynchronizationContext` anywhere | `TThread.Synchronize/Queue`, `Application.ProcessMessages` |
| 2 | **An exception in any event handler is unhandled.** The event loop has no try/catch around `Dispatch`; the only guard is in the live-resize watch. A typo in a handler closes the app. | `Application.RunLoop` / `Dispatch` | `Application.OnException`, default error dialog |
| 3 | **No grid.** No StringGrid/DrawGrid equivalent, and `ListView` is a minimal report list (below). Tabular data is the core of most business apps. | roadmap "After 1.0" lists it | `TStringGrid`, `TDrawGrid`, data-aware grids |
| 4 | **List controls are too thin to hold data.** `ListView`: no scroll bars (wheel only), no horizontal scroll (columns wider than the view are unreachable), no icons, checkboxes, multi-select, sorting, or virtual mode. `TreeView`: no scroll bars, images, checkboxes, multi-select; nodes only from code (not in the form file). `ListBox`: `string[]` items only (no `Tag`/objects), no multi-select. `ComboBox` publishes only Items/ItemIndex/DropDownCount (no editable style or autocomplete as far as I could see). | `ViewControls.cs`, `ListControls.cs` | `TListView` (icons, checks, `OwnerData`), `TCheckListBox`, `TTreeView` state images |
| 5 | **The `Canvas` can't draw lines, paths, gradients or arcs.** It has rects, round rects, ellipses, a check mark, text and pictures; the `SKCanvas` is private. A `PaintBox` chart, diagram or custom control can't draw a line. | `Drawing/Canvas.cs` (read in full) | `TCanvas` (LineTo, Polygon, Arc, Draw…) |
| 6 | **No per-control font.** Only `Label` has `FontSize/FontBold/FontColor`; the font is the theme's. No family, italic, underline, and no way to add a family from the form file. | `BasicControls.cs`, `Theme.FontFace` | `Font` on every control |
| 7 | **Windows are under-specified.** No `WindowState` (maximize/minimize/fullscreen), border style (dialog, tool, none), start position (centre, remembered), window icon, min/max size passed to the OS, always-on-top, or a screen/monitor API. | grep for `WindowState`/`Icon`/`Position` in Core and Sdl: no hits | `Position`, `WindowState`, `BorderStyle`, `BorderIcons`, `Icon`, `Screen` |
| 8 | **Scale is fixed at form load.** `Form.DefaultScale` is read once at `Initialize`; dragging a window to a monitor with a different DPI (or, on the web, zooming the page) doesn't re-layout at the new scale. (Inferred: pixel density is tracked per window for pixels ↔ points, but lengths are scaled once at load.) | `Application.Initialize`, `Form.Scale` | per-monitor-v2 DPI, `ScaleBy` |
| 9 | **Reusable UI.** No frames / user controls (a form file whose root is a `Panel` embeddable in other forms), no visual form inheritance. Every real app needs shared composite pieces. | roadmap "After 1.0" | `TFrame`, visual inheritance |
| 10 | **Cross-platform claim is unverified.** The roadmap says the CI matrix has not run (no remote); the README says macOS and Linux "run in CI". Golden images were generated on Windows. "Same pixels on every OS" is the headline promise, and Skia's CPU raster may pick different SIMD paths on ARM (Apple Silicon) than on x64. | roadmap M0, M2 status; `README.md` | n/a |

### P1

| # | Gap | Notes |
|---|---|---|
| 11 | **Data binding.** Listed for after 1.0. Without it every app copies model ↔ controls by hand. Smallest useful version: a generator-emitted, AOT-safe binding of a property path to a control property with change notification. | Delphi: DataSource / LiveBindings |
| 12 | **Tooltips.** `Control.Hint` is published but nothing displays it. | trivial to fix; users expect it |
| 13 | **Keyboard mnemonics.** No `&File` / Alt+letter in captions or menus. Menus have shortcuts but not accelerator letters. | accessibility and speed for keyboard users |
| 14 | **Text-field basics.** No built-in context menu (right-click cut/copy/paste/select all) on Edit/Memo; no masked edit; no bidirectional text (Arabic/Hebrew unusable); IME unverified by hand on macOS/Linux. | bidi is a hard blocker for those markets |
| 15 | **Accessibility.** Custom-drawn controls are invisible to screen readers. AccessKit is planned after 1.0. Some customers cannot buy software without it. | |
| 16 | **Layout depth.** Only Align + Anchors: no flow/grid/stack panels, no auto-size labels or buttons, no preferred-size. Localized or DPI-scaled text overflows fixed rects. | Delphi has `TFlowPanel`, `TGridPanel`, `AutoSize`; FMX has flow/grid layouts |
| 17 | **Drag and drop / OS file drop.** Neither in-app drag nor dropping files from the desktop (SDL supports the latter). | |
| 18 | **Input beyond mouse and keyboard.** No touch, pen or gesture events in the input model (SDL emulates mouse from touch by default, which covers taps). Matters for tablets and for the web on phones. | |
| 19 | **Images.** No SVG (raster only), no image on plain buttons, no animated images. Icons at several DPIs need SVG or multi-resolution lists. | |
| 20 | **Clipboard is text only.** No images or file lists. | |
| 21 | **Idle cost.** The loop wakes at ~60 Hz forever (`SDL_WaitEventTimeout(…, 16)`). Fine for a demo, poor for a laptop app left open. Compute the next deadline (caret blink, timers) instead. | |
| 22 | **Localization.** No mechanism; strings live in the form file. | Delphi: resource strings, ITE |
| 23 | **Designer.** No alignment/size tools (align lefts, same width), no zoom, PageControl page editing only by clicking tabs, no menu-editor altShortcut/separator fields, component libraries from NuGet not yet used in live runs. (All listed as "not yet" in the roadmap.) | |
| 24 | **Debugging.** External IDE only; F9 runs inside the designer with no debugger story. F5 in the IDE works since the project is ordinary .NET, but design edits need the designer. A VS Code/Rider extension is the natural fix. | Delphi's integrated debugger is part of "the Delphi loop" |
| 25 | **AOT gap between F9 and Build.** F9 runs on the JIT; Build is NativeAOT. Reflection-based code (many JSON, DI, ORM libraries) works in F9 and fails or trims away in the published exe. Turn on the AOT/trim analyzers in the template and list an AOT-safe stack in the docs. | |
| 26 | **Distribution.** No `.app` bundling, no exe icon or version resource, no installer/MSIX/AppImage tooling; three native libraries ship next to the exe; the designer is 230 MB and building needs the SDK plus C++ tools, which strains "light". Signing is documented but not automated. | |

### P2 (API polish)

- `Timer` collides with `System.Threading.Timer` under implicit usings; `Form.Color` shadows `RadSharp.Drawing.Color`
  (the roadmap already flags this one).
- No printing, no sound/media, no settings/INI helper (`Environment.GetFolderPath` covers most of it).
- `MessageDlg`, `OpenDialog.Execute()` and `ShowModal()` are synchronous (nested loops). Fine on desktop; a problem
  for the web (section 3).

## 3. Comparison with Delphi / RAD Studio, by area

| Area | Delphi VCL/FMX | RADSharp today | Verdict |
|---|---|---|---|
| Form format + code-behind | .dfm/.fmx + .pas | `.radform.json` + partial class, compile-time binding | **On par** (arguably stricter) |
| Layout | Align, Anchors, plus Flow/Grid/Stack panels, AutoSize | Align, Anchors, constraints | Missing flow/grid/auto-size |
| Standard controls | ~40 common, plus grids, calendar, date picker, rich edit, masked edit, check-list, up-down | ~35 (no grid, date/calendar, rich/masked edit, check-list) | Missing data-entry set |
| Data | DataSets, FireDAC, LiveBindings, data-aware controls | none | Missing (bindings are a start) |
| Custom drawing | Full canvas | rects/ellipses/text/images only | Missing lines/paths/gradients |
| Actions | `TActionList` (one command, many UI surfaces) | none (menu items and buttons share handlers) | Missing; a light `Action` would help |
| Reuse | Frames, inheritance, packages, DataModules | none yet | Missing |
| Windows/App | full window model, `Screen`, `Application` events, tray | Show/ShowModal/Close only | Missing basics |
| Threading | `TThread`, `Synchronize`, `TTask` | none in Core | Missing |
| Localization/RTL | yes | none / none | Missing |
| Accessibility | UIA/MSAA (VCL); FMX partial | none | Missing |
| IDE | inspector, structure, tab order, align tools, code editor, debugger, refactoring | inspector, structure, tab order, external editor, Roslyn rename | Fine for "light"; debugger via the IDE |
| Output | one exe (VCL), mobile, macOS, Linux | NativeAOT exe + 3 libs, Win/mac/Linux | **Ahead** on code protection; behind on mobile |
| Web | none native (third parties transpile to JS) | not yet | Opportunity |
| Pixel identity across OS | not a goal (VCL native; FMX styles) | goal, tested | **Ahead**, if CI confirms it |

## 4. Suggested order of work (native)

Small first, because several P0s are a day each:

1. **Robustness (S):** UI-thread marshalling in Core (`Form.Queue`, an installed `SynchronizationContext` so `await`
   works), an `Application.OnException` with a default dialog, tooltips, Edit/Memo context menu.
2. **Get CI green on macOS and Linux (S–M):** push the repo, look at the golden diffs, decide whether ARM needs
   its own goldens or a tolerance of 0. Everything else depends on trusting this.
3. **Canvas completeness (S):** lines, polylines, paths, arcs, gradients, offscreen bitmaps; per-control `Font`.
4. **Window model (M):** `WindowState`, border style, position, icon, min/max size, and per-monitor scale change.
5. **Lists and grid (L):** scroll bars and horizontal scroll in ListView/TreeView, icons, checkboxes, multi-select,
   sort; then a StringGrid/DrawGrid with virtual mode.
6. **Frames / user controls (M):** allow a non-Form root in `*.radform.json`.
7. **Data binding (M–L).**
8. **Layout panels and auto-size (M), mnemonics (S), file drop (S), SVG (M).**
9. **Bidi, accessibility (L each)**, decided by target market.

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

### 5.1 What is already in RADSharp's favour

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

RADSharp's core is a credible base for real apps. For 1.0 the gaps that hurt most are not exotic:

1. safe UI-thread access and error handling,
2. a real grid and fuller list/tree controls,
3. a complete canvas and per-control fonts,
4. a proper window and DPI model,
5. reusable frames,
6. proof that the pixel-identical promise holds on macOS and Linux.

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
