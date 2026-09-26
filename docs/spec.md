# RADSharp specification: form files, layout, input

This is the language-neutral part of RADSharp: any runtime that follows it can load any designed form.
The conformance tests are machine-readable in `spec/vectors/` (layout, Appendix D) and
`spec/vectors/serialization/` (form files). *RADSharp is a working title; the format ID is the neutral `"radform"`.*

## A. Form file format (v1.0)

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
- In an app, the source generator binds names to methods **at compile time**; a missing method is a compile error
  (RAD1001, reported at the handler name in the form file, with the method signature to add).
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
ordered by name; a subclass entry with the same name overrides the inherited one; defaults are read from a
freshly constructed prototype of the class, so constructor defaults need no annotation):
`{ name, type, defaultValue, values?, labels?, min?, max?, step?, category?, readOnly? }`.
Types: `string, int, real, bool, enum, color, image, font, anchors, struct, array, event, menus`.
Enum values are written camelCased (`Align.Client` → `"client"`, `Cursor.SizeWE` → `"sizeWE"`).

**Generated form classes.** For `MainForm.radform.json` the generator emits `partial class MainForm : Form` with
an `internal` field per named control, `InitializeComponent()` and `DesignedClassName` (the file's root class,
which the writer saves instead of `MainForm`). The file is embedded as resource `MainForm.radform.json`.

## B. Layout rules (Delphi Align + Anchors)

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

## C. Input, focus and popup rules

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
  - While a popup is open, key presses (other than Tab) go to the topmost popup (or the control
    focused inside it), not the form's focused control. Typed text reaches only a control focused
    inside the topmost popup (an inline editor); otherwise it is discarded. An Escape the popup
    doesn't handle closes it.
  - Closing a popup also closes every popup opened above it (submenus).
  - Hiding, disabling or removing the control that opened a popup closes it.
- **Context menus:** a control's `popupMenu` opens on right-button press, at the pointer, after the
  press was delivered (so lists select the row under the pointer first).

## D. Conformance tests

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
