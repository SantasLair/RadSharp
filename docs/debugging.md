# Debugging a RadSharp app

A RadSharp app is an ordinary .NET project, so you debug it with your IDE's debugger. The designer is for layout; the
IDE is for breakpoints.

## F5 in your IDE (breakpoints, stepping, watches)

File › New Application writes everything F5 needs (`dotnet new radsharp-app` writes the `.vscode` files only, no
`Properties/launchSettings.json`; Visual Studio then runs the project's default profile):

- `.vscode/launch.json` and `.vscode/tasks.json` for VS Code (C# / C# Dev Kit extension, `coreclr` debugger). F5 runs
  the `build` task, then launches `bin/Debug/net10.0/<App>.dll`.
- `Properties/launchSettings.json` for Visual Studio and Rider: F5 starts the project (`commandName: Project`).

Open the project folder (or the `.sln`) in the IDE, set a breakpoint in a handler in `MainForm.cs`, press F5. Use
**Project › Open in IDE** (Ctrl+Shift+E) in the designer, or double-click a control or event to jump to its handler.
Debug builds run on the JIT, not NativeAOT, so the full debugger works; NativeAOT is only for `dotnet publish`.

## The designer: layout, not logic

The designer shows the form file (`*.radform.json`) on a design surface. Use it to place and size controls, set
properties and wire events. It never runs your handlers, so breakpoints are not hit there.

Controls your project defines (and those of the libraries it references) are on the palette once the project compiles:
the designer compiles it in the background when it opens, when a source file changes, and on **Project › Refresh
Project Controls**. Their constructors, published property setters, layout and painting then run in the designer
process, with `DesignMode` true (from the constructor on): check it to skip what needs the running app, such as a
database or a file. A control whose constructor throws there stays a placeholder (the exception is in the Output pane);
while the project doesn't compile, all of its controls are placeholders, which keep their properties and save unchanged.

## F9 (Run) in the designer

F9 compiles the project with Roslyn into a collectible assembly *inside the designer process* and shows the main form.
It is a quick look at the real behaviour with the designer's unsaved edits, but:

- no debugger is attached to it, so breakpoints in your code-behind are ignored;
- the compile errors appear in the designer's Output pane, not in your IDE;
- your own code runs in the designer's process: an unhandled exception or an endless loop affects the designer.

F9 resolves the project's packages and references with MSBuild (a restore the first time, which can take minutes) and
compiles in the background: the status bar and the Output pane show progress, and the designer stays usable. Pressing
F9 again before it finishes starts over. If MSBuild can't resolve the project, F9 compiles against the designer's own
assemblies (warning RAD9001) and remembers the failure until the project file, its settings files or a project
reference change; Project › Refresh Project Controls makes the next F9 try again.

Use F9 to check a layout or a flow quickly, and F5 in the IDE when you need to debug.

## Attaching to a running app

Start the app normally (`dotnet run`, or the built `.exe`), then attach:

- **Visual Studio:** Debug › Attach to Process, pick your app's process, code type *Managed (.NET Core)*.
- **VS Code:** add an attach configuration to `launch.json` and run it. `${command:pickProcess}` lists the processes:

  ```json
  {
    "name": "Attach to RadSharp app",
    "type": "coreclr",
    "request": "attach",
    "processId": "${command:pickProcess}"
  }
  ```

## Current limits

- A **NativeAOT-published** executable has no managed debugger: debug the JIT build (`dotnet build`, F5) and use native
  debuggers (WinDbg, lldb, gdb) on the published one.
- F9 live run cannot be debugged (see above): the code is compiled in memory in the designer process.
- The launch files are written once, when the project is created. If you rename the project, edit the `program` path in
  `.vscode/launch.json` and the profile name in `Properties/launchSettings.json`.
- Renaming a form (Project › Rename Form) does not touch these files, since they name the project, not a form.
