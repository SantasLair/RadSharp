# RADSharp (working title)

A light, Delphi-style RAD tool for C#: design forms visually, double-click to write a handler, press **F9** to
run it live, press **Build** to get a native executable — on Windows, macOS and Linux.

- **Delphi's model:** Align + Anchors layout, published properties driving an Object Inspector, form files next
  to a code-behind partial class, events bound by name (at compile time — a missing handler is a compile error).
- **Pixel-perfect everywhere:** custom-drawn controls rendered by Skia on the CPU with bundled fonts and our own
  glyph outlines, so a form looks identical on every OS (golden-image tests enforce it).
- **Live resizing:** forms reflow and repaint while you drag a window border (no frozen or stretched frames).
- **Native output:** NativeAOT executables (~3–4 MB + SDL3/Skia/HarfBuzz libraries); no runtime to install and no
  IL to decompile.

*RADSharp is a working title; the released name is still to be decided.*

## Status

All roadmap milestones M0–M9 are implemented (see [RADSharp-roadmap.md](RADSharp-roadmap.md)). It is a young
codebase: tested on Windows; macOS and Linux run in CI.

| | |
|---|---|
| `src/RadSharp.Core` | controls, layout, rendering, text, form reader/writer, component metadata |
| `src/RadSharp.Generators` | source generators: component metadata and form code-behind |
| `src/RadSharp.Platform.Sdl` | the SDL3 host: windows, input, IME, clipboard, dialogs, modal loops |
| `src/RadSharp.Design` | the design surface, document model, menu and tab-order editors |
| `src/RadSharp.Design.Code` | Roslyn: handler stubs, renames, live run (F9), builds |
| `src/RadSharp.Designer` | the designer app (itself a RADSharp form) |
| `samples/` | Hello, Calculator, TextEditor, SettingsDialog, TileEditor (the GMForms sample, rebuilt) |
| `spec/` | conformance vectors (layout and form files) |
| `templates/` | `dotnet new radsharp-app` |

## Quick start

```sh
dotnet test                                   # 153 tests: layout vectors, golden images, input, editors, generators, designer
dotnet run --project samples/TileEditor       # a sample
dotnet run --project src/RadSharp.Designer    # the designer
```

Read [docs/getting-started.md](docs/getting-started.md), the [control reference](docs/controls.md), the
[spec](docs/spec.md) and the [distribution notes](docs/distribution.md). The design background is in
[future-framework-brainstorm.md](future-framework-brainstorm.md).

## Licences

Inter (the bundled font) is under the SIL Open Font License 1.1 (`src/RadSharp.Core/Fonts/Inter-LICENSE.txt`).
RADSharp's own licence is still to be decided.
