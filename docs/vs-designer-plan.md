# Visual Studio designer: plan

Goal: open a `*.radform.json` file in Visual Studio and get the RadSharp form designer in the document tab, like the
WinForms designer. That means design surface, component palette, Object Inspector, Ctrl+S / dirty asterisk / undo,
and double-click on a control or event that jumps to the handler in VS's own C# editor.

## Shape

Avalonia-style: the real renderer runs out of process and VS only shows pixels.

```
 devenv.exe (.NET Framework 4.8)                     RadSharpDesignHost.exe (.NET 10)
 ┌──────────────────────────────────┐   named pipe   ┌─────────────────────────────────────┐
 │ RadSharp.VisualStudio (VSIX)     │ ◄────────────► │ HeadlessPlatform : IPlatformServices │
 │  editor factory, doc data, cmds  │   frames  ◄──  │ DesignDocument + DesignSurface form  │
 │ RadSharp.VisualStudio.Client     │   input   ──►  │ PropertyGrid form, palette form      │
 │  WPF pane: WriteableBitmap+input │                │ popups/modals = more windows         │
 └──────────────────────────────────┘                └─────────────────────────────────────┘
              both reference RadSharp.DesignHost.Protocol (netstandard2.0)
```

Why it works with little change to RadSharp (from the code survey):
- `DesignSurface` is a plain `Control` and `DesignDocument` is UI-free (src/RadSharp.Design).
- Forms already render headlessly: `FormRenderer.Render(form, SKCanvas, clip)` with dirty rects from
  `Form.TryTakeDirtyRect` (the same path SdlHost uses and golden tests rely on).
- Input is platform-neutral: `Form.ProcessMouse*/ProcessKey*/ProcessTextInput/ProcessTick/Resize`.
- The platform seam is one interface, `IPlatformServices`, plus `Clipboard.Current`.
- `PropertyGrid` lives in Core and takes `SetTargets(...)`, so it can be its own window.
- `CodeIntegration.EnsureHandler` returns `(source, line)`. We send that line to VS instead of launching an editor.

The VS side never references RadSharp. It is about 1–2k lines of net48 WPF: show bitmaps, forward input, map VS
commands.

## Projects

| Project | TFM | Role |
|---|---|---|
| `src/RadSharp.DesignHost.Protocol` | netstandard2.0 | Message types and length-prefixed binary framing over a `Stream`. No dependencies. |
| `src/RadSharp.DesignHost` | net10.0 exe `RadSharpDesignHost` | Headless host: pipe server, UI loop, window registry, render and diff, input dispatch. |
| `tests/RadSharp.DesignHost.Tests` | net10.0 | Protocol round-trips; host driven over in-memory duplex streams. |
| `vs/RadSharp.VisualStudio.Client` | net48 WPF lib | Starts the host, pipe client, `RemoteWindowView` (WPF element that shows a remote window and forwards input), popup/modal windows. |
| `vs/RadSharp.DesignHost.Harness` | net48 WPF exe | Opens a form file in a plain WPF window using the Client. Lets us test and screenshot without VS. |
| `vs/RadSharp.VisualStudio` | net48 VSIX | Editor factory, document pane, persistence, commands, tool windows, navigation to code. |

