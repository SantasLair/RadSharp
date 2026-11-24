# RadSharp

**Delphi-style rapid application development for C#.** Draw a form, double-click a button to write its handler, press
**F9** to run it live, and press **Build** for a small native executable. Runs on Windows, macOS and Linux.

![The RadSharp designer in dark mode, editing a calculator form with the = button selected](docs/images/designer-dark.png)

## Why RadSharp

- **The Delphi workflow in C#.** Forms use Align + Anchors layout. An Object Inspector edits their published
  properties. Each form file sits next to a code-behind partial class, and events bind to handlers by name at compile
  time, so a missing handler is a compile error rather than a runtime surprise.
- **Pixel-identical on every OS.** Controls are custom-drawn by Skia on the CPU with bundled fonts, so a form looks
  the same on Windows, macOS and Linux. Golden-image tests enforce this.
- **Native, self-contained output.** `dotnet publish` produces a NativeAOT executable of about 3–4 MB, plus the SDL3,
  Skia and HarfBuzz libraries. Users install no runtime, and there is no IL to decompile.
- **A designer built with itself.** The designer is a RadSharp application, so every control it uses is one you can
  put on your own forms.
- **Design inside Visual Studio too.** A Visual Studio 2022/2026 extension opens `.radform.json` files in a
  `[Design]` tab with its own Toolbox and Properties windows, much like the WinForms designer. Your project's own
  controls are at the top of the Toolbox, and they update each time you build.
- **Light and dark themes,** in the designer and in your apps.

## Controls

About 40 components, each documented in the [control reference](docs/controls.md):

| Group | Components |
|---|---|
| Standard | Button, Label, Edit, Memo, CheckBox, RadioButton, GroupBox, Panel, Image, PaintBox |
| Values | SpinEdit, TrackBar, ProgressBar, ScrollBar, ColorBox |
| Lists | ListBox, ComboBox, CheckListBox |
| Data views | StringGrid, DrawGrid, ListView, TreeView |
| Containers | PageControl, TabSheet, ScrollBox, ScrollView, Splitter, StatusBar, ToolBar |
| Menus | MainMenu, PopupMenu |
| Non-visual | Timer, ImageList, OpenDialog, SaveDialog, FolderDialog |

The list and data controls share one scrolling and selection base, built to handle large data:

- **StringGrid and DrawGrid** have fixed header rows and columns, cell, range and row selection, and in-place
  editing. They also support column resizing, sorting on a header click, Ctrl+C copy as tab-separated text, and a
  virtual mode (`onGetCell`) that scrolls 100 000 rows while asking only for the cells in view.
- **ListView** shows a report view with a pinned header, sorting, multi-select, checkboxes, icons from an ImageList and
  type-ahead. Its virtual mode (`onGetItem`, `onFindItem`) needs no per-row storage.
- **TreeView** loads children lazily (`onExpanding` with `HasChildren`) and has checkboxes, icons, multi-select,
  in-place rename with veto, and Windows tree keys.
- **ListBox** can select several items, carry a tag per item, and give each row its own height (`style: variable`).
  **ComboBox** has an editable style with autocomplete. **CheckListBox** adds a check box per item, with an optional
  grayed state.

Grid and ListView columns, and TreeView nodes, are saved in the form file. The designer edits them through
**Columns** and **Nodes** editors opened from the Object Inspector.

## Screenshots

### Visual Studio integration

The same designer inside Visual Studio 2022, docked like the WinForms designer. Double-clicking a control writes its
event handler in the C# editor.

![The RadSharp designer in a Visual Studio [Design] tab with the RadSharp Toolbox and Properties windows](docs/images/visual-studio-dark.png)

### Built with RadSharp

The samples in [`samples/`](samples), shown in the dark theme:

![The tile editor sample with a painted island map](docs/images/tile-editor-dark.png)

<p>
  <img src="docs/images/text-editor-dark.png" alt="The text editor sample" width="62%">
  <img src="docs/images/calculator-dark.png" alt="The calculator sample showing 1284 × 27 = 34668" width="30%">
</p>

The default light theme:

<p>
  <img src="docs/images/hello-light.png" alt="The Hello sample in the light theme" width="48%">
  <img src="docs/images/calculator-light.png" alt="The calculator sample in the light theme" width="24%">
</p>

### Toolbox

Every component has a vector icon that stays sharp at any scale and follows the theme.

![Toolbox icons for all components, in the light and dark themes](docs/images/toolbox-icons.png)

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), 10.0.404 or later (`global.json` asks for it; older 10.0 SDKs bring a NativeAOT compiler package that NuGet flags as vulnerable, which fails the build).

