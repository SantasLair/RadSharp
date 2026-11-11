# Plan: from form designer to a full IDE

> Written 2026-11-11 from a read of `src/RadSharp.Designer/`, `src/RadSharp.Design/`, `src/RadSharp.Design.Code/`,
> `src/RadSharp.Core/Text/TextEditor.cs`, `src/RadSharp.Core/Controls/EditControls.cs`,
> [designer-gap-analysis.md](designer-gap-analysis.md), [gap-plan.md](gap-plan.md) and
> [vs-designer-plan.md](vs-designer-plan.md). Same conventions as `gap-plan.md`: milestones ordered by dependency,
> not date; S ≈ days, M ≈ 1–2 weeks, L ≈ several weeks; every milestone has a **done when** that a test can check.
> Milestones are numbered **I1–I9** so they don't clash with the M-numbers in `gap-plan.md`.

## Goal

Today the designer covers the form half of the Delphi loop well: placing controls, editing properties,
double-clicking to create a handler, and F9 to run. For all code it sends you to VS Code, Rider or Visual Studio. The
target is an IDE where a RADSharp developer can **create a project, design forms, write the code, build, run, debug
and fix errors without leaving it**. It should also be pleasant enough that they *prefer* to stay: quick to start,
keyboard-driven, with errors visible as you type.

This reverses one call in [designer-gap-analysis.md §4](designer-gap-analysis.md), which treated the missing
embedded code editor as a deliberate choice rather than a gap. External editors stay supported (Tools › Options,
"Open in IDE"), but they become optional.

**"Functional" means** the work in a typical session never needs another tool. That covers editing C# with
highlighting, completion, live errors and go-to-definition; finding things; building and running; stopping at a
breakpoint; and managing files and packages.

**"Enjoyable" means** the feel targets in I9: no visible lag while typing, fast switching between form and code,
a command palette, a restored session, and no dead ends where the only way forward is "open this in another
program".

### Non-goals (for this plan)

- A general-purpose editor for other languages. Code editing is C#, plus JSON for form files.
- Our own debugger engine. We drive an existing one (I6).
- Full Visual Studio parity: profilers, test explorer UI, designer-integrated git history, and so on. Git gets a
  minimal treatment (I8); the rest is post-plan.
- NativeAOT for the IDE itself. The designer already runs on the JIT because it hosts Roslyn
  (`RadSharp.Designer.csproj`), and the Roslyn features layer (MEF-composed) makes that permanent. The IDE ships
  framework-dependent or self-contained JIT. **Apps built with it** still publish NativeAOT.

## Coexistence with the Visual Studio designer work

The `vs-designer` branch ([vs-designer-plan.md](vs-designer-plan.md)) is being built at the same time. It hosts
`DesignDocument` + `DesignSurface` headlessly inside Visual Studio. Both tracks share `RadSharp.Design`, so the rules
below keep them from colliding.

**Ownership**