`vs/` gets its own `Directory.Build.props` (net48, not the repo's net10 one) and its own `vs/RadSharp.VisualStudio.slnx`,
built with VS's MSBuild. The main `RadSharp.slnx` and `dotnet test` stay as they are, except that Protocol, Host and
the host tests join the main solution.

### Building the VSIX (checked on this machine, without the VS extension workload)

An SDK-style net48 project with `Microsoft.VisualStudio.SDK` 17.14.40265 (`ExcludeAssets="runtime"`) and
`Microsoft.VSSDK.BuildTools` 17.14.2094 (`PrivateAssets="all"`). Use explicit SDK imports so the VSSDK targets come
last:

```xml
<Project>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <!-- GeneratePkgDefFile, CreateVsixContainer, IncludeAssemblyInVSIXContainer = true; DeployExtension = false -->
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
  <Import Project="$(VSToolsPath)\VSSDK\Microsoft.VsSDK.targets" />
</Project>
```

Build it with `"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" -restore`.
`dotnet build` cannot produce a `.vsix`. Under `dotnet build` / `dotnet test` the project only compiles: no pkgdef, no
.vsix and no host publish. `vs/global.json` pins SDK 9.0.3xx, because VS 2022's MSBuild 17.14 can't load the root's
10.0.4xx. The host publish runs `dotnet publish` in `src/RadSharp.DesignHost`, so the root `global.json` applies there.

## Protocol (v1)

Frame: `uint32 length (LE) | byte type | payload` using BinaryWriter encoding. Strings are length-prefixed UTF-8.
Ids are `int`. Coordinates are device pixels, and the client sends its DPI scale.

Client → host
- `Hello(protocolVersion, clientName)` → `HelloAck(protocolVersion, hostVersion)`
- `OpenDocument(docId, path)`, `CloseDocument(docId)`, `ActivateDocument(docId)`, `ReloadDocument(docId)` (file changed on disk)
- `SaveDocument(docId, path)` → `DocumentSaved(docId, ok, error)`
- `AttachWindow(windowId)` binds a client view to a host window that the host announced
- `ResizeWindow(windowId, w, h, scale)`, `FocusWindow(windowId, focused)`
- `MouseMove/MouseDown/MouseUp/MouseWheel(windowId, x, y, button, mods, clicks, dx, dy)`
- `KeyDown/KeyUp(windowId, key, mods, repeat)`, where `key` is the RadSharp `Key` name as a string, so the protocol
  needs no copy of the enum. `TextInput(windowId, text)`.
- `Command(docId, name)`: `Undo, Redo, Cut, Copy, Paste, Delete, SelectAll`, plus the alignment commands later
- `CloseWindow(windowId)`: user closed a modal or popup

Host → client
- `WindowOpened(windowId, docId, role, ownerWindowId, x, y, w, h, title, modal)`, where `role` is one of
  `Surface | Inspector | Palette | Structure | Popup | Dialog`. Panes come from `OpenDocument`. Popups and dialogs come
  from `IPlatformServices.ShowForm/ShowModal/RunModal`.
- `WindowClosed(windowId)`, `WindowMoved(windowId, x, y, w, h)`
- `Frame(windowId, x, y, w, h, bgraPremul bytes)`: the dirty rect only. The first frame after a resize is full.
- `Cursor(windowId, cursorName)`
- `DocumentState(docId, dirty, canUndo, canRedo, hasSelection)`
- `NavigateToCode(docId, file, line)`
- `Error(docId, message)`, `Log(level, message)`

The implemented message list and type bytes are in `src/RadSharp.DesignHost.Protocol/Messages.cs`. That file is
authoritative where it differs from the list above. For example, `Error` carries `Text`, and `Log.Level` is
0 debug, 1 info, 2 warning, 3 error.

**v2: code edits** (`ProtocolInfo.Version = 2`). The host never writes code-behind files, because the IDE may hold
unsaved edits. On a double-click or an inspector event, the host assigns the handler name on the document and sends
`CodeRequest(requestId, docId, file)` (0x8C). The client answers `CodeResponse(requestId, found, text)` (0x19): with
`found` the text is the editor's current buffer, otherwise the host reads the file from disk. The host runs
`EnsureHandler` on that text. If the text changed it sends `ApplyCodeEdit(docId, file, text, line)` (0x8D): the full
new file text and the 1-based caret line, which the client applies and navigates to. If the handler already existed it
sends `NavigateToCode` as before. The host does not block the UI loop while waiting. With no answer in 10 s it logs a
warning and falls back to the file on disk (still sending `ApplyCodeEdit`). Pending requests are dropped when the
document closes or the client disconnects.

**v3: edit safety** (`ProtocolInfo.Version = 3`). `ApplyCodeEdit` gains a trailing `BaseHash` string: the lowercase hex
SHA-256 of the UTF-8 text the edit was computed from (the `CodeResponse` buffer, or the file's text when the client had
no buffer). The client must compare `BaseHash` with the hash of its current buffer (or of the disk text when the file
is not open) and, on a mismatch, apply nothing and raise an error to the user instead: the buffer changed while the
host was working. The host no longer falls back to the disk when a `CodeRequest` goes unanswered for 10 s: it sends
`Error("code editor did not respond; try again")` and takes the handler name back off the form. It also takes the
handler name back, and sends `Error`, if the code file cannot be read or the handler cannot be added.

**Process contract.**
- **Startup:** the client runs `RadSharpDesignHost.exe --pipe <name> [--parent-pid <pid>]`. The host creates
  `NamedPipeServerStream(name, InOut, 1, Byte, Asynchronous)` and accepts one connection. The client connects with
  `NamedPipeClientStream(".", name, InOut, Asynchronous)`, retrying for up to 10 s. It then sends `Hello`, and the
  host replies `HelloAck`.
- **Shutdown:** the host exits when the pipe closes or the parent process dies.
- **Logging:** the host writes diagnostics to stderr, and the client logs them.

**Window flow.**
- `OpenDocument` makes the host announce three windows with `WindowOpened`: `Surface`, `Inspector` and `Palette`.
  Their size is 0×0 until the client sends `ResizeWindow`.
- The client sends `AttachWindow` and `ResizeWindow(w, h, scale)`. The host replies with a full `Frame`, then
  dirty-rect frames.
- The Inspector and Palette windows follow the active document. The host keeps one of each per document, so each VS
  tab has its own set and the client shows the active document's.
- Popups and dialogs get their own `WindowOpened`, with `OwnerWindowId`. `X` and `Y` are device pixels relative to
  the owner window's top-left.
- `Modal` dialogs are shown modally by the client. Closing one in the client sends `CloseWindow`.

**Cursors** are RadSharp `Cursor` enum names (src/RadSharp.Core/Input/Input.cs:32): `Default, IBeam, Hand, SizeWE,
SizeNS, SizeNWSE, SizeNESW, SizeAll, Wait, NotAllowed, Crosshair`.

**Keys** are RadSharp `Key` enum names (src/RadSharp.Core/Input). The client maps WPF `Key` values to those names and
drops keys with no mapping.

Host threading: a reader thread queues messages. One UI thread drains the queue, calls `ProcessTick` about every
16 ms, renders the dirty rect of each window, and sends frames. `PostToUiThread` enqueues. `RunModal` and
`ProcessEventsUntil` pump the same queue (nested loop). `ShowFileDialog` returns null (cancelled) in v1. The
clipboard bridge goes through the client later, and v1 uses the in-process clipboard.

## Phases and owners

| # | Work | Agent / model | Depends on |
|---|---|---|---|
| 1 | Protocol library + round-trip tests | general-purpose / sonnet | — |
| 2 | Headless host + tests over in-memory streams | general-purpose / opus | 1 |
| 3 | Client WPF lib + harness exe | general-purpose / opus | 1 (parallel with 2) |
| 4 | VSIX: editor factory, persistence, commands, navigation, tool windows | general-purpose / opus | 3 |
| 5 | Integration: harness against the real host, screenshot, fix | main session | 2, 3 |
| 6 | Review: `/code-review` on the branch | sonnet | all |

## Risks

- **`.json` editor registration.** VS picks editor factories by the last extension (`.json`). The factory registers
  for `.json` and returns `VS_E_UNSUPPORTEDFORMAT` for anything not ending in `.radform.json`, so other JSON keeps the
  normal editor. "Open With… → Source Code (Text) Editor" stays available for the raw JSON.
- **Keyboard in VS panes.** VS pre-translates keys into commands. The pane must claim keys while focused
  (`IVsWindowPane.TranslateAccelerator` / WPF `PreviewKeyDown`) and route Edit-menu commands as `Command` messages.
- **Custom controls from the user's project.** v1 shows built-in components only, as the standalone designer does today.
- **Runtime.** The host is framework-dependent .NET 10, shipped inside the VSIX. RadSharp users already have the SDK.
- **Verification.** Everything but the VSIX is tested headlessly or in the harness. The VSIX gets a build check plus
  a manual checklist in the experimental instance (`devenv /rootsuffix Exp`), because driving VS's UI can't be automated here.
- **Targets.** `InstallationTarget [17.0,19.0)`, amd64 and arm64, which covers VS 2022 (17.14) and VS 18.

## Status (2026-11-11)

All phases are built. These pass: main solution `dotnet test RadSharp.slnx -p:NuGetAudit=false` (84 host tests),
and `dotnet test vs/RadSharp.VisualStudio.slnx` (78 client tests and 86 VSIX tests).

**Checked live in the VS 2022 (17.14) experimental instance**, using the Release `.vsix` with synthetic mouse and
keyboard input:

- **Opening a form:** `MainForm.radform.json` opens as `[Design]` with the out-of-process surface. RadSharp Toolbox
  docks on the left and RadSharp Properties on the right; both follow the active designer and show "No RadSharp form
  is active" on a code tab. The dark VS theme is applied.
- **Selection and keyboard:** clicking a control selects it and the Properties window follows. Arrow keys nudge the
  control, so keys get past VS's accelerator translation, and the tab shows `*`.
- **Undo:** Ctrl+Z reaches the designer through Edit > Undo, one step per nudge, and Redo becomes enabled.
- **Event handlers:** double-clicking OK opens `MainForm.cs` in VS's C# editor with `btnOk_Click` inserted and the
  caret in its body (protocol v3 round trip).
- **Save:** Ctrl+S on the designer clears `*` and writes `"onClick": "btnOk_Click"` to the form file.

**Fixed after the live run:** the designed form took the designer's dark theme, so its labels were unreadable on
explicitly white panels. The designed form now always uses `Theme.Default` (`HostDocument.UseDesignedTheme`, with a
test). Re-checked live with a rebuilt `.vsix`.

**Typing:** checked live in the rebuilt `.vsix`. Ctrl+A then "Okay" + Enter in RadSharp Properties renamed the
button's caption on the surface. Ctrl+A stayed in the text field and did not become a designer Select All.

**Second live pass (same day, rebuilt Release `.vsix`):**

- **Toolbox:** typing "butt" in the filter narrows it to ToolButton, Button and RadioButton. Picking Button, then
  clicking the surface, adds `Button1` and marks the tab dirty. A new Button has no caption, so it is only an outline,
  as in the standalone designer.
- **Reload on an external change:** a clean tab reloads silently. A dirty tab asks "Reload it and lose your changes?",
  and Yes reloads and clears `*`.
- **Save As:** the dialog filters `*.radform.json` and writes the new file. VS then keeps the tab and document under
  the old name while later saves go to the new file. VS's own C# editor does exactly the same for a project item, so
  this is left as VS behaviour.
- **Save Copy As:** VS 2022 reports `File.SaveCopyofSelectedItemsAs` as not available even for C# files, so it can't
  be reached. The code path is covered by unit tests only.
- **Host crash:** killing `RadSharpDesignHost.exe` shows the "Restart designer host" banner, and both tool windows say
  the host isn't running. Restart starts a new host, reloads the form, and reattaches the tool windows (the Toolbox
  filter text is kept).
