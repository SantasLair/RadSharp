# Brainstorm: RADSharp, a light, Delphi-like cross-platform RAD tool for C#

> **What this is:** a self-contained brainstorm for a *new* project, started 2026-09-26. It grew out of
> **GMForms**, a Delphi-style form system and visual designer built for GameMaker (a game engine with its
> own scripting language, GML). You don't need GMForms or GameMaker to use this document: the parts
> worth reusing (form file format, layout rules, input/popup rules, conformance tests) are written out
> in full in Appendices A–D, and the tests also exist as machine-readable vectors in `spec/vectors/`.
> The GMForms source is at `D:\dev\gamemaker\GMForms` for deeper reference (Appendix E).
>
> **Name:** *RADSharp* is the **working title** (earlier placeholder: DamonForms). The released product
> name must be vetted before release (NuGet, GitHub, domain, trademark; note Telerik's "Rad" control
> prefix). Until then, nothing durable carries the name: the form format ID is the neutral `"radform"`.
>
> **Status:** the direction is chosen (C#, custom-drawn controls, pixel-perfect Skia rendering; section 4).
> The milestone plan is in `RADSharp-roadmap.md`. Everything else here is still open for discussion.

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

## Appendix A: Form file format (v1.0)

A form file is UTF-8 JSON:

```json
{
  "format": "radform",
  "version": "1.0",
  "generator": "RADSharp Designer 0.1",
  "root": {
    "class": "Form",
    "name": "frmMain",
    "caption": "Tile Editor",
    "height": 700,
    "width": 1024,
    "children": [
      { "class": "Panel", "name": "pnlToolbar", "align": "top", "height": 36,
        "children": [
          { "class": "Button", "name": "btnSave", "caption": "Save", "left": 6,
            "onClick": "btnSave_Click", "top": 4 }
        ] },
      { "class": "Viewport", "name": "vpMap", "align": "client", "onDraw": "vpMap_Draw" }
    ]
  }
}
```

**Header**
- `format` must be `"radform"`, a neutral ID so a product rename doesn't touch any file.
- `version` is `"MAJOR.MINOR"`. An unknown major version is rejected with a clear message. A newer minor
  version loads; properties it added are ignored with a warning (see below).
- `generator` is optional and informational (the tool that wrote the file). Writers put their own.

**Nodes**
- Each node has `class` (looked up in a **class registry**: class name → constructor), optional `name`
  (unique within the form; used by `FindControl(name)` and by generated fields), published properties,
  and an optional `children` array. `children` order = creation order = Align processing order = default Tab order.
- **Only non-default published properties are written.** A property not in the file takes the class default.
- Only properties the class **publishes** are read or written. Anything else is ignored with a warning.
- **Property names are camelCase** throughout (`minWidth`, `tabStop`, `popupMenu`, `onMouseDown`).
- **An unknown class** logs a warning and that node (with its subtree) is skipped; siblings still load.

**Events**
- Events are properties whose name starts with `on` (`"onClick": "btnSave_Click"`). The value is a handler *name*.
- In an app, the source generator binds names to methods **at compile time**; a missing method is a compile error.
- When a form is loaded without generated bindings (as the designer does), names are kept on the control
  so re-saving doesn't lose them.
- Handler convention: the **first argument is always the sender**, e.g.
  `void btnSave_Click(Control sender)` or `void vpMap_MouseDown(Control sender, MouseEventArgs e)`.

**Which bounds are saved**
- `align` `top`/`bottom`: only `height` (position and width are computed).
- `align` `left`/`right`: only `width`.
- `align` `client`: none of `left`, `top`, `width`, `height`.
- `align` `none` (anchored): `left`, `top`, `width`, `height` as laid out at the form's **design size**.
  Writers lay the form out at its design size before saving, so a stretched rect is never written.
  (Anchored layout returns exactly to design rects at design size, Appendix B.)
- The root form saves `width`/`height` as its design size.

**Menus** save their items as nested objects:
`{ "caption", "shortcut", "altShortcut", "onClick", "enabled", "checked", "visible", "separator", "items": [...] }`,
with only non-default values written.

**Deterministic output** (save → load → save is byte-identical, in every runtime)
- Top-level keys: `format`, `version`, `generator`, `root`.
- Node keys: `class`, `name`, `caption`, `text`, then the rest in **ordinal (code-point) alphabetical order**,
  then `items`, then `children`.
- Struct properties are written whole when any field differs from the default, fields in their
  declared order (`margin`/`padding`: `l`, `t`, `r`, `b`).
- `anchors` is written in the order `left`, `top`, `right`, `bottom`; `[]` means no anchors.
- Numbers: integers without a fraction (`700`, not `700.0`); reals in the **shortest round-trip** form,
  invariant culture, no exponent.
- Strings: UTF-8 written literally; escape only `"`, `\` and control characters (`\n`, `\r`, `\t`,
  otherwise `\u00XX`); `/` is not escaped.
- Colours: `"#RRGGBB"`, or `"#RRGGBBAA"` when alpha isn't `FF`; uppercase hex.
- Two-space indentation, `\n` line endings, a final newline, no trailing NUL byte.

**Base published properties** (every control; subclasses add their own and may change defaults):

| Property | Type | Default |
|---|---|---|
| `left`, `top` | int | 0, 0 |
| `width`, `height` | int | 100, 30 (subclasses override, e.g. Button 80 × 28) |
| `align` | enum `none, top, bottom, left, right, client` | `none` |
| `anchors` | set of `left, top, right, bottom` | `["left", "top"]` |
| `margin`, `padding` | `{l, t, r, b}` | all 0 |
| `minWidth`, `minHeight`, `maxWidth`, `maxHeight` | int (0 = no limit) | 0 |
| `visible`, `enabled` | bool | true |
| `tabStop` | bool | false (Button, Edit, … override to true) |
| `tabOrder` | int (-1 = use child order) | -1 |
| `hint` | string | "" |
| `onClick`, `onMouseDown`, `onMouseUp`, `onMouseMove`, `onMouseEnter`, `onMouseLeave`, `onMouseWheel`, `onKeyDown`, `onKeyUp`, `onFocus`, `onBlur`, `onChange`, `onResize` | event | none |

**Metadata entry shape** (emitted per class by the source generator from `[Published]` attributes;
ordered; a subclass entry with the same name overrides the inherited one):
`{ name, type, defaultValue, values?, labels?, min?, max?, step?, category?, readOnly? }`.
Types: `string, int, real, bool, enum, color, image, font, anchors, struct, array, event, menus`.

## Appendix B: Layout rules (Delphi Align + Anchors)

**Coordinates.** `left`/`top` are relative to the parent's top-left corner. After layout, each control
also knows its absolute position (parent absolute + left/top).

**Arithmetic.** Layout math is done in real numbers. Values are rounded **half away from zero**
(C#: `Math.Round(v, MidpointRounding.AwayFromZero)`; *not* the default banker's rounding) when a control
is placed. Divisions are real divisions (`aW / 2` of `241` is `120.5`).

**Scaling.** The runtime multiplies every stored length (`left`, `top`, `width`, `height`, margins,
padding, constraints) by the scale factor and rounds it **before** layout; layout then runs in physical
pixels. Conformance vectors are at scale 1.

**Constraints.** A control's size is constrained by `max*` (only when > 0), then `min*`, so `min` wins
if they conflict. Sizes are never negative.

**Arranging a container's children** (runs when the container is marked dirty):
1. Start with the container's **client rect** `R` = its own rect minus its `padding`.
   *Padding affects aligned children only.* A container can override its client rect (e.g. a tab
   control excludes its tab strip).
2. **Invisible children are skipped entirely.**
3. Process align groups **in this order**, each group in child order. `m` is the child's margin;
   `h`/`w` is the child's own height/width **after constraints**. The computed (stretched) dimension is
   also constrained, keeping the computed position.
   - **top:** place at `(R.x + m.l, R.y + m.t)`, width `R.w - m.l - m.r`, height `h`; then
     `R.y += h + m.t + m.b` and `R.h` shrinks by the same amount.
   - **bottom:** place at `(R.x + m.l, R.y + R.h - m.b - h)`, width `R.w - m.l - m.r`, height `h`;
     `R.h` shrinks by `h + m.t + m.b`.
   - **left:** place at `(R.x + m.l, R.y + m.t)`, height `R.h - m.t - m.b`, width `w`; then
     `R.x += w + m.l + m.r` and `R.w` shrinks by the same amount.
   - **right:** place at `(R.x + R.w - m.r - w, R.y + m.t)`, height `R.h - m.t - m.b`, width `w`;
     `R.w` shrinks by `w + m.l + m.r`.
   - **client:** every client child gets `(R.x + m.l, R.y + m.t, R.w - m.l - m.r, R.h - m.t - m.b)`.

   Each shrink is capped so `R.w`/`R.h` never go below 0 (`R.x`/`R.y` still advance in full).
   When the space runs out, later children overlap earlier ones; that's expected (vector D.3b).
4. **Anchored children** (`align = none`) are positioned against the parent's **full** size (not `R`):
   - **Capture:** store the control's rect and the parent size, `(aL, aT, aW, aH)` and `(PW0, PH0)`,
     the first time it is laid out in its parent, and **immediately** (with the parent's size at that
     moment) whenever it is moved or resized from code or its align/anchors change.
   - Then, for the current parent size `(PW, PH)`, with `dW = PW - PW0` and `dH = PH - PH0`, horizontally:

     | anchors | x | width |
     |---|---|---|
     | left + right | `aL` | `aW + dW` (stretches) |
     | right only | `aL + dW` | `aW` |
     | left only | `aL` | `aW` |
     | neither | `(aL + aW / 2) * PW / PW0 - aW / 2` (centre keeps its relative position) | `aW` |

     The same applies vertically with top/bottom and `dH`.
   - **Zero-size guard:** if `PW0` is 0, "neither" behaves as "left only" (same for `PH0` and "top only").
   - The computed size is then constrained.
   - Moving or resizing a control from code goes through a setter that **re-captures** the anchors.
     Assigning left/width directly on an anchored control would be overwritten by the next layout.
5. When a child's size changes, it is marked dirty so its own children re-arrange (`onResize` fires).

**Design size.** The root form's `width`/`height` in the file are its *design size*. Before the first
real resize, the form lays itself out once at design size, so anchors capture design-time distances.
The host then resizes the form to the window and everything reflows. Laying out at design size again
must reproduce every design rect exactly (no drift).

**Invalidation.** Changing bounds, align, anchors, visibility or children marks the control and its
parent dirty. Layout runs once per frame, before input and drawing, top-down, only on dirty subtrees.
Absolute positions are refreshed for the whole tree every pass.

**Splitter** (a control with `align` left/right/top/bottom and a `minSize`):
- Its **target** is the nearest earlier visible sibling with the same align (Delphi's rule).
- **Dragging away from the target grows it; dragging toward it shrinks it.** With `delta` measured
  along the axis (positive = right/down): left/top splitters add `delta` to the target's size;
  right/bottom splitters subtract it.
- The target never goes below `minSize`, and never grows so far that the space left for the parent's
  client area (`R` along that axis, before client children) drops below `minSize`:
  `max = targetSize + remaining - minSize`.

## Appendix C: Input, focus and popup rules

- **Hit testing:** the deepest visible control under the point wins. Children are clipped to their
  parent (to its *child clip rect*: a scroll box's viewport excludes its scroll bars); later children
  are on top. A press on a disabled control (or inside a disabled container) is ignored.
- **Containers and child coordinates:** anchored children are positioned from the parent's top-left
  corner (Delphi's rule, also for group boxes), except in a **scroll box**, whose children use content
  coordinates: (0, 0) is the viewport's corner when not scrolled.
- **Mouse capture:** the control that receives a button press gets all moves and the release until that
  button is released. A **click** fires on a left release only if the pointer is still over the captured
  control.
- **Focus:** a press focuses the control if it is a tab stop.
  - **Tab / Shift+Tab** cycles through visible, enabled tab stops, depth-first. Among siblings, controls
    with `tabOrder >= 0` come first, ascending (ties by child order), then controls with `tabOrder = -1`
    in child order.
  - Hiding, disabling or removing a control clears hover, capture and focus inside it.
- **Wheel** events bubble from the control under the pointer up through its parents until one handles them.
- **Keys** go to the focused control as key events (key + modifiers). A control that accepts tabs (a
  multi-line editor) gets Tab itself; otherwise Tab moves focus.
- **Enter / Escape** that no control handled click the form's *default* / *cancel* button. **Text input** arrives separately
  as text events and never contains control characters; IME composition arrives as composition events
  to the focused control. Clipboard commands (Mod+C, Mod+X, Mod+V, Mod+A) are key events.
- **Shortcuts:** a modifier + key or F1–F12 press is offered to the control tree after the focused
  control had its chance. Shortcut text uses **`Mod`** for the platform command key (Ctrl on
  Windows/Linux, ⌘ on macOS): `"Mod+Shift+Z"`. `Ctrl` means the Control key on every OS; `Alt` is
  Option on macOS. A menu bar matches its items' `shortcut` text.
- **Popup layer** (menus, dropdown lists, colour pickers):
  - Open popups are hit-tested before the form and drawn after it, above everything.
  - A press **outside every popup closes all of them and is consumed** (nothing underneath receives it).
  - **Escape** closes the topmost popup.
  - **Tab** closes all popups, then moves focus.
  - While a popup is open, key presses (other than Tab) go to the topmost popup, not the focused
    control, and typed text is discarded. An Escape the popup doesn't handle closes it.
  - Closing a popup also closes every popup opened above it (submenus).
  - Hiding, disabling or removing the control that opened a popup closes it.
- **Context menus:** a control's `popupMenu` opens on right-button press, at the pointer, after the
  press was delivered (so lists select the row under the pointer first).

## Appendix D: Conformance tests

Every runtime must reproduce these rects exactly (`left, top, width, height`, relative to the parent).
The tables below are for reading; **the JSON vectors in `spec/vectors/` are authoritative**.

### D.0 Vector file format

Each file holds one test object or an array of them:

```json
{
  "name": "D.4a Left splitter",
  "form": { "format": "radform", "version": "1.0", "root": { ... } },
  "steps": [
    { "expect": { "palLeft": [0, 0, 200, 300] } },
    { "do": "dragSplitter", "control": "splLeft", "delta": 30 },
    { "expect": { "palLeft": [0, 0, 230, 300] } }
  ]
}
```

- Loading the form lays it out at design size; the first step runs after that.
- Actions (`do`): `resize` (`width`, `height`: resizes the root form), `set` (`control`, `property`,
  `value`: sets a published property), `dragSplitter` (`control`, `delta`: Appendix B splitter rule).
  A runtime lays out after every action.
- `expect` maps control names to `[left, top, width, height]`; controls not listed are not checked.

### D.1 Align, margins and nesting (`d1-align.json`)

Form `1024 × 700` with these children, in this order:

| Name | Class | Properties |
|---|---|---|
| pnlToolbar | Panel | align top, height 40 |
| pnlStatus | Panel | align bottom, height 26 |
| pnlPalette | Panel | align left, width 180 |
| pnlInspector | Panel | align right, width 220 |
| pnlClient | Panel | align client |

Inside **pnlInspector**, in order:
- lblInspector: Label, align top, height 24;
- pnlInspectorBody: Panel, align client, margin `{l: 6, t: 0, r: 6, b: 48}`.

| Control | 1024 × 700 (design) | 1400 × 900 |
|---|---|---|
| pnlToolbar | 0, 0, 1024, 40 | 0, 0, 1400, 40 |
| pnlStatus | 0, 674, 1024, 26 | 0, 874, 1400, 26 |
| pnlPalette | 0, 40, 180, 634 | 0, 40, 180, 834 |
| pnlInspector | 804, 40, 220, 634 | 1180, 40, 220, 834 |
| pnlClient | 180, 40, 624, 634 | 180, 40, 1000, 834 |
| lblInspector | 0, 0, 220, 24 | 0, 0, 220, 24 |
| pnlInspectorBody | 6, 24, 208, 562 | 6, 24, 208, 762 |

After hiding pnlPalette (still 1400 × 900): pnlClient = `0, 40, 1180, 834`.

### D.2 Anchors (`d2-anchors.json`)

Inside **pnlClient** of the D.1 form (design size 624 × 634), anchored Buttons (height 28):

| Name | Design rect | Anchors | Expected at 1400 × 900 (pnlClient = 1000 × 834) |
|---|---|---|---|
| btnTL | 16, 46, 130, 28 | left, top | 16, 46, 130, 28 |
| btnTR | 478, 46, 130, 28 | top, right | 854, 46, 130, 28 |
| btnBL | 16, 588, 130, 28 | left, bottom | 16, 788, 130, 28 |
| btnBR | 478, 588, 130, 28 | right, bottom | 854, 788, 130, 28 |
| btnStretch | 16, 84, 592, 28 | left, top, right | 16, 84, 968, 28 |

A Panel **pnlBox** at `16, 124, 592, 450` with all four anchors becomes `16, 124, 968, 650`. Inside it,
a Button **btnCentre** at `176, 211, 240, 28` with **no anchors** keeps its centre proportional:
`364, 311, 240, 28` (centre (296, 225) in 592 × 450 → (484, 325) in 968 × 650).

Resizing back to 1024 × 700 returns every control to its design rect exactly.

### D.3 Constraints and edge cases (`d3-constraints.json`)

- **D.3a:** form 400 × 300 with one client Panel, `maxWidth 150, minHeight 500` → `0, 0, 150, 500`.
- **D.3b:** the D.1 form resized to **300 × 50**. No size is negative; later children overlap earlier ones:

| Control | Rect |
|---|---|
| pnlToolbar | 0, 0, 300, 40 |
| pnlStatus | 0, 24, 300, 26 |
| pnlPalette | 0, 40, 180, 0 |
| pnlInspector | 80, 40, 220, 0 |
| pnlClient | 180, 40, 0, 0 |
| lblInspector | 0, 0, 220, 24 |
| pnlInspectorBody | 6, 24, 208, 0 |

### D.4 Splitter (`d4-splitter.json`)

**D.4a:** form 600 × 300, children in order: palLeft (Panel, align left, width 200); splLeft (Splitter,
align left, width 4, minSize 40); cliMain (Panel, align client).

| Step | Expected |
|---|---|
| Initial | palLeft `0,0,200,300`; splLeft `200,0,4,300`; cliMain `204,0,396,300` |
| Drag +30 | palLeft `0,0,230,300`; splLeft `230,0,4,300`; cliMain `234,0,366,300` |
| Drag −1000 | palLeft width clamps to **40** |
| Drag +1000 | palLeft width clamps to **556** (cliMain keeps 40); cliMain `560,0,40,300` |

**D.4b (mirror):** form 600 × 300: pnlRight (Panel, align right, width 260); splRight (Splitter, align
right, width 4, minSize 40); cliMain (client). Initially pnlRight `340,0,260,300`, splRight
`336,0,4,300`, cliMain `0,0,336,300`. Dragging +10 (toward the target) gives pnlRight `350,0,250,300`.

### D.5 Serialization (prose for now; vectors to follow)

- Save → load → save must produce **byte-identical** text.
- A Button with default caption and width writes neither `caption` nor `width`; `class` is always written.
- A top-aligned Panel writes `height` but not `left`, `top` or `width`; a client-aligned Panel writes none of them.
- An anchored control saved after its parent was resized writes its **design** rect, not the stretched one.
- Loading a file with an unknown class skips that node (with a warning) and still loads its siblings.
- A wrong `format` or an unknown major `version` is rejected, not crashed on; a newer minor version loads
  with warnings for unknown properties.

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