| Area | Owner | Rule for the other track |
|---|---|---|
| `src/RadSharp.DesignHost.Protocol/`, `src/RadSharp.DesignHost/`, `tests/RadSharp.DesignHost.Tests/`, `vs/` | VS track | IDE track doesn't touch them |
| `src/RadSharp.Core/Form.cs`, `src/RadSharp.Platform.Sdl/SdlHost.cs` | VS track while its edits are in flight | IDE track makes no changes until the VS edits are merged to `main`; after that, additive changes only, in separate commits |
| New projects `src/RadSharp.CodeEditor/`, `src/RadSharp.Ide.Language/`, `src/RadSharp.Ide.Debug/` and their tests | IDE track | VS track doesn't depend on them |
| `src/RadSharp.Designer/` (the shell) | IDE track | VS track doesn't use the shell, so this is free to restructure |
| `src/RadSharp.Design/` (`DesignDocument`, `DesignSurface`, `Editors.cs`) | **shared** | see the rules below |
| `src/RadSharp.Design.Code/` | **shared** (`CodeIntegration.EnsureHandler` feeds the VS host's `NavigateToCode`) | see the rules below |
| `RadSharp.slnx`, `Directory.Build.props` | shared | add lines only, never reorder or reformat; conflicts then resolve trivially |

**Rules for shared code**

1. **`RadSharp.Design` stays UI-shell-free and headless-safe.** The VS host drives it with no SDL window. Nothing in
   it may reference the IDE shell, dock panels, the code editor, or `Platform.Sdl`.
2. **Additive API only on shared types.** Add members; don't rename, remove or change the signatures of public members
   of `DesignDocument`, `DesignSurface`, `CodeIntegration`, `RadProject` or `LiveSession`. If a breaking change is
   genuinely needed, agree it with the VS track first and land it as its own commit on `main`.
3. **IDE work branches from `main`, not from `vs-designer`.** Use its own branch (`ide/<milestone>`), ideally in a
   separate worktree, so neither track's uncommitted edits leak into the other. Rebase onto `main` whenever the VS
   track merges.
4. **Code navigation goes through one seam.** Today `DesignerForm.Code.cs` turns `EnsureHandler`'s
   `(source, line)` into an external-editor launch, and the VS host turns it into a `NavigateToCode` message.
   The IDE adds `ICodeNavigator` (in `RadSharp.Design.Code`, new file, additive) with an `ExternalEditorNavigator`
   and an `EmbeddedEditorNavigator`. The VS host can adopt it or ignore it; nothing forces it to.
5. **Undo/document-model changes are announced first.** I7 adds named, finer-grained undo to `DesignDocument`. The
   VS host maps VS's undo to `Command(Undo)`, so this change is designed to keep `Undo()`/`Redo()`/`CanUndo`
   behaving the same. It lands as a single reviewed commit, and the DesignHost tests must pass on it.
6. **Both tracks run the whole `dotnet test` before merging**, including the other track's tests.

The two tracks also strengthen each other. Every improvement to `DesignSurface`/`DesignDocument` in I7 (component
tray, zoom, collection editor) appears in Visual Studio for free. The VS host's headless rendering and input path is
also a ready-made way to drive the IDE in UI tests.

## What we're building on

Already there and reusable:

- **Roslyn is already a dependency** (`Microsoft.CodeAnalysis.CSharp` 4.14 in `RadSharp.Design.Code`), and
  `LiveSession.Compile` already knows how to assemble a project's sources, references and in-process generator run.
  That is most of what a language-service workspace needs.
- **Diagnostics with locations** already exist (`BuildMessage(File, Line, Column, Severity, Id, Message)`), and so
  does a `NativeAOT` publish path (`ProjectBuilder.PublishAsync`) with streamed output.
- **The shell is itself a RADSharp form** (`DesignerForm.radform.json`): menus, toolbar, `PageControl` tabs,
  `TreeView`, `Splitter`, `ScrollView`, `PopupMenu`, tooltips, dark mode, access keys and menu keyboard navigation.
- **Multi-window support** in `IPlatformServices` (`ShowForm`, `RunModal`, non-blocking modal), which floating
  tool windows need later.

Not suitable as is:

- **`Memo` cannot be the code editor.** `TextEditor` keeps the whole document in one `string` and stores a full copy
  of it for every undo step (up to 500). `Memo` re-runs `TextLayout` over every line when the text changes. That's
  fine for a dialog field but unusable for a 3 000-line file at typing speed. The code editor needs its own buffer
  and virtualised rendering (I1).
- **Only Inter is bundled.** Code needs a monospaced font with good ASCII punctuation; I1 bundles one.
- **The shell layout is fixed** (`Panel` + `Splitter`): one form tab strip and no place to put code documents next
  to forms (I3).

## Architecture

```
 RadSharp.Designer (the IDE shell; RADSharp form, JIT)
 ├─ DockHost ─ document tabs (forms + code) ─ tool windows (Project, Structure, Inspector, Problems, Output, Find, Debug)
 ├─ CommandRegistry ─ menus, toolbar, shortcuts, command palette all read from it
 │
 ├─ RadSharp.CodeEditor        (new; depends on RadSharp.Core only; usable by any RADSharp app)
 │    TextBuffer (piece table + line index) · EditorView (virtualised paint, gutter, carets, folding)
 │    ITokenSource / IDecorationSource hooks (no Roslyn knowledge)
 │
 ├─ RadSharp.Ide.Language      (new; Roslyn)
 │    ProjectWorkspace : AdhocWorkspace built from RadProject (+ generator output, incremental)
 │    classification · diagnostics · completion · quick info · signature help · go to def · find refs ·
 │    semantic rename · formatting · code fixes (subset)
 │    Runs on a background worker; results marshalled with Application.Post; every request cancellable
 │
 ├─ RadSharp.Ide.Debug         (new) DAP client → netcoredbg process → the user's app (Debug build, separate process)
 │
 └─ RadSharp.Design / RadSharp.Design.Code  (shared with the VS track; additive changes only)
```

Key choices, with reasons:

- **The code editor is a general RADSharp control, not a part of the IDE.** It lives in its own package so the
  layering stays clean (Core → CodeEditor → Language adapter). Apps such as the `TextEditor` sample can use it, and it
  can be tested with golden images like any other control. Highlighting comes in through `ITokenSource`; the
  Roslyn classifier is one implementation, and a JSON tokenizer for `.radform.json` is another.
- **`AdhocWorkspace`, not `MSBuildWorkspace`.** RADSharp projects have a known, simple shape. `RadProject` already
  enumerates sources and `LiveSession` already resolves references, so we skip MSBuild Locator, the evaluation cost
  and the fragile load path. Only `PackageReference`s need extra work: run `dotnet restore`, then read
  `obj/project.assets.json` for compile references. This replaces the textual `RenameInCode` with Roslyn's semantic
  `Renamer`, which closes [designer-gap-analysis.md §3.13](designer-gap-analysis.md).
- **Generated code is in the workspace.** The form generator's output (fields, `InitializeComponent`) is added as
  generated documents and re-run when a form changes in the designer. Completion then knows `btnSave` as soon as you
  drop the button, before any save. That's the Delphi feel.
- **Two run modes, side by side.** **F9 stays in-process**: the current Roslyn + collectible `AssemblyLoadContext`
  path, fastest, no breakpoints. **F5 runs a Debug build as a separate process under the debugger**. **Ctrl+F5**
  runs that build without the debugger. In-process runs also gain exception capture, so the loop that has no debugger
  still points you at the line.
- **The debugger is netcoredbg over DAP.** Microsoft's `vsdbg` licence forbids use outside Microsoft tools. ClrMD can
  inspect but cannot step. netcoredbg (MIT, Windows/macOS/Linux, x64/arm64) speaks the Debug Adapter Protocol, so
  the client is a small JSON-RPC layer, and an alternative adapter can replace it later without touching the UI.

## Milestones

```
I1 code editor control ─▶ I2 language service ─▶ I4 build/problems ─▶ I6 debugger
         │                        │
         └──▶ I3 shell: docking + documents ──┬──▶ I5 navigation & search
                                              └──▶ I8 project & packages
I7 designer depth (independent; shared-code rules apply)
I9 polish & feel (continuous, closes last)
```

I1 and I3 can run in parallel. I7 is independent of everything else and is where most overlap with the VS track
lives, so schedule it when the VS track's changes to `RadSharp.Design` have settled.

### I1: Code editor control (L)

New project `src/RadSharp.CodeEditor` + `tests/RadSharp.CodeEditor.Tests`.

1. **`TextBuffer`**: a piece table with a balanced line index. Edits are O(log n) and so is mapping line ↔ offset.
   Snapshots are immutable versions, so a background reader such as the language service gets a consistent view
   while the user keeps typing. Undo stores **edit deltas** grouped into transactions (typing coalesces, as
   `TextEditor` already does), not text copies. CRLF/LF are detected and preserved.
2. **`EditorView`**: paints only the visible lines and caches each line's `TextLayout`, invalidated per line.
   Includes a gutter (line numbers, breakpoint and diagnostic glyphs, fold markers), current-line highlight,
   selection, horizontal and vertical scrolling on the existing `ScrollView`/`ScrollBarDriver`, and wheel and
   keyboard scrolling.
3. **Editing**: standard caret movement plus word and smart-home movement, auto-indent, Tab/Shift+Tab for blocks,
   bracket pairing and auto-close, comment toggle (Ctrl+/), duplicate and move line, **multiple carets**
   (Alt+click, Ctrl+D next match), column selection, and IME through the existing text-input path.
4. **Decoration hooks**: `ITokenSource` (colour spans per line, versioned), `IDecorationSource` (squiggles,
   highlights, inline hints), and `IFoldingSource`. None of them knows about C#.
5. **A bundled monospace font** (an OFL font such as JetBrains Mono or Cascadia Code), registered through the
   existing font registry; ligatures off by default.
6. **Find/replace bar inside the editor** (Ctrl+F / Ctrl+H): case, whole word, regex, highlight all matches.

**Done when:** a 20 000-line file opens in under 150 ms and typing at the end costs under 4 ms per keystroke
(benchmarked in tests). Undo/redo round-trips 1 000 random edits exactly (property test). Golden images cover
the gutter, selection, multiple carets and squiggles at 100 % and 150 % in light and dark. Input is tested through
`form.Simulate`, including IME composition. The `TextEditor` sample switches to the new control and publishes
NativeAOT.

### I2: Language service (L)

New project `src/RadSharp.Ide.Language` (depends on `RadSharp.Design.Code`; adds `Microsoft.CodeAnalysis.CSharp.Features`
pinned to the same Roslyn version as `Design.Code`).

1. **`ProjectWorkspace`** builds from `RadProject`: source documents, generated documents (incremental
   `GeneratorDriver`, re-run on form edits), and metadata references, including restored packages. It follows file
   changes on disk through a watcher and edits from the open editor's buffer.
2. **Classification** (semantic, through Roslyn's `Classifier`) as the `ITokenSource`, with a fast syntactic pass
   first so colours never lag behind typing.
3. **Live diagnostics** as squiggles plus the Problems list, debounced about 300 ms after typing stops. The analysers
   bundled with RADSharp (RAD1001 and others) run too, so a missing handler shows up before you build.
4. **Completion** (`CompletionService`) with snippets for `if`, `for`, `foreach` and `prop`, **signature help**,
   **quick info** on hover, and **go to definition** (F12; metadata-as-source for framework types is a stretch).
5. **Find all references** (Shift+F12) and **semantic rename** (F2) with a preview. Renaming a control in the
   designer now uses the same semantic path (through the additive `ICodeNavigator`/rename seam; the old textual
   `RenameInCode` stays as a fallback).
6. **Format document/selection** and format-on-paste; a small set of code fixes (add `using`, implement interface,
   generate method).

**Done when:** tests over the `TileEditor` and `SettingsDialog` samples assert specific completions (a dropped
control's field appears without saving), diagnostics (a deleted handler → RAD1001 within one debounce), a rename
across two files, and go-to-definition targets. Completion responds within 150 ms on a warm workspace (benchmarked).
Every request is cancellable, and stale results are dropped by snapshot version.

### I3: Shell: dock host, documents, commands (L)

Restructures `src/RadSharp.Designer`, which the IDE track owns.

1. **`CommandRegistry`**: every action has an id, title, default shortcut, enabled/checked predicate and handler.
   Menus, toolbar, context menus and the command palette all read from it, which replaces today's per-menu
   `mnuX_Click` wiring. Shortcuts are user-rebindable and stored in `DesignerSettings`.
2. **Unified document area**: forms and code files share one tab strip, with a dirty marker, close, middle-click
   close, reorder, "close others", and a preview tab for single-click from the project tree. F12 toggles a form and
   its code-behind **in place** (Delphi's gesture). **Split view** (designer beside its code) is a toggle.
3. **`DockHost`**: tool windows (Project, Structure, Inspector, Palette, Problems, Output, Find Results, and later
   Debug) dock to left/right/bottom regions, can be grouped as tabs, resized, collapsed to an edge strip, and moved
   between regions by drag. Layout is persisted per user. **Floating windows** (multi-monitor) come as phase 2 of
   this milestone, using `IPlatformServices.ShowForm`. This settles `gap-plan.md`'s M18 "dockable panels" decision
   with a yes for the IDE.
4. **Session restore**: open documents, carets, scroll positions, layout and the last project are restored on
   start. Settings and keymap are saved as JSON next to the existing `DesignerSettings`.
5. **Dirty-state safety**: Save All before build/run, a prompt on close with unsaved files, and a reload offer when
   an open file changes on disk (and automatic reload when the buffer is clean).

`DockHost` and the tab strip are general controls. If they prove themselves, they move to `RadSharp.Core` in a
later, separate commit, after the VS track's edits to `Form.cs` have merged.

**Done when:** a scripted UI test opens a project, opens a form and its code, splits them, moves the Problems
window to the left, restarts the IDE, and finds the same layout and carets. Every menu item resolves through
`CommandRegistry` (a test enumerates them). Rebinding a shortcut works and persists.

### I4: Build, run and the Problems window (M)

1. **Problems window**: live diagnostics (I2) and build diagnostics in one list, filterable by severity and
   current file, with double-click navigation and a count in the status bar.
2. **Build (Ctrl+Shift+B)**: `dotnet build` with streamed output parsed into `BuildMessage`s. **Publish** keeps the
   existing NativeAOT path. Both are cancellable.
3. **Run modes**: F9 in-process live run (existing), Ctrl+F5 separate-process Debug run, and F5 under the debugger
   once I6 lands. The app's stdout and stderr go to the Output window.
4. **Exception capture on live runs**: unhandled exceptions in the live-run assembly are shown with a stack trace
   whose frames link to source lines (portable PDBs are emitted in `LiveSession.Compile`).
5. **Run configurations**: arguments, working directory and environment, stored in the project
   (`Properties/launchSettings.json`, the format other IDEs read too).

**Done when:** tests build a sample with an injected error and get a Problems entry with the right file and line.
A live run that throws produces a clickable stack frame on the throwing line. Ctrl+F5 runs a separate process whose
output reaches the Output window.

### I5: Navigation and search (M)

1. **Command palette** (Ctrl+Shift+P) over `CommandRegistry`, with fuzzy match and recently used commands first.
2. **Go to file** (Ctrl+P), **go to symbol in workspace** (Ctrl+T, through Roslyn's navigate-to), **go to symbol in
   file** (Ctrl+Shift+O), and **go to line** (Ctrl+G).
3. **Find in files / replace in files** (Ctrl+Shift+F) with results in a tool window. Form files are searchable too,
   and a hit in a `.radform.json` opens the designer with that control selected.
4. **Back/forward navigation history** (Alt+←/→) across files and designer selection.
5. **Structure ↔ code sync**: selecting a control in Structure or on the surface highlights its field and handlers
   in the code view, and placing the caret in a handler highlights the controls wired to it.

**Done when:** a scripted test reaches a symbol, a file, a line, and a control in a form file through each entry
point, and back-navigation returns through them in order.

### I6: Debugger (L)

New project `src/RadSharp.Ide.Debug`.

1. **DAP client** (JSON over stdio) with the subset we need: launch, set breakpoints, continue, step
   over/into/out, pause, stack trace, scopes, variables, evaluate, terminate, and exception breakpoints.
2. **netcoredbg acquisition**: downloaded per RID on first use into the user's tool cache, with version and
   checksum checks. It is not bundled, so the IDE download stays small and the licence notices stay clear.
3. **UI**: breakpoints in the gutter (conditions and hit counts as a stretch), current-line marker, and Call Stack,
   Locals, Watch and Breakpoints tool windows. Hover shows a value while paused. A debug toolbar appears only while
   debugging.
4. **Break on unhandled exception** opens the throwing line with the exception detail.
5. **Stretch: .NET Hot Reload** (`dotnet watch`-style apply of edits to a running Debug process) for handler edits.
   Spike it and decide by how reliable it proves.

**Done when:** an integration test (skipped where netcoredbg is missing) launches a sample, hits a breakpoint in a
button handler driven by simulated input, reads a local, steps over, and continues to exit. A manual checklist
covers Windows, macOS and Linux.

### I7: Designer depth (M–L, independent; shared-code rules apply)

These are the remaining items from [designer-gap-analysis.md](designer-gap-analysis.md) and `gap-plan.md` M16/M18,
which an IDE can no longer leave out. All of them go in `RadSharp.Design` and benefit the VS track as well.
Coordinate each with that track before starting.

1. **Component tray** for non-visual components (§3.8): a strip under the surface instead of icons at their own X/Y.
   This changes the form-file meaning of a component's `left`/`top`, so it needs a format version bump and a
   serialization vector in `spec/vectors/serialization/`.
2. **Generic collection editor** (§3.11), with the menu editor's separator and `altShortcut` fields rebuilt on it.
   It's ready for M14's Columns and Nodes editors.
3. **Named undo** ("Undo Move btnSave") with coalescing and a depth cap (§3.3), with the same public
   `Undo`/`Redo`/`CanUndo` behaviour (rule 5).
4. **Zoom** (Ctrl+wheel, 50–400 %) and **in-place `PageControl` page editing**.
5. **Project frames and custom controls in the palette**, once M15 frames exist. They come from the user's live
   compiled assembly, which the language-service workspace now keeps current.

**Done when:** each item has the test its `gap-plan.md` entry already names, the DesignHost tests still pass, and
the format change has its vector.

### I8: Project, files and packages (M)

1. **Project tree**: real folders, new file from templates (class, interface, form, frame), rename/move/delete with
   the code updated (a namespace fix-up offer on move), drag to reorder, and reveal in OS file manager.
2. **NuGet**: a search/add/update/remove dialog over `dotnet add/remove package` and the nuget.org search API (no
   user details sent). The workspace re-restores afterwards.
3. **Project properties**: app name, version, icon, target RIDs, AOT on/off, and the root namespace. They're edited
   as a form over the `.csproj`, and unknown elements are preserved.
4. **Minimal git**: modified/added markers in the project tree and gutter, plus a diff of the current file against
   HEAD. Commit, branch and history stay in your git tool of choice; that's a deliberate cut.
5. **Multi-project**: open a `.slnx` with several projects (app + library) and one workspace across them. This is
   the "Project Groups" note in the designer gap analysis.

**Done when:** a scripted test creates a project, adds a folder and a class, moves it (namespace updated), adds a
package, and builds. Changing the icon and version shows up in the published executable's metadata on Windows.

### I9: Polish and feel (continuous; closes last)

These are the targets the earlier milestones get measured against, and the small things that make it enjoyable:

- **Budgets** (checked in CI benchmarks where measurable): cold start to welcome page < 1.5 s; open a project and
  show its main form < 2 s; keystroke to paint < 16 ms with the language service busy; F9 live run of a sample
  < 1.5 s warm; F12 form↔code toggle < 100 ms.
- **No modal dead ends**: every error message offers the next action (open the file, fix it, open settings).
  Long operations show progress in the status bar and are cancellable.
- **Welcome page** with recent projects, new-project templates (empty, single form, master/detail once M15 lands)
  and a "what's new" panel.
- **Themes**: light/dark already exist. Add editor colour themes and font/size settings, and follow the OS theme.
- **Discoverability**: shortcuts shown in menus, tooltips and the command palette; a one-page keyboard reference
  (Help › Keyboard Shortcuts) generated from `CommandRegistry`.
- **Autosave option** (on focus loss) and crash recovery of unsaved buffers.
- **Dogfooding**: from I3 onwards, work on the IDE itself happens in the IDE whenever possible, and every friction
  point becomes an issue. That's the same filter `gap-plan.md` uses for the reference app.

**Done when:** the budgets pass in CI benchmarks, and someone who has never seen the project builds `gap-plan.md`
M17's reference app inside the IDE from the getting-started page alone, without opening another editor.

## Testing approach

These follow `gap-plan.md`'s "How every milestone is done", plus:

- **CodeEditor**: unit and property tests on `TextBuffer`, golden images for `EditorView`, and benchmarks as tests
  with generous ceilings so CI noise doesn't flake them.
- **Language service**: tests run against the repo's own samples as fixtures, with no UI.
- **Shell**: scripted UI tests through `form.Simulate`, in the style of `RebuildTileEditorTests`. Once the VS
  track's headless host is merged, it can drive full-IDE screenshots without a window.
- **Debugger**: gated integration tests plus a per-OS manual checklist, as the VS plan does for the VSIX.

## Decisions I need from you

| Decision | Options | My default |
|---|---|---|
| Embedded editor vs external | keep external-only (status quo) / embedded, external optional | embedded, external optional: the premise of this plan |
| Debugger engine | netcoredbg via DAP / ClrMD (no stepping) / none (attach from VS Code) | netcoredbg, downloaded on first use |
| Code font | JetBrains Mono / Cascadia Code / other OFL | JetBrains Mono |
| Docking scope | regions + tabs only / plus floating windows | regions + tabs first, floating as I3 phase 2 |
| Where `DockHost` and the tab strip live | designer project / `RadSharp.Core` | designer first, promote later once the VS track merges |
| Git support | none / markers + diff (I8) / full | markers + diff |
| Priority vs the VS track | IDE first / VS first / interleave | interleave: I1 and I2 don't touch shared code at all, so they can start now |

## Risks

| Risk | Mitigation |
|---|---|
| Collision with the VS designer track | ownership table and rules above; IDE branches from `main`; I1, I2 and I6 live in new projects only |
| Text rendering performance in our own Skia pipeline | I1 benchmarks first; per-line layout cache; paint visible lines only; fall back to monospace fast-path shaping for ASCII lines |
| Roslyn Features package size and MEF startup cost | load the workspace in the background after the window shows; measure in the I9 budget |
| `AdhocWorkspace` drifts from what `dotnet build` sees (packages, analyzers, `Directory.Build.props`) | references from `project.assets.json`; a test that compares the workspace's diagnostics with `dotnet build`'s for every sample |
| netcoredbg gaps on some OS/arch | DAP abstraction lets another adapter be swapped in; manual checklist per OS |
| Scope creep toward "be Visual Studio" | non-goals list; every feature must serve the reference-app build in I9 |