- **Dialogs:** the surface's right-click menu now has Bring to Front, Send to Back, Tab Order… and Menu Editor…, as in
  the standalone designer; Menu Editor is greyed out when the form has no menu. Tab Order opens as a VS-owned modal
  window centred over the designer. Moving a row and clicking OK marks the tab dirty, and Ctrl+S writes the new
  `tabOrder` values.
- **Missing host:** with `RADSHARP_DESIGNHOST` pointing at a missing file, the tab says the host wasn't found, shows the
  path, and points to Open With → Source Code (Text) Editor.

**VS 2026 (18.10):** the same Release `.vsix` installs into VS 2026's experimental instance unchanged (VSIXInstaller
from `...\Microsoft Visual Studio\18\Community\Common7\IDE`, then `/updateconfiguration`). The `[Design]` tab and both
tool windows open. Click-select, arrow nudge (`*` appears), Ctrl+Z back to clean, and double-click → `btnOk_Click` in
the C# editor with the caret in the body all work.

**Not yet checked live:** switching the VS colour theme while running, the dirty-tab close prompt, and a DPI change
between monitors.

## Manual test checklist

Run these in the experimental instance only, never in the main VS hive.

1. Build: from `vs/`, run `"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" -restore RadSharp.VisualStudio.slnx`.
   The build has no warnings, and the log says `RadSharp design host: N files added`. `dotnet test RadSharp.VisualStudio.slnx`
   passes.
