# Brainstorm: RADSharp, a light, Delphi-like cross-platform RAD tool for C#

> **What this is:** a self-contained brainstorm for a *new* project, started 2026-09-26. It grew out of
> **GMForms**, a Delphi-style form system and visual designer built for GameMaker (a game engine with its
> own scripting language, GML). You don't need GMForms or GameMaker to use this document: the parts
> worth reusing (form file format, layout rules, input/popup rules, conformance tests) are written out
> in full in [docs/spec.md](docs/spec.md) (formerly Appendices A–D), and the tests also exist as machine-readable vectors in `spec/vectors/`.
> The GMForms source is at `D:\dev\gamemaker\GMForms` for deeper reference (Appendix E).
>
> **Name:** *RADSharp* is the **working title** (earlier placeholder: DamonForms). The released product
> name must be vetted before release (NuGet, GitHub, domain, trademark; note Telerik's "Rad" control
> prefix). Until then, nothing durable carries the name: the form format ID is the neutral `"radform"`.
>
> **Status:** the direction was chosen (C#, custom-drawn controls, pixel-perfect Skia rendering; section 4) and
> built: milestones M0–M9 in `RADSharp-roadmap.md` are implemented. See `README.md` and `docs/`.

## 1. What we're after

- A **cross-platform RAD tool** for small utility apps, and possibly bigger apps later.
- A **super-easy, "light Delphi" experience:** drop controls on a form; double-click to write a handler;
  press F9 to run; press Build to get an executable.
- **Deployment that doesn't expose the app's code** as readable scripts or easily decompiled IL.
- **Delphi-style forms on every OS:** our own custom-drawn VCL/FMX-style controls, not native widgets.

### What made Delphi feel good (the bar to aim for)
1. The form designer and the code stay in step: double-click a button, land in its handler.
2. The Object Inspector is driven by *published* properties (runtime type information).
3. The output is a single native executable, with nothing to install.
4. Compile-and-run is fast enough that F9 feels instant.
5. Components are dropped from a palette and extended by installing packages.

## 2. Background: what GMForms proved

GMForms was built in milestones on GameMaker and reached a working visual designer. Its design choices,
all of which carry over to any language:

- **Controls are a retained tree** (each control has a parent, children, bounds and properties),
  not immediate-mode drawing.
- **Layout is Delphi's Align + Anchors**, not flexbox or nested HBox/VBox (spec: Appendix B).
- **Forms are JSON files.** Only non-default properties are saved; events are saved as handler *names*
  (spec: Appendix A).
- **Each control class publishes property metadata** (name, type, default, allowed values, range).
  One list drives the serializer, the property inspector and the designer, standing in for runtime
  type information.
- **A popup layer** in the root form handles menus, dropdowns and colour pickers (rules: Appendix C).
- **The designer is built with the framework itself.** It shows the designed form as a live instance
  whose input is never processed, so clicks select instead of firing events. It uses JSON snapshot undo
  (every edit = one snapshot of the whole form) and generates handler stubs.
- **Headless UI tests go through the real input code.** Functions like `simulate_click(x, y)`,
  `simulate_drag(...)` and `simulate_key(key)` feed the same press/move/release/key routines as real
  input. The GameMaker version ended with 500 such checks; the layout checks became Appendix D.
- **Deterministic JSON output** keeps form files diffable in git.

**Lessons learned the hard way:**
- Self-drawn UI inherits the host's limits: GameMaker gave a single window, no native file dialogs,
  weak IME and no accessibility. The new platform layer must provide these (section 5).
- A runtime that **can't load code at runtime** makes a live "run the form with real handlers" preview
  inside the designer impossible. .NET can (Roslyn + collectible `AssemblyLoadContext`), which is part
  of why C# was chosen.

## 3. Architectures considered

### A. C core + embedded script, player + bundle

A C core on raylib or SDL, handlers in Lua (or **Luau**, typed Lua with a real analyzer/LSP, or Teal),
and a prebuilt **player** executable per platform with the project appended, the way LÖVE "fuses" games.
- ✅ Lightest; one small download; live F9 is easy because the tool contains the interpreter;
  script plugins load at runtime.
- ⚠️ Weak protection: Lua bytecode decompilers exist (unluac) and the decryption key ships in the player.
- ⚠️ "Export for every OS from any machine" has caveats: appending data to a signed macOS binary breaks
  its signature (use `Contents/Resources` instead), macOS needs signing and notarization, and Windows
  SmartScreen and antivirus heuristics dislike unsigned executables with appended payloads.
- ⚠️ We would build the editor tooling ourselves.

### B. Native compile per language

- **C#:** custom-drawn controls, metadata from a source generator, NativeAOT output. **Chosen** (section 4).
- **Python:** pyray or SDL, shipped with Nuitka (compiles to C). Slower loop, heavy builds, each OS built
  on that OS; the weakest fit for "light". Avoid PyInstaller when hiding code matters.

### C. Hybrid

One designer and one form format, code generators for several languages, each runtime passing the shared
conformance tests. Covers everything, costs the most; a possible later phase, since the spec (Appendices
A–D) is already language-neutral.

### D. Build on something that exists

- **Godot** already has a self-hosted designer, live scripting and export templates for every OS. Heavy
  and game-oriented, but a real alternative to building.
- **Avalonia** is a custom-drawn, Skia-based, cross-platform C# toolkit with NativeAOT support. We could
  build only the Delphi layer on it (Align + Anchors, form files, designer, code-behind) and get text
  input, IME and accessibility for free. Kept as the **fallback if our own text editing stalls**
  (roadmap M5).

## 4. Chosen direction: C# with custom-drawn controls

Anders Hejlsberg designed both Delphi and C#, and it shows: properties, events, attributes and partial
classes map directly onto Delphi's component model.

| Area | Decision |
|---|---|
| Window, input, IME, clipboard, native dialogs, multiple windows | **SDL3** (via SDL3-CS bindings) |
| Rendering | **Pixel-perfect Skia**: SkiaSharp CPU raster → SDL3 texture (section 5) |
| Controls | Our own VCL/FMX-style hierarchy, custom-drawn, one consistent theme on every OS |
| Layout | Align + Anchors (Appendix B) |
| Published properties | `[Published]` attribute + a **source generator** emitting metadata; no runtime reflection, so AOT-safe |
| Form files | JSON (Appendix A), embedded as a resource and **streamed at runtime**, like Delphi's `.dfm` |
| Code-behind | A **source generator reads the form JSON** and emits the other half of a `partial class`: typed fields (`btnSave`) and a `Bind()` method wiring `onClick` → `btnSave_Click`. A missing handler is a **compile error**. The user owns only `MainForm.cs`, like the `.pas` next to a `.dfm` |
| Non-visual components | Timer, dialogs, ImageList etc. sit in a component tray under the form, as in Delphi |
| Designer | Built with RADSharp itself; runs on JIT (not AOT) so it can load user code |
| F9 | Compile in-process with **Roslyn**, load into a collectible `AssemblyLoadContext`, run the form live with real handlers, unload on close |
| Code editor | **External**: VS Code, Rider or Visual Studio. Double-click inserts the stub with Roslyn and runs e.g. `code -g MainForm.cs:42`. IntelliSense and debugging come for free |
| Build | `dotnet publish -p:PublishAot=true`: a native executable, no IL to decompile |

**What stays hard:**
1. **Text editing controls** (Edit, Memo): caret, selection, undo, IME composition, bidirectional text.
   The largest single piece of work.
2. **Designer ↔ code sync:** renaming a control or handler must update the `.cs` file (Roslyn rename).
3. **Accessibility:** custom-drawn controls are invisible to screen readers until AccessKit is integrated.
4. **"Light":** users need the .NET SDK, and NativeAOT on Windows also needs the C++ build tools.
   F9 needs only the SDK; the build tools matter only at Build time.

## 5. Pixel-perfect Skia rendering

The promise: **a form looks identical, pixel for pixel, on Windows, macOS and Linux**, and the designer
shows exactly what the running app will show.

**Pipeline:** SkiaSharp draws on the CPU into a pixel buffer → SDL3 uploads it as a texture and presents
it → only dirty rectangles are redrawn, which keeps CPU drawing fast enough for form UIs even at 4K.

| Source of differences | Rule |
|---|---|
| System fonts differ by OS | **Bundle fonts** with the framework; never use system fonts by default |
| OS text rendering (DirectWrite, CoreText, FreeType) | No OS font engine at all: HarfBuzz shapes, glyph outlines are read from the TrueType file by our own code, and Skia fills them as anti-aliased paths (no hinting, no LCD subpixel). Even Skia's `GetGlyphPath` is avoided, since it goes through the OS engine |
| Blurry 1 px lines | Pixel snapping: layout rounds to whole pixels (Appendix B), 1 px strokes on half-pixel centres, fills on whole pixels |
| Fractional DPI (125 %, 150 %) | Scale first, then round, per value (Appendix B). Optional strict mode with whole-number scales only |
| Characters the bundled fonts lack (CJK, emoji) | Fall back to further **bundled** fonts first (still identical everywhere), then to system fonts drawn by Skia (colour emoji work, but those glyphs can differ per OS) |
| GPU vs CPU differences | CPU raster only. A GPU backend may come later as an option, without the pixel-exact guarantee |

**Payoff: golden-image tests.** Render a form headlessly to PNG, compare against a stored image, and fail
CI on any changed pixel on any OS. Together with simulated input, that makes UI regressions visible.

**Trade-offs:** apps look like RADSharp everywhere rather than native on each OS (FMX's trade-off too),
and CPU rendering isn't meant for heavy animation or huge canvases.

## 6. Technical realities

- **SDL3** provides multiple windows, native file dialogs, IME text input, clipboard and Hi-DPI.
  Keep a small platform interface anyway, so a web (WebAssembly) target stays possible.
- **Native libraries:** Skia and SDL ship next to the executable, so a build is "one executable + two
  native libraries", not literally one file.
- **Cross-OS builds:** NativeAOT builds only for the host OS. Users need a CI matrix (e.g. GitHub
  Actions); the tool should generate that workflow for them.
- **Distribution:** macOS needs code signing and notarization; Windows SmartScreen warns about unsigned
  executables. Document both; don't pretend they away.
- **Text:** UTF-8 throughout; grapheme-aware cursor movement; IME is where self-drawn toolkits usually fall short.
- **Accessibility:** AccessKit (Rust, with C bindings) is the known bridge; after 1.0.

### Code protection, weakest to strongest

| Approach | Effort to read the logic |
|---|---|
| Plain `.py` / `.lua` / JSON | None |
| PyInstaller bundle | Low (extract + decompile bytecode) |
| .NET single-file (IL) | Low (ILSpy recovers near-source) |
| Lua bytecode + encryption in the player | Low to medium (the key ships in the player; decompilers exist) |
| Nuitka (Python → C → native) | High |
| **.NET NativeAOT** / C / Zig | High |

Nothing shipped to a user's machine is truly secret. The goal is "not trivially readable". Form JSON is
embedded as a resource and is readable; that's layout, not logic.

## 7. Existing tools to compare against

- **Delphi FMX** (Community Edition is free): the original cross-platform custom-drawn Delphi.
- **Lazarus / Free Pascal:** a cross-platform Delphi clone with native widgets. The benchmark for "Delphi experience".
- **Xojo:** commercial, cross-platform "light VB/Delphi" with single-executable builds. The most direct comparison.
- **PureBasic, B4J:** small, self-contained, form designers, native output.
- **Avalonia** (C#, XAML): custom-drawn, Skia, NativeAOT; not "light" and not Delphi-style. Possible base (3.D).
- **Godot:** designer + live scripting + export templates; proves the player model; heavy for utilities.
- **Qt Designer + PySide6 + Nuitka:** covers much of the Python case, but heavy.
- **Slint:** declarative UI with live preview (Rust/C++/JS/Python).
- **Radzen:** a visual designer for .NET, but for Blazor web apps.
- **WinForms:** the Delphi-like C# designer experience, Windows-only. The closest in feel to what we want.

**The gap:** WinForms' designer experience, Delphi's layout and streaming, cross-platform with identical
pixels, and NativeAOT output, in a light tool. Xojo and Delphi FMX come closest; neither is C#.

## 8. First experiment

1. Port the Appendix B layout engine to C# (`RadSharp.Core`, no drawing) and run the JSON vectors in
   `spec/vectors/` with xUnit on Windows, macOS and Linux.
2. Open an SDL3 window with Skia CPU rendering, load `d1-align.json`'s form, draw Panel/Button/Label,
   and resize the window to watch it reflow. Render the same form headlessly to PNG as the first golden image.
3. Write the source generator: form JSON → partial class with typed fields and handler binding. F5 in
   VS Code should run it, and `dotnet publish -p:PublishAot=true` should produce a native executable.

**Success means the core loop works:** the spec is portable, rendering is identical, and generated code
binds the form to handlers without reflection.

## 9. Open questions

- **Released product name:** to be vetted before release (RADSharp is the working title).
- **Form file extension:** `.radform.json` (editors recognise JSON) or `.radform`.
- **Target .NET version** and minimum OS versions.
- **Default theme:** neutral/modern or classic Delphi.
- **Project model:** several forms, navigation, data binding (`"text": "{doc.title}"`), assets, localization. When?
- **Component packages:** third-party controls as NuGet packages that appear on the palette.
- **Licence**, if the project is published.

---

## Appendices A–D: the specification

The form file format, layout rules, input/focus/popup rules and conformance tests have moved to
**[docs/spec.md](docs/spec.md)**, which is now the single source (they grew from this brainstorm's appendices).

## Appendix E: The GMForms reference implementation

The appendices above are enough to start. For anything they don't cover, the working GameMaker
implementation is on this machine at **`D:\dev\gamemaker\GMForms`** (a git repository; the M1–M6
milestones are committed). It's GML, which reads much like JavaScript.

**Differences from this spec:** GMForms uses `"format": "gmforms"`, an integer `version`, snake_case
property names (`min_width`, `tab_stop`) with a camelCase → snake_case mapping for events, GameMaker's
rounding, and none of the D.3b/D.4 clamping rules above. Converting a GMForms file means renaming the
format, the version and the snake_case keys.

| Topic | File (relative to the repo) |
|---|---|
| Project goals, decisions D1–D12, milestones | `gamemaker-form-designer-plan.md` |
| What's built, conventions, gotchas found along the way | `CLAUDE.md`, `README.md` |
| Control base class, **layout engine** (`arrange_children`, `_layout_anchored`) | `scripts/gmforms_control/gmforms_control.gml` |
| Root form: input manager, focus, **popup layer**, shortcuts, `simulate_*` | `scripts/gmforms_form/gmforms_form.gml` |
| **Serializer**, deterministic JSON writer, handler binding | `scripts/gmforms_serialize/gmforms_serialize.gml` |
| Controls | `scripts/gmforms_controls_*/` (basic, edit, viewport, propgrid, lists, menus, containers, values) |
| Undo helper (commands, groups, JSON snapshots) | `scripts/gmforms_undo/gmforms_undo.gml` |
| **Designer** (canvas, app, handler stub generation, scripted rebuild) | `scripts/designer_*/` |
| The full test suite (500 checks; Appendix D is drawn from it) | `scripts/gmforms_tests/gmforms_tests.gml` |
| A real designed form (menus, toolbar, events by name) | `datafiles/tile_editor.gmform.json` |
| Headless build + run of the tests via GameMaker's CLI | `tools/gmtest.sh` |

If the repository moves, only this appendix needs updating.
