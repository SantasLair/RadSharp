# Getting started

RADSharp (working title) is a Delphi-style RAD tool for C#: design a form visually, double-click to write
a handler, press F9 to run it, press Build to get a native executable for Windows, macOS or Linux.

## What you need

- The **.NET 10 SDK** (`dotnet --list-sdks` should list a 10.x version).
- For native builds (**Build**): the platform linker NativeAOT uses — on Windows the *Desktop development with
  C++* workload of Visual Studio or its Build Tools; on Linux `clang` and `zlib1g-dev`; on macOS the Xcode
  command-line tools.
- An editor for the code-behind: VS Code (`code` on the PATH) is used by default; set `RADSHARP_EDITOR` to
  another command, e.g. `rider --line {line} "{file}"`.

## The designer

Run the designer (`src/RadSharp.Designer`, or the published app):

1. **File › New project…** and pick an empty folder. You get a `.csproj`, `Program.cs`, `MainForm.radform.json`
   (the designed form), `MainForm.cs` (your code) and a GitHub Actions workflow for cross-platform builds.
2. Pick a control in the **Components** palette and click (or drag a rectangle) on the form.
3. Set properties in the **Object Inspector**; rename the control in the **Name** box.
4. **Double-click** the control (or an event on the **Events** tab). The designer writes the handler into
   `MainForm.cs` and opens your editor on it:

   ```csharp
   void Button1_Click(Control sender)
   {
       lblMessage.Caption = "Hello!";
   }
   ```

5. **Run › Run (F9)** compiles the project in memory — including unsaved changes to the form — and opens the
   form with your handlers live. Close it to go back to designing.
6. **Run › Build (Ctrl+F9)** publishes a native executable for the current OS (NativeAOT) and shows where it went.
   Push the project to GitHub and the included workflow builds Windows, macOS and Linux versions.

Other designer tools: **Component › Menu editor** (for MainMenu/PopupMenu), **Tab order**, bring to
front/send to back, copy/paste of controls, undo/redo, grid and edge snapping (hold **Alt** to place freely),
arrow keys to nudge (**Shift** resizes, **Ctrl** moves by the grid).

## Without the designer

Until the packages are on nuget.org, build them with `dotnet pack` (they land in `artifacts/packages`) and
install the template from there:

```sh
dotnet new install artifacts/packages/RadSharp.Templates.0.1.0-dev.nupkg
dotnet new radsharp-app -n MyApp
cd MyApp
dotnet run            # or F5 in VS Code
dotnet publish -c Release   # a native executable for this OS
```

Form files are plain JSON (see [spec.md](spec.md)); edit them in any editor. The form generator turns
`X.radform.json` into the other half of `partial class X`: a typed field per named control and
`InitializeComponent()`. Handlers are bound at compile time — a handler named in the form but missing from the
class is compile error **RAD1001**, reported at the right line of the form file.

## Concepts, for Delphi developers

| Delphi | RADSharp |
|---|---|
| `.dfm` + `.pas` | `X.radform.json` + `X.cs` (partial class; the generated half holds the fields) |
| `published` | `[Published]` on a property or event |
| `Align`, `Anchors` | same rules (spec, section B) |
| `TNotifyEvent(Sender)` | `void Handler(Control sender)`; `(object sender)` to share between buttons and menu items |
| `ShowModal = mrOk` | `form.ShowModal() == ModalResult.Ok`; `Button.ModalResult` closes the dialog |
| `Application.Run` | `Application.Initialize(); Application.Run(new MainForm());` (RadSharp.Platform.Sdl) |
| `TPaintBox.OnPaint` | `PaintBox.PaintContent` with a `Canvas` |

Your own controls: derive from `Control` (or `Component` for non-visual ones), mark properties `[Published]`,
override `Paint(Canvas)` and the `On…` input methods. The metadata generator registers the class, so forms can
use it and the inspector can edit it.

See also: [the control reference](controls.md), [distribution notes](distribution.md), [the spec](spec.md).