2. Install: `"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /rootSuffix:Exp vs\RadSharp.VisualStudio\bin\Debug\net48\RadSharp.VisualStudio.vsix`.
   To uninstall, run the same tool with `/rootSuffix:Exp /u:RadSharp.VisualStudio.cc12a64b-3c38-44bd-9d86-2bf42bfc792b`.
   **Then run `devenv /rootsuffix Exp /updateconfiguration` once.** Without it, VS kept the old configuration after a
   command-line install: the RadSharp commands were missing, and `.radform.json` opened in the JSON editor.
   Do not pass a file after a bare `/log`, because VS takes it as the log path and overwrites the file.
3. Run `devenv /rootsuffix Exp` and open a sample, for example `samples/Hello`, which has `MainForm.radform.json` next to its code-behind. The steps below say `Form1` for the form.
4. Open: open `Form1.radform.json`.
   - The tab reads `Form1.radform.json [Design]`. It says "Starting the RadSharp designer…", then shows the form.
   - The Output pane "RadSharp Designer" logs "Design host connected".
   - The "RadSharp Toolbox" opens, tabbed with the VS Toolbox, and "RadSharp Properties" opens, tabbed with Properties.
     Both show this form's palette and inspector.
   - View → Other Windows lists both windows.
   - Any other `.json` file opens in the normal JSON editor.
