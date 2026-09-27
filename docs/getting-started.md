# Getting started

RADSharp (working title) is a Delphi-style RAD tool for C#: design a form visually, double-click to write
a handler, press F9 to run it, press Build to get a native executable for Windows, macOS or Linux.

## What you need

- The **.NET 10 SDK** (`dotnet --list-sdks` should list a 10.x version).
- For native builds (**Build**): the platform linker NativeAOT uses — on Windows the *Desktop development with
  C++* workload of Visual Studio or its Build Tools; on Linux `clang` and `zlib1g-dev`; on macOS the Xcode
  command-line tools. On Windows, run from a *Developer Command Prompt/PowerShell*, or make sure
  `vswhere.exe` (installed with Visual Studio under `.../Installer/`) is on `PATH` — otherwise NativeAOT's
  link step fails with "'vswhere.exe' is not recognized" even though the workload is installed.
- An editor for the code-behind: VS Code (`code` on the PATH) is used by default; set `RADSHARP_EDITOR` to
  another command, e.g. `rider --line {line} "{file}"`.

## The designer

Run the designer (`dotnet run --project src/RadSharp.Designer`, or the published app). As in Delphi and Lazarus,
you always work in a **project**: the designer starts on a welcome page with **New Application…**,
**Open Project…** and your recent projects (also under **File › Reopen**). You can pass a `.csproj` on the command line.

1. **New Application…** asks for a name and a location and creates the project: `.csproj`, `.sln`,
   `Program.cs`, `MainForm.radform.json` (the designed form), `MainForm.cs` (your code), a `.gitignore` and a
   GitHub Actions workflow for cross-platform builds. When the designer runs from the RADSharp repository it finds
   the local packages in `artifacts/packages` (run `dotnet pack` for `src/RadSharp.Core` and
   `src/RadSharp.Platform.Sdl` first); otherwise projects restore RADSharp from nuget.org.
2. Each open form has a **tab**. **File › New Form** (Ctrl+N) adds `FormN.radform.json` + `FormN.cs`; the
   **Project** tab of the inspector lists every form (double-click to open one). **Project › Set as Main Form**
   changes which form `Program.cs` starts with.
3. Pick a control in the **Components** palette and click (or drag a rectangle) on the form. Set properties in
   the **Object Inspector** and rename the control in the **Name** box (its field and handlers follow in the code).
4. **Double-click** the control (or an event on the **Events** tab): the designer writes the handler into the
   form's `.cs` file and opens your IDE there. **F12** opens the form's code; **Project › Open in IDE** opens the
   whole project (the folder in VS Code, the `.sln` in Visual Studio or Rider).

   ```csharp
   void Button1_Click(Control sender)
   {
       lblMessage.Caption = "Hello!";
   }
   ```

5. **Run › Run (F9)** compiles the application in memory — including unsaved changes to every open form — and
   starts it from its main form, handlers live. Close it to go back to designing.
6. **Run › Build (Ctrl+F9)** saves all forms and publishes a native executable for the current OS (NativeAOT).
   Push the project to GitHub and the included workflow builds Windows, macOS and Linux versions.

**Tools › Options** chooses the code editor, Unity style: Visual Studio Code, Visual Studio or JetBrains Rider (the
designer detects which are installed), or a custom command with `{file}`, `{line}`, `{folder}` and `{solution}`.
VS Code and Rider open handlers at their line; Visual Studio opens the file (it can't take a line number from the
command line). Debug, refactor and build the code in your IDE as usual — the project is an ordinary .NET project.

Other designer tools: **Component › Menu Editor** (for MainMenu/PopupMenu), **Tab Order**, bring to
front/send to back, copy/paste of controls, undo/redo per form, grid and edge snapping (hold **Alt** to place
freely), arrow keys to nudge (**Shift** resizes, **Ctrl** moves by the grid).

## Without the designer

Until the packages are on nuget.org, build them with `dotnet pack` (they land in `artifacts/packages`) and
install the template from there:

```sh
dotnet new install artifacts/packages/RadSharp.Templates.*.nupkg
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