```sh
dotnet run --project src/RadSharp.Designer    # the designer
dotnet run --project samples/TileEditor       # a sample app
dotnet run --project samples/ControlsDemo     # ListView, TreeView, list controls and DrawGrid
dotnet run --project samples/GridDemo         # a 100 000-row virtual StringGrid
dotnet run --project samples/WindowsDemo      # monitors, per-monitor scale, placement, window styles, Alt keys
dotnet test                                   # the test suite
```

To start your own app, follow [docs/getting-started.md](docs/getting-started.md). Build the local packages with
`./pack.ps1` or `./pack.sh` (one version for every package), not `dotnet pack` per project. The guide packs the
[`templates/`](templates) project, installs it, and creates a new app with `dotnet new radsharp-app -n MyApp`.

## The Visual Studio designer

The extension lives in [`vs/`](vs). It is a thin .NET Framework 4.8 layer, which is what Visual Studio runs on. The
designer itself runs in a separate .NET 10 process (`src/RadSharp.DesignHost`) and streams its picture to Visual Studio,
as Avalonia's previewer does.

1. Build the `.vsix` with Visual Studio 2022's MSBuild (`dotnet build` can't produce one):
   ```
   cd vs
   "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" -restore RadSharp.VisualStudio.slnx -p:Configuration=Release
   ```
2. Close Visual Studio, then double-click
   `vs\RadSharp.VisualStudio\bin\Release\net48\RadSharp.VisualStudio.vsix` and pick Visual Studio 2022 or 2026.
3. Open any `.radform.json` file. If it opens as plain JSON, run `devenv /updateconfiguration` once and restart
   Visual Studio.

The design, protocol and test checklist are in [docs/vs-designer-plan.md](docs/vs-designer-plan.md).

## Repository layout

| Path | What's there |
|---|---|
| `src/RadSharp.Core` | Controls, layout, rendering, text, form reader/writer, component metadata |
| `src/RadSharp.Generators` | Source generators for component metadata and form code-behind |
| `src/RadSharp.Platform.Sdl` | The SDL3 host: windows, input, IME, clipboard, dialogs, modal loops |
| `src/RadSharp.Design` | The design surface, document model, palette and toolbox icons, and the menu, tab-order, columns and nodes editors |
| `src/RadSharp.Design.Code` | Roslyn integration: handler stubs, renames, live run (F9), builds |
| `src/RadSharp.Designer` | The standalone designer app (itself a RadSharp form) |
| `src/RadSharp.DesignHost` | The headless design host behind the Visual Studio designer, and its pipe protocol |
| `vs/` | The Visual Studio extension and its WPF client |
| `samples/` | Hello, Calculator, TextEditor, SettingsDialog, TileEditor, GridDemo, ControlsDemo and Robustness |
| `spec/` | Conformance vectors for layout and form files |
| `templates/` | The `dotnet new radsharp-app` template |

## Status

**Preview: 0.1.0-preview.1.** Download the designer and the Visual Studio extension from the
[releases page](https://github.com/SantasLair/RadSharp/releases); the packages are on nuget.org
(`dotnet new install RadSharp.Templates::0.1.0-preview.1`). APIs may change before 1.0; see
[docs/known-limits.md](docs/known-limits.md) for what isn't there yet and [CHANGELOG.md](CHANGELOG.md) for releases.

Milestones M0–M16 of the [roadmap](RadSharp-roadmap.md) and [gap plan](docs/gap-plan.md) are implemented as cut for
1.0. M17 (ship it) is under way: packaging is done, and the reference app (`refapp/Tally`), the 1.0 acceptance test,
is being finished. RadSharp is developed and tested
on Windows; Linux runs the full suite in CI, and macOS is checked on a Mac. Known gaps and the plan to close them are in
[docs/gap-analysis.md](docs/gap-analysis.md), [docs/gap-plan.md](docs/gap-plan.md) and
[docs/designer-gap-analysis.md](docs/designer-gap-analysis.md).

## Documentation

- [Getting started](docs/getting-started.md)
- [Known limits](docs/known-limits.md)
- [Control reference](docs/controls.md)
- [Form file and layout spec](docs/spec.md)
- [Distribution and NativeAOT publishing](docs/distribution.md)
- [Visual Studio designer](docs/vs-designer-plan.md)
- [Releasing RadSharp](docs/releasing.md)
- [Design background](future-framework-brainstorm.md)

## License

RadSharp is released under the [MIT License](LICENSE).

The bundled Inter font is under the SIL Open Font License 1.1
([`src/RadSharp.Core/Fonts/Inter-LICENSE.txt`](src/RadSharp.Core/Fonts/Inter-LICENSE.txt)).