5. Open With: open the Open With dialog on `Form1.radform.json`.
   - "RadSharp Form Designer" is the default. "Source Code (Text) Editor" opens the raw JSON.
   - Find in Files results for the file open as text.
6. Edit and save:
   - Drop a control from the RadSharp Toolbox and move it. An asterisk appears on the tab.
   - Ctrl+S clears the asterisk and writes the file.
   - Save All works, and so does File → Save As (the dialog filters `*.radform.json`; the tab follows the new name).
   - Save Copy As: with no changes, it copies the file. With changes, it saves the copy, then saves the original too,
     and the Output pane says so.
7. Close: close a dirty tab. VS asks whether to save. Yes saves, No discards, and Cancel keeps the tab.
8. External change:
   - Edit the file in Notepad. A clean tab reloads silently.
   - A dirty tab asks before reloading.
   - VS's own saves don't trigger either.
9. Edit commands in the designer:
   - With a control selected, Ctrl+C, Ctrl+V, Ctrl+X, Del, Ctrl+A, Ctrl+Z and Ctrl+Y act on controls.
   - The Edit menu does the same, and its items are greyed out when unavailable (Undo with nothing to undo, Cut with no
     selection).
10. Keys on the surface:
    - Arrow keys (with or without Shift or Ctrl) nudge or resize the selection.
    - Tab, Escape, Enter and letter keys reach the host and do what they do in the standalone designer.
    - VS keeps Ctrl+S, Ctrl+Shift+S, Ctrl+Tab, Ctrl+F4, F5, F7, F9, Ctrl+Q and Alt+letter menu access.
11. Keys in RadSharp Properties: edit a text property.
    - Typing, arrows, Home/End, Backspace, Del, Ctrl+A, Ctrl+C, Ctrl+V, Ctrl+X and Ctrl+Z edit the text.
    - These keys don't act on the designer's controls. Enter commits the edit.
    - VS's global keys still work.
12. Event handlers: double-click a button.
    - `Form1.cs` opens in the C# editor, the handler stub is inserted, and the caret sits inside its body.
    - Ctrl+Z in the C# editor removes the stub in one step. The file is modified but not saved.
    - With `Form1.cs` already open with unsaved edits, the stub is inserted into the buffer and those edits are kept.
    - With `Form1.cs` closed, the file is written with its encoding kept (BOM or not) and then opened.
    - Double-click an existing handler: the editor jumps to it with no edit.
13. Two forms: open two forms and switch tabs. The Toolbox and Properties follow the active form. Switch to a C# file:
    both show "No RadSharp form is active".
14. Host crash: kill `RadSharpDesignHost.exe` in Task Manager.
    - Each designer tab shows a banner with "Restart designer host", and the Output pane logs the reason.
    - Restart reloads the forms from disk, and the tool windows reattach.
15. Theme: switch Tools → Options → Environment → Color Theme between Light and Dark.
    - With no unsaved forms, the host restarts in the matching theme.
    - With unsaved forms, the Output pane asks you to restart the host yourself.
    - A RadSharp app started with F5 doesn't inherit `RADSHARP_THEME`.
16. Missing host: set `RADSHARP_DESIGNHOST` to a missing path and restart the Exp instance. Opening a form shows the
    reason and the path in the tab, and Open With → text editor still works. Pointing the variable at a fresh
    `src/RadSharp.DesignHost/bin/.../RadSharpDesignHost.exe` uses that host instead.
17. Dialogs and popups: open a property drop-down in RadSharp Properties and a dialog-style editor, if any.
    - The drop-down appears at the right place.
    - The dialog is owned by VS and centred over it.