# Known limits

What RadSharp does not do yet, so you can decide before you build on it. Most of these are planned for after 1.0; see
[gap-plan.md](gap-plan.md) for the plan and [reference-app-scope.md](reference-app-scope.md) for why the 1.0 line sits
where it does.

## Preview releases (0.x)

- **APIs can change** between previews and before 1.0. Form files (`*.radform.json`, format version 1.0) written by a
  preview are meant to keep loading in 1.0.
- **Builds are not code-signed.** Windows SmartScreen may warn on the designer and the VSIX; the macOS designer needs its
  quarantine attribute removed (`xattr -dr com.apple.quarantine .`). Apps you build with RadSharp can be signed by your
  own workflow; see [distribution.md](distribution.md).
- **Tested most on Windows.** Linux runs the full test suite in CI and under WSL2. macOS is checked on a local Mac, not
  on every CI run. IME input and the designer on macOS and Linux have had little hand-testing.
- **The macOS designer is a plain executable**, started from Terminal, not an `.app` bundle. Apps you build do get an
  `.app` bundle.
- **The Visual Studio extension** is on the GitHub release page, not the Visual Studio Marketplace. The project's own
  controls in the VS Toolbox are covered by automated tests but have not yet been checked by hand in Visual Studio.

## Text and input

- **No right-to-left or bidirectional text.** HarfBuzz shapes the glyphs, but caret movement, selection and Unicode bidi
  ordering are not implemented.
- **No accessibility support.** Screen readers don't see RadSharp controls (no UI Automation, NSAccessibility or AT-SPI).
- **Clipboard is text only.**
- **No localization.** There is no resource lookup for form strings and no locale switch in the designer.

## Controls

- **Dates only:** `DateTimePicker` and `Calendar` have no time of day, no date ranges and no culture-specific long formats.
- **No `MaskEdit`.**
- **`SpinEdit` is whole numbers only.**
- **`StatusBar` shows one text** (`SimpleText`); it has no panels.
- **No `FlowPanel` or `GridPanel`,** and no `AutoSize` for labels and buttons. Layout is Align + Anchors.
- **No printing.** Export HTML (or draw to an image) and print from another app.
- **No SVG images.**
- **No web (WebAssembly) target.**

## Reuse and data

- **No frames and no visual form inheritance.** Reuse a control by writing it as a class in your project: the designer
  puts your project's controls on its palette.
- **Binding is in code** (`Binder<T>`, see [binding-and-actions.md](binding-and-actions.md)). There is no binding
  syntax in form files, and lists are not bound to collections (use a virtual `ListView` or `StringGrid`).

## Designer

- **No zoom,** and pages of a `PageControl` are added from the palette rather than edited in place.
- **Non-visual components** (timers, dialogs, image lists) show as icons on the form, not in a separate tray.
- **Panels are fixed** in a splitter layout; they can't be docked, floated or saved as layouts.
- **No built-in code editor or debugger.** The designer opens your IDE (VS Code, Visual Studio or Rider); debug there
  with F5 (see [debugging.md](debugging.md)).
