# Reference app: scope, 1.0 cut and build method

> Written 2026-11-17 on branch `m17-refapp` (from `main` at `0af2b2b`). Sources: `docs/gap-plan.md` (M13–M18, M14
> item 7), `docs/gap-analysis.md`, `docs/designer-gap-analysis.md`, `RadSharp-roadmap.md`, the other docs, and the
> code in `src/`, `samples/` and `tests/`. Where the docs and the code disagree, this page follows the code. Two
> claims were checked by experiment instead of by reading. The scripts and results are in the appendix.
>
> Purpose: `gap-plan.md` M17 makes "a small but real application" the 1.0 acceptance test, and M14.7, M15, M16 and
> M18 leave several items "decided by the reference app". This page picks the app, lists what it needs, and from
> that recommends what goes into 1.0. It also proposes how agents, which can't drive a GUI, can build the app "with
> only the designer plus code". M13 is being finished in parallel, so its items count as **arriving**.

## Summary

- **The app: Tally**, a small invoicing app for a freelancer or small shop. It has clients (contacts), invoices with
  line items, and settings. There are three designed forms plus one progress dialog. Data lives in one SQLite file,
  accessed with `Microsoft.Data.Sqlite` (plain ADO.NET, no ORM). UI state lives in a `settings.json`.
- **Two findings that change the plan.** Both were checked by experiment (appendix):
  1. **F9 (live run) can't compile code that uses a NuGet package or any source generator other than RadSharp's.**
     That includes `System.Text.Json` source generation, which `docs/aot-safe-stack.md` recommends. `LiveSession`
     references only the designer's own assemblies and runs only `FormGenerator` and `MetadataGenerator`
     (`src/RadSharp.Design.Code/LiveRunner.cs:85,118`). Both libraries publish cleanly with NativeAOT; only F9 breaks.
  2. **The designer can't hold a control class defined in the app's own project.** It isn't on the palette, and a
     form file that uses one loses that node when the designer loads and re-saves it (`FormReader.cs:126`: "unknown
     class … node and its children skipped"). So any workaround that writes a custom control (for example a date
     picker) breaks the "designer plus code" rule.
- **Recommended 1.0 cut:**
  - **In:** F9 with the project's packages and generators; project controls in the designer, with unknown classes
    kept rather than dropped; a code-side two-way `Binder` (not the declarative form-file binding); a light
    `Action`; `DateTimePicker` + `Calendar`, date only; menu-editor separator and alt-shortcut fields; naming new
    forms; the `itemIndex` load bug; the debugging-story basics.
  - **Out:** frames, visual inheritance, declarative and list binding, localization, FlowPanel/GridPanel,
    AutoSize, zoom, the generic collection editor, MaskEdit.
- **Build method:** a scripted designer driver. It runs the real `DesignerForm` under a fake host and uses the same
  input paths a person does (palette, mouse drag on the surface, typing in the Object Inspector and the Name box,
  double-click to create a handler, the collection editors, Mod+S, F9). Each form has a checked-in *recipe*, and a
  test proves that the committed `.radform.json` is exactly the recipe's output. Agents write only recipes and `.cs`
  code, never form JSON. A short list of checks is redone by hand in the real designer.

---

## 1. The app

### 1.1 Why contacts + invoices (and not something else)

| Candidate | Grid editing | Master/detail | Tree | Dialogs | File drop | Verdict |
|---|---|---|---|---|---|---|
| **Clients + invoices** | invoice lines | client → its invoices; invoice header → lines | status / year / month navigation | invoice, settings, confirmations | CSV import, logo | **Chosen**: every M17 item occurs naturally |
| Inventory / stock | stock grid | item → movements | categories | few | barcode CSV | weaker contact/detail form |
| Notes / to-do | none | weak | folders | few | attachments | no grid editing, no real data entry |
| Media library | none | album → tracks | folders | few | files (natural) | no data entry; mostly a viewer |

Clients and invoices is the most ordinary business app there is, and that is the point: if RadSharp can't build this
without workarounds, it isn't ready for the people it targets (Delphi and WinForms developers).

### 1.2 Forms and dialogs

| Form | Contents | Exercises |
|---|---|---|
| **MainForm** | `MainMenu` (File, Edit, Client, Invoice, View, Help, with mnemonics and separators); `ToolBar` with `ImageList` icons and a search `Edit` (debounced by a `Timer`); a left `TreeView` for navigation (*Clients*, *Invoices* › Draft / Sent / Overdue / Paid, *By year* › 2026 › Nov, loaded lazily); a `Splitter`; a right `PageControl` with two tabs. **Clients** tab: a virtual `ListView` of clients (sortable, resizable columns) over a detail panel (name, company, email, phone, address, notes `Memo`, Save/Revert) and the selected client's invoices (`ListView`). **Invoices** tab: a virtual `ListView` filtered by the tree node. `StatusBar` (counts, totals, "Loading…"); `PopupMenu` on both lists. | grid, tree, tabs, async loading, the bound master/detail form, mnemonics, file drop (a CSV dropped on the client list), settings persistence (placement, splitter, columns, tab) |
| **InvoiceForm** (modal, `ShowModalAsync`) | Header: number, client (editable `ComboBox` with autocomplete), issue and due dates, status. A `StringGrid` of lines (Description, Qty, Unit price, Tax %, Amount, computed and read-only) with in-place editing and validation in `onSetEditText`. Subtotal, tax and total labels; notes `Memo`; buttons **&Save**, **&Cancel**, **E&xport…**; `CloseQuery` asks before discarding changes. | grid editing, master/detail (header → lines), dialogs, mnemonics with `Label.FocusControl`, dates |
| **SettingsForm** (modal) | `PageControl`: **Business** (name, address, tax ID, logo `Image` that accepts a dropped PNG/JPEG or **Browse…**); **Invoices** (next number, payment terms `SpinEdit`, default tax rate, currency `ComboBox`); **Appearance** (light/dark theme). OK/Cancel. | dialogs, tabs, file drop on a specific control, settings persistence |
| **ProgressForm** (modal, small) | `Label`, `ProgressBar`, **Cancel**. Used by CSV import and demo-data generation. | async loading with progress and cancellation |
| Message boxes | `Dialogs.MessageDlgAsync` for deletes ("Delete client with 12 invoices?"), import confirmation and errors. The About box is a `MessageDlg`. | dialogs |

That is three designed forms plus one tiny one. A separate client dialog would only duplicate the inline detail pane,
and the inline pane is the master/detail test, so there isn't one.

### 1.3 Data model

| Entity | Fields |
|---|---|
| `Client` | `Id`, `Name`, `Company`, `Email`, `Phone`, `Street`, `City`, `Postcode`, `Country`, `TaxId`, `Notes`, `Archived`, `CreatedAt` |
| `Invoice` | `Id`, `Number` (for example `2026-0042`), `ClientId`, `IssueDate`, `DueDate`, `Status` (Draft/Sent/Paid; *Overdue* is computed from `DueDate`), `Currency`, `Notes`, `PaidOn?` |
| `InvoiceLine` | `Id`, `InvoiceId`, `Position`, `Description`, `Quantity` (decimal), `UnitPrice` (decimal), `TaxRate` (decimal %) |
| `BusinessSettings` (one row) | name, address, tax ID, logo (PNG bytes), next invoice number, payment terms (days), default tax rate, currency |
| UI state (`settings.json`) | window placement (the M13 helper), splitter position, column widths and sort per list, active tab, theme, last data file, recent files |

Amounts are `decimal`, stored as integer cents (or as TEXT) in SQLite, so no rounding goes through `REAL`.

### 1.4 Where data lives, and AOT safety

Measured on 2026-11-17 (appendix A):

| Option | NativeAOT publish | F9 live run today | Extra shipping cost | Notes |
|---|---|---|---|---|
| **`Microsoft.Data.Sqlite` 10.0.0** (raw ADO.NET) | **clean**: no IL2xxx/IL3xxx warnings with the AOT and trim analyzers on and `TrimmerSingleWarn=false`; the exe runs and does async open, query and reader | **fails** (CS0234: the package isn't referenced) | `e_sqlite3.dll` 1.8 MB, a fourth native library next to SDL3, Skia and HarfBuzz | realistic for business apps; async queries make "async loading" natural |
| `System.Text.Json` with source generation (`JsonSerializerContext`) | **clean** (same run) | **fails** (CS0534: the generator doesn't run in F9) | none | recommended by `aot-safe-stack.md`, yet unusable under F9 today |
| `System.Text.Json` with `JsonDocument`/`Utf8JsonWriter` by hand | clean (how `DesignerSettings` works) | works | none | the only JSON path that works everywhere today; verbose |
| EF Core | not used | n/a | n/a | NativeAOT support is still experimental (it needs precompiled queries); out of scope |

**Recommendation:** SQLite through a small hand-written `SqliteStore` behind an `IStore` interface, with a
`MemoryStore` (seeded demo data) used until F9 can load packages. Use `JsonSerializerContext` for `settings.json`
and for CSV import options. This deliberately runs into finding 1, because any real user will, and the fix is in
the 1.0 cut (§3).

### 1.5 What "async loading" means in Tally

1. **Startup.** The form shows at once; `Shown` starts `await store.OpenAsync()` and then the client count and the
   first page. The status bar says "Loading…" and the window stays responsive. This uses the installed
   `SynchronizationContext` (`src/RadSharp.Core/Threading/UiSynchronizationContext.cs`, `SdlHost.cs:51`).
2. **Detail on selection.** Selecting a client starts loading its invoices with a `CancellationToken`. A newer
   selection cancels the older load, and a stale result is thrown away. The selection is the race to test.
3. **Lazy tree.** *By year* nodes have `HasChildren = true`. `onExpanding` adds a "Loading…" child, queries the
   month counts asynchronously, then replaces it.
4. **CSV import / demo data.** Parsing on `Task.Run`, with progress posted back through `Application.Queue`,
   **Cancel** honoured, and the import done in one transaction so a cancel leaves no partial data. *Generate demo
   data* creates 100 000 invoices, which proves the virtual `ListView` at a realistic size.

### 1.6 Workflows (each one becomes a test through simulated input)

1. First run: no data file, so a new empty file is created; open Settings, enter business details, drop a logo.
2. Add a client in the detail pane, edit fields, Save (Mod+S). Revert discards changes. Switching selection with
   unsaved changes asks first.
3. New invoice for the selected client (Mod+Shift+N): add three lines in the grid, change Qty, check totals, Save.
   Cancel with changes asks first.
4. Find: type in the search box. After the debounce the client list filters. Sort by column; resize columns.
5. Navigate: tree › Invoices › Overdue lists overdue invoices; double-click opens one.
6. Mark paid (context menu), delete invoice/client with confirmation.
7. Drag `contacts.csv` from the desktop onto the client list: confirm, progress, done (or cancel).
8. Export an invoice as HTML and open it in the browser (`Application.OpenUrl`) to print. This is the stand-in for
   printing (§3).
9. Quit and restart: same data file, window placement, splitter, column widths, tab and theme.
10. Keyboard only: Alt+F opens File, Alt+N on "&Name:" focuses the name box, Tab order is sensible.
11. Drag the window to a monitor with a different scale: the layout rescales without drift (M13).

---

## 2. Feature matrix

Status values: **exists** (with evidence), **arriving** (M13, in flight), **M15.n / M16 / M14.7** (planned there),
**not planned** (nothing in `gap-plan.md` covers it), **bug** (a known defect).

| Feature (used for) | Status | Evidence / note |
|---|---|---|
| New project from the designer (File › New Application) | exists | `NewApplicationDialog` (`src/RadSharp.Designer/Dialogs.cs`), `RadProject.Create` (`src/RadSharp.Design.Code/RadProject.cs:58`); also `templates/radsharp-app` |
| Add a form **with a name** (InvoiceForm, SettingsForm) | **not planned** | `DesignerForm.NewForm` only creates `FormN` (`DesignerForm.Code.cs:40`); there is no rename-form command. `RadProject.AddForm(className)` exists but isn't reachable from the UI |
| MainMenu, shortcuts, submenus | exists | `src/RadSharp.Core/Controls/Menus.cs`; `MenuEditor` (`src/RadSharp.Design/Editors.cs:32`) |
| Menu **separators** and alt shortcuts set in the designer | M16 (menu-editor fields) | the editor has no field for either; `RebuildTileEditorTests` sets `created.Separator` directly ("no field for it yet") |
| PopupMenu on lists | exists | `PopupMenu` (`Menus.cs:428`), `popupMenu` property (spec §C) |
| ToolBar, ToolButton, ImageList icons | exists | `ContainerControls.cs:141,176,296`; `ImageList.Resolve` (M12) |
| TreeView, lazy expand, `Tag` | exists | `ViewControls.cs:1203`; `onExpanding` with `HasChildren`; `TreeNode.Tag`; nodes editor `TreeNodesEditor` (`Editors.cs:387`) |
| Splitter | exists | `BasicControls.cs:223` |
| PageControl/TabSheet, switching pages in the designer | exists | `ContainerControls.cs:6,19`; clicking a tab in `DesignSurface.OnMouseDown` switches the page |
| Add/delete page commands in the designer | M16 (in-place PageControl editing) | today: place a `TabSheet` from the palette into the PageControl (`DesignDocument.AcceptsChild`, `DesignDocument.cs:148`) |
| Virtual ListView, sort, column resize, multi-select, type-ahead | exists | `ViewControls.cs:79` (`GetItem`, `FindItem`, `Compare`, `AutoSort`); `RowViewSuiteTests` |
| StringGrid in-place editing with validation, Columns editor | exists | `GridControls.cs:1234` (`onGetEditText`/`onSetEditText`); `GridColumnsEditor` (`Editors.cs:226`) |
| Editable ComboBox with autocomplete | exists | `ListControls.cs:561` (`style: dropDown`, `autoComplete`) |
| A ComboBox `itemIndex` set in the designer survives load | **bug** | `gap-plan.md` M14: "`itemIndex` saved before `items` is still lost on load"; never scheduled |
| Edit, Memo, placeholder, read-only, CheckBox, RadioButton, GroupBox | exists | `EditControls.cs`, `ToggleControls.cs` |
| Integer entry (payment terms) | exists | `SpinEdit` (`ValueControls.cs:158`) |
| Decimal entry outside a grid | not planned | `SpinEdit` is int-only; Tally only needs decimals in the grid, where `onSetEditText` validates |
| Image from file, scaling | exists | `Image` (`ContainerControls.cs:250`), `Picture.FromFile/FromBytes` (`Drawing/Picture.cs`) |
| Label word wrap and ellipsis | exists | `Label.WordWrap`/`Ellipsis` (`BasicControls.cs`) |
| StatusBar | exists (text only) | `StatusBar.SimpleText` (`ContainerControls.cs:118`); panels are not planned and not needed |
| ProgressBar, Timer | exists | `ValueControls.cs:129`, `Components.cs:18` |
| Modal dialogs, async: `ShowModalAsync`, `ModalResult`, Default/Cancel buttons | exists | `Form.cs:391,401`; `Button.ModalResult` |
| `MessageDlgAsync` | exists | `Dialogs.cs:104` |
| Open/Save dialogs, async, `FileResult` | exists | `FileDialog.ExecuteAsync` (`Dialogs.cs:42`) |
| Unsaved-changes prompt | exists | `Form.CloseQuery` (`Form.cs:380`) |
| `await` resumes on the UI thread; `Application.Queue` | exists | `UiSynchronizationContext.cs`; `Application.Queue` (`Application.cs:15`) |
| Unhandled exceptions don't kill the app | exists | `Application.ThreadException` (`Application.cs:9`) |
| Tooltips (toolbar hints) | exists | `Controls/ToolTip.cs`, `Control.Hint` |
| Edit/Memo context menu | exists | M11 item 5 |
| **File dropped on a specific control** (CSV on the list, logo on the Image) | exists (not hand-verified on a real OS) | `Form.ProcessFilesDropped` (`Form.cs:891`), `DragEventArgs.Files`/`IsFileDrop` (`Input/Input.cs:75`), wired at `SdlHost.cs:585` |
| Light/dark theme at run time | exists | `Theme.Default`/`Theme.Dark`; the designer switches them the same way (`DesignerForm.Code.cs`, `mnuToggleDarkMode_Click`) |
| Open exported HTML or a `mailto:` link | exists | `Application.OpenUrl` (`Application.cs:29`) |
| Window state, start position, minimum size | exists | `Form.WindowState`, `StartPosition`, `Resizable` (gap-analysis gap 7) |
| Window placement save/restore, Screen API | arriving (M13) | |
| Mnemonics and `Label.FocusControl` | arriving (M13) | today `grep FocusControl src` finds nothing |
| `Application.Title`, `OnIdle`, `Terminate` | arriving (M13) | `Application.ExitCode` exists |
| Per-monitor DPI rescale | arriving (M13) | today scale is fixed at load (gap 8) |
| PointerKind | arriving (M13) | Tally doesn't use it |
| Settings JSON: hand-written `JsonDocument`/`Utf8JsonWriter` | exists | `DesignerSettings.Load` (`src/RadSharp.Design.Code/DesignerSettings.cs`) |
| Settings JSON via `JsonSerializerContext` **under F9** | **not planned** | AOT-clean, but F9 runs only RadSharp's generators (`LiveRunner.cs:85`); appendix B |
| **SQLite under F9** | M16 ("NuGet component libraries usable in live runs (F9)"), partly | F9 references only the designer's own assemblies (`LiveRunner.cs:118`); appendix B. M16 covers *component libraries*, not "any package plus its generators and native libraries" |
| **A project-defined control on the palette, kept on load** | M15.1 ("project classes appear in the palette"), partly | the palette is `ComponentRegistry.All` of the designer process (`ComponentPalette.cs`); unknown classes are dropped on load (`FormReader.cs:126`) and so lost on save. Keeping unknown nodes is not planned |
| Master/detail without copy-in/copy-out | M15.3 | nothing yet (`grep INotifyPropertyChanged src` finds nothing); `samples/SettingsDialog/SettingsForm.cs` shows today's `Load`/`Result` pattern |
| One command on menu + toolbar + context menu, with enable state | M15.4 | nothing yet; today handlers are shared with `(object sender)` and enabling is done by hand |
| Reusable address block (client and business address) | M15.1 (frames) | Tally gets by with designer copy/paste (`DesignDocument.CopySelection`/`Paste`) and a small helper class |
| Issue and due dates | M14.7 | no `DateTimePicker` or `Calendar` in `src/` |
| Masked input (phone, postcode) | M14.7 | not used: masks don't fit international data |
| Localized strings | M15.5 | not used: Tally is English-only |
| FlowPanel/GridPanel, AutoSize | M16 | not used: Align + Anchors suffice, and fonts are bundled and identical on every OS |
| Printing | **not planned** | Tally exports HTML and the browser prints it |
| F5 debugging in VS Code from a designer-made project | M16 (debugging story) | the template has `.vscode/launch.json` (`templates/radsharp-app/.vscode/`); `RadProject.Create` writes none |
| NativeAOT build from the designer, cross-OS workflow | exists | `ProjectBuilder.PublishAsync` (`LiveRunner.cs:172`), `RadProject.CrossPlatformWorkflow` |
| Exe icon, version resource, `.app`, AppImage | M17 (packaging) | packaging must also carry `e_sqlite3` |

---

## 3. Recommended 1.0 cut

The test for each item is what Tally needs, and what it costs to do without it in plain code.

### M15

| Item | 1.0? | Reason from the app |
|---|---|---|
| **1. Frames** (form file with a container root, palette entries) | **Out.** But **in**: *project controls in the designer* (below) | Tally's only reusable piece is a four-field address block used in two places. Designer copy/paste of the fragment already works, and a 15-line `AddressFields` helper (it wraps four `Edit`s, with `Load(Address)` and `Store(Address)`) covers the code. Frames need `FormReader.LoadInto(Control)` (today it takes a `Form`, `FormReader.cs:33`), generator changes, palette entries, and a decision on editing embedded frames: M at least. **The part Tally does need** is cheaper and more urgent. A custom control the app defines in code must be placeable and must survive a designer load and save. Today it's silently deleted. |
| **2. Visual inheritance** | **Out** | No two Tally forms share enough to justify it; the OK/Cancel bar is two buttons. |
| **3. Data binding** | **In, reduced: a code-side `Binder`.** **Out:** the `"text": "{Model.Name}"` form-file syntax, list item-sources and converters in the generator | Without binding, Tally's three bound surfaces (client detail, invoice header, settings) need about 90 lines of copy-in/copy-out plus a `_loading` re-entry guard and dirty flags, and each new field is a chance to forget one of the two copies (see below). A typed, lambda-based binder removes all of it with no reflection and no generator work, so it's AOT-safe by construction. List binding isn't needed, because the virtual `ListView` (`onGetItem`) is already three lines per list. The declarative syntax is the large, design-heavy part (spec, generator, format version) and can follow after 1.0 on top of the same runtime. |
| **4. Action** | **In, light** | Tally has about 12 commands, most of them on two or three surfaces (menu, toolbar, context menu, shortcut), with enabled state that depends on selection and on the dirty flag. By hand that's an `UpdateCommands()` setting about 30 `Enabled` flags, called from every place that changes state. Make `Action` a non-visual `Component` with caption, shortcut, hint, `enabled`, `checked`, `onExecute` and `onUpdate`, linked by name (`"action": "actSave"`) like `popupMenu` and `imagesName`, so it needs no generator change. `onUpdate` runs on `Application.OnIdle` (M13). `MenuItem` gains an `action` key, which is a minor form-format version step with a vector. |
| **5. Localization** | **Out** | Tally is single-language. List it as a known limit on the M17 page. |

**What the code looks like without binding** (client detail, today's pattern from `samples/SettingsDialog`):

```csharp
Client? _current; bool _loading, _dirty;

void lvClients_Change(Control sender)
{
    if (!ConfirmLeave()) return;           // ask about unsaved changes
    _current = SelectedClient();
    _loading = true;                       // or every Edit's Change marks it dirty
    edtName.Text = _current?.Name ?? "";   // ...x 11 fields
    edtCompany.Text = _current?.Company ?? "";
    _loading = false; _dirty = false; UpdateCommands();
}
void Field_Change(Control sender) { if (!_loading) { _dirty = true; UpdateCommands(); } }   // shared by 11 controls
void actSave_Execute()
{
    _current!.Name = edtName.Text.Trim();  // ...x 11 again, the second copy
    _current.Company = edtCompany.Text.Trim();
    store.Save(_current); lvClients.Invalidate(); _dirty = false; UpdateCommands();
}
```

**The same with the proposed `Binder<T>`** (code-only, in `RadSharp.Core`):

```csharp
readonly Binder<Client> _client = new();          // buffered: controls <-> a copy, Commit() writes back

public MainForm()
{
    InitializeComponent();
    _client.Bind(edtName, c => c.Name, (c, v) => c.Name = v);
    _client.Bind(chkArchived, c => c.Archived, (c, v) => c.Archived = v);
    // ...one line per field; Edit/Memo/CheckBox/SpinEdit/ComboBox(text or index)/TrackBar, plus a format/parse pair
}
void lvClients_Change(Control sender) { if (ConfirmLeave()) _client.Current = SelectedClient(); }
void actSave_Update(Action a) => a.Enabled = _client.IsDirty;
void actSave_Execute(Action a) { _client.Commit(); store.Save(_client.Current!); }
```

Judgement on M15's done-when ("no manual copy-in/copy-out for master/detail"): it **is** worth it for 1.0, because
it's the most repeated code in every form-over-data app, and the reduced version is small (S–M). It is **not** worth
holding 1.0 for the declarative, generator-resolved version. Change the M15 done-when to "a master/detail form with
no copy-in/copy-out, and a Save action enabled by a dirty flag, under NativeAOT", dropping "one form plus one frame".

### M16 (open items)

| Item | 1.0? | Reason |
|---|---|---|
| FlowPanel / GridPanel | **Out** | Tally's label/edit columns lay out fine with Anchors. Without localization and with identical bundled fonts on every OS, text doesn't change size, so there's no overflow problem to solve. |
| AutoSize / `GetPreferredSize` | **Out unless** the hand check (§4.4) finds label sizing a real nuisance | Same reason. If it's added, it's S for `Label` only. |
| Designer zoom | **Out** | Neither Delphi nor the WinForms designer has it. |
| In-place PageControl editing | **Out, keep an S fallback**: "New Page / Delete Page" on the surface's context menu if the hand check finds placing `TabSheet`s from the palette clumsy | Switching pages by clicking a tab works today. |
| **Menu-editor fields (separator, `altShortcut`)** | **In (S)** | Tally's menus need separators. Today the only way is hand-editing JSON, which breaks the designer-only rule. |
| Columns and Nodes editors | already done | `GridColumnsEditor`, `TreeNodesEditor` (M14 stage 6) |
| Generic collection editor | **Out** | The bespoke editors exist and Tally needs no other collection. It would be a refactor, not a capability. |
| Palette entries for project frames | **Out** with frames; replaced by *project controls in the designer* (in) | |
| **NuGet libraries in F9** | **In, widened (M)** | Must cover any `PackageReference` (managed and native assets), the project's analyzers and source generators (including System.Text.Json's), and its `ProjectReference`s. Otherwise F9, the core of "the Delphi loop", stops working once an app uses SQLite or follows the AOT-safe-stack page. |
| **Debugging story** | **In (S)** | `RadProject.Create` should write `.vscode/launch.json` and `tasks.json` as the template does, plus a page: "F5 in your IDE; design edits in the designer". Tally's development will need breakpoints. |

### M14 item 7

| Item | 1.0? | Reason |
|---|---|---|
| **DateTimePicker + Calendar** | **In, date only (M)** | Invoices have issue and due dates. An `Edit` plus parsing is a workaround a real user would resent. Writing your own picker in the app is impossible today: a project-defined control is dropped by the designer (finding 2). Build it as a popup-layer `Calendar` inside a `DateTimePicker` (Edit-like text with a drop-down button, keyboard ±day/month, `Date?` value), with goldens at 100 % and 150 %. Leave time-of-day, ranges and culture-specific long formats out. |
| MaskEdit | **Out** | Phone numbers, postcodes and tax IDs vary by country, so masks fight real data. Money is edited in the grid. |

### Not in any milestone, needed by Tally (add to 1.0)

| Item | Size | Why |
|---|---|---|
| F9 with packages and generators (widens M16's F9 item) | M | finding 1 |
| Project controls in the designer: compile the project (reuse `LiveSession.Compile`), register its classes on the palette, and **keep unknown nodes as opaque JSON** instead of dropping them | M (the keep-unknown part alone: S) | finding 2 |
| Name a new form (File › New Form asks for the class name) and Project › Rename Form (file + class + code) | S | a designer-only app needs `InvoiceForm`, not `Form2` |
| `itemIndex` before `items` lost on load (ListBox/ComboBox) | S | the designer writes this order itself, so a default currency set in the designer is lost |

Known limits, listed rather than fixed: no printing (HTML export instead), StatusBar text only, no decimal
`SpinEdit`, clipboard text only, and no frames, visual inheritance or localization.

---

## 4. Building it "with only the designer plus code", by agents

### 4.1 What exists to build on

- **Document level** (`RebuildTileEditorTests`, `tests/RadSharp.Design.Tests/RebuildTileEditorTests.cs`):
  `DesignDocument.Create(cls, parent, x, y)` stands in for the palette, `DesignDocument.Rename` for the Name box,
  `PropertyGrid.Apply(row, text)` for the inspector, and `MenuEditor`'s own fields for menus. The test proves the
  saved text is byte-identical to a reference. It **bypasses `DesignerForm`**: for example, `doc.Rename` doesn't
  run `RenameInCode`, and nothing writes handlers into code.
- **Designer level** (`tests/RadSharp.Designer.Tests/ProjectWorkflowTests.cs`): the real `DesignerForm` with a
  `FakeHost : IPlatformServices` that answers modals (`AnswerModal` returns the button to press). It already drives:
  opening a project, Mod+N, palette selection (`tvPalette.Selected = node`), palette drag via `BeginDrag`, pressing
  and dragging on the surface (`DesignSurface.DesignToScreen` + `Simulate.Press/MoveTo/Release`), typing into the
  Name box (which triggers the project-wide `RenameInCode`), `gridProps.BeginEdit(row)` + `Simulate.Type` + Enter,
  the Columns and Nodes editors through `AnswerModal`, the context menu, `EditHandler` (what a double-click does:
  set the handler name, insert the stub, save, open the editor), and **F9** (`Simulate.Key(Key.F9)`, live form in
  `host.Shown`).
- **Headless rendering:** `FormRenderer.RenderToBitmap` + `EncodePng` (`src/RadSharp.Core/Drawing/FormRenderer.cs`)
  turns any form into a PNG an agent can look at. `RADSHARP_SCREENSHOT` does the same for a real SDL run.

### 4.2 Proposed method: recipes on a scripted designer driver

Use the designer level, not the document level, because it is the code a person actually runs.

1. **`DesignerDriver`** (test helper, new `tests/RadSharp.RefApp.Tests`) wraps `DesignerForm` + `FakeHost`. Each
   verb uses the person's path:

   | Verb | Path it drives |
   |---|---|
   | `NewApplication(name, folder, packages)` | the File › New Application menu item's `PerformClick`; `AnswerModal` fills `edtName`/`edtLocation`/`edtPackages` and clicks `btnOk` |
   | `NewForm(className)` | File › New Form (once it asks for a name, §3; until then `RadProject.AddForm` + `OpenFormFile`, logged as a bypass) |
   | `Place("ListView", parent: "tsClients", x, y, w, h)` | type the class into `edtPaletteFilter`, select the leaf, then press, drag and release on the surface at the parent's design point (`DesignToScreen`) |
   | `Select("lvClients")` | click its centre on the surface |
   | `Name("lvClients")` | type into `edtName`, Enter (renames the code too) |
   | `Set("align", "client")`, `Anchors(left, top, right)` | `gridProps.IndexOf` → `BeginEdit` → type → Enter; enum and bool rows through their list popup; anchors through the flag rows |
   | `Handler("onChange")` | double-click the event row on the Events tab (`EventDoubleClick` → `EditHandler`): the stub lands in the `.cs` |
   | `Columns(...)`, `Nodes(...)`, `Menu(...)`, `TabOrder(...)` | the real editors, answered through `AnswerModal`, as `ProjectWorkflowTests` does |
   | `Save()` | Mod+S |
   | `Run()` | F9, then drive the live form with `Simulate` |

   Every shortcut that skips real input (setting `tvPalette.Selected`, `BeginDrag`, a `FakeHost` file dialog that
   returns a path, the harmless "editor" command) is listed in the driver's header. Each is a known gap between the
   recipe and a person.
2. **One recipe per form** (`refapp/recipes/MainForm.recipe.cs`…), written by agents as plain C# calls on the driver.
   Layout numbers come from the agent's plan and are checked by looking at PNG renders (step 5).
3. **Code** is written directly in the `.cs` files, inside the stubs the designer created, plus the model and store
   files. That is the "plus code" half, exactly what a person does in their IDE.
4. **Guard test:** `RebuildReferenceAppTests` runs every recipe on a fresh project in a temp folder and asserts each
   produced `*.radform.json` is **byte-identical** to the committed `refapp/Tally/*.radform.json`. Agents never
   hand-edit form JSON. To change a form, change the recipe and run with `RADSHARP_REFAPP_WRITE=1` to copy the
   outputs over. This is `RebuildTileEditorTests` raised to the designer level, and it keeps protecting the
   designer after 1.0.
5. **Visual check:** the same test writes `TestResults/refapp/<Form>@100%.png` and `@150%.png`. Agents look at them
   before committing.
6. **App tests:** `tests/RadSharp.RefApp.Tests` also references `refapp/Tally` and runs the §1.6 workflows on the
   real forms with `form.Simulate` and a fake host. Asynchronous parts use an `IStore` with a controllable delay.
   File drop is tested through `ProcessFilesDropped`.
7. **Rule for bugs:** whenever a recipe or the app code needs something the designer or framework can't do, the agent
   stops, files it as a bug here or in `gap-plan.md`, and fixes it in the framework. It never works around it in
   the app. That is M17's "anything that needs a workaround becomes a bug".

### 4.3 How the project gets created

Use **File › New Application** through the driver (step 1). It runs `RadProject.Create`, which writes the `.csproj`
with `PublishAot` and the analyzers, `Program.cs`, `MainForm`, the `.sln`, `.gitignore`, the cross-OS workflow,
and a `nuget.config` pointing at `artifacts/packages`. Then `dotnet pack` Core and Platform.Sdl, and add
`Microsoft.Data.Sqlite` by editing the `.csproj` (code, as a person would in their IDE). `dotnet new radsharp-app` is
the "without the designer" path and isn't the acceptance test. It still gets one smoke test, since M17's done-when
includes a newcomer following `getting-started.md`.

### 4.4 What a person should redo by hand in the real designer

A short session, about an hour, after stage 5:

1. File › New Application on a clean folder; F9; Build. Run the exe from Explorer or Finder.
2. Recreate **SettingsForm** by mouse only: palette click-and-drag, resize handles, snapping, the Anchors rows,
   adding TabSheets, the Tab Order editor. Compare it with the recipe's render by eye, not byte for byte.
3. Double-click a button: the handler opens in the IDE at the right line. Rename a control: the code follows.
4. Use the Menu editor (with separators, once they exist), the Columns editor and the Nodes editor by hand.
5. Drop a real CSV from the desktop onto the client list, and a real PNG onto the logo. Neither path has been
   hand-verified on any OS yet (`gap-plan.md` M13 item 4).
6. Drag the running app between monitors of different scale; Alt mnemonics; switch to the dark theme.
7. F5 in VS Code or Visual Studio with a breakpoint in a handler.
8. Linux (WSL2) at least once; macOS if a machine is available.

Anything that feels wrong becomes a bug, and the recipe gains a step that would have caught it.

---

## 5. Build order

Each stage has a done-when that a test can check. "Blocked on" names the framework piece; until it lands, the
stage uses the interim noted and records the workaround as a bug.

| # | Stage | Done when | Blocked on |
|---|---|---|---|
| 0 | **Harness + empty project.** `DesignerDriver`, File › New Application, the guard test with one empty form | the recipe creates `Tally`; F9 shows MainForm under the fake host; `dotnet build` of the created project passes against packed packages | named forms (S). Interim: `RadProject.AddForm`, logged as a bypass |
| 1 | **Main window shell on `MemoryStore`.** Menu, toolbar, tree, splitter, tabs, virtual client and invoice lists, status bar, demo-data generator | F9 run lists 10 000 clients, sorts on a header click, filters from the tree; renders at 100 % and 150 % checked | menu separators (S, M16). Interim: no separators |
| 2 | **Client master/detail, by hand.** Detail pane with copy-in/copy-out, dirty flag, Save/Revert, client's invoices | workflow 2 passes through simulated input. **Count the copy code** (feeds the binding decision) | none |
| 3 | **InvoiceForm.** Lines grid with validation, totals, client combo, async modal, `CloseQuery` | workflow 3 passes; totals right with decimal quantities | DateTimePicker (M, M14.7). Interim: ISO date `Edit`s |
| 4 | **Persistence + async loading.** `SqliteStore`, `settings.json` (`JsonSerializerContext`), startup load, cancellable detail load, lazy tree | restart keeps data and UI state; with a 300 ms fake store the UI stays responsive and a quick reselect never shows stale invoices | F9 with packages and generators (M). Interim: `dotnet run`/F5 instead of F9 |
| 5 | **Settings, file drop, import/export.** SettingsForm, logo drop, CSV drop with progress and cancel, HTML export | workflows 1, 7 and 8 pass; a cancelled import leaves the client count unchanged | none. **Then run the §4.4 hand check** |
| 6 | **M13 pass.** Mnemonics + `FocusControl` everywhere, `Application.Title`, placement save/restore, DPI rescale | Alt+letter tests on every form; MainForm at 100 → 150 → 100 % equals a fresh load (M13's own test, on Tally) | M13 |
| 7 | **M15 pass.** `Binder` replaces copy-in/copy-out, `Action` replaces `UpdateCommands` | the client, invoice-header and settings code has no per-field load/store methods left; tests show edit → Revert restores the values and edit → selection change asks first; menu, toolbar and context items follow action state | Binder (S–M), Action (S–M), OnIdle (M13) |
| 8 | **Ship.** NativeAOT on Windows and Linux (and macOS in CI), packaging incl. `e_sqlite3`, the newcomer walkthrough | M17's done-when, with Tally as "the reference app" | M17 packaging |

Stages 1–3 and 5 need nothing new and can start right away, interleaved with M13. Framework work then runs in this
order: menu fields and named forms (both S, unblock 0–1), keeping unknown classes (S), F9 widening (M, unblocks 4),
DateTimePicker (M, swaps into 3), then Binder and Action (stage 7).

### 5.1 Facts learned in stages 0–1 (added 2026-11-17)

- **Harness.** `tests/RadSharp.RefApp.Tests`: `DesignerDriver` (its header lists every verb's input path and every
  bypass), `DriverHost` (answers modal dialogs from a queue; an unexpected dialog fails the test with its text; raises
  `Shown` like the SDL host), recipes in `refapp/recipes/*.cs` compiled into the test project, and the guard
  `RebuildReferenceAppTests`. The committed app was made by the recipe itself (first `RADSHARP_REFAPP_WRITE=1` run copies
  everything File › New Application wrote; later runs copy forms and add missing handler stubs with
  `CodeIntegration.EnsureHandler`, never touching other code).
- **One RadSharp in the test process.** The tests never reference `refapp/Tally/Tally.csproj`: Tally uses
  `PackageReference`s, and mixing them with the tests' `ProjectReference`s would put two builds of `RadSharp.Core` (same
  name, different versions) in one process. Tally's forms run through F9, which compiles the sources in memory against the
  process's own RadSharp assemblies, so types match by construction. The package path is tested out of process
  (`TallyBuildTests`: `dotnet pack` Core and Platform.Sdl, then `dotnet build` Tally; opt-in with `RADSHARP_REFAPP_PACK=1`).
  Stage 4 (SQLite) can't use F9 until F9 loads packages; the fallback is compiling Tally's sources into a test assembly.
- **Packages.** The generators have no package of their own: `RadSharp.Core.nupkg` carries `RadSharp.Generators.dll` in
  `analyzers/dotnet/cs` and `RadSharp.Core.targets` in `build/` and `buildTransitive/`, so a project that references only
  `RadSharp.Platform.Sdl` still gets form generation. `RadProject.Create`'s `nuget.config` points at the folder typed in
  New Application (the designer defaults it to the repository's `artifacts/packages`) and puts restored packages in the
  project's own `packages/` folder.
- **Bugs met** (each needed an interim; framework fixes belong to other branches): the menu editor's onClick field writes
  no handler stub (menu handlers were typed into `MainForm.cs` by hand); New Application writes the package folder as an
  absolute path into `nuget.config` (hand-edited to `../../artifacts/packages` for the committed app); `Platform.Sdl`'s
  package depends on the Core version of its own build, so separate `dotnet pack` runs that straddle a minute disagree (the
  pack test passes one `-p:Version`).
- `refapp/Directory.Build.props` stops the repository's own props (version stamp, package output) from reaching Tally;
  it keeps `TreatWarningsAsErrors`.
- Not yet in MainForm: ImageList toolbar icons (F9 embeds only form files as resources, `LiveRunner.cs`, so an icon
  stored as an embedded resource would not load under F9: unverified), the PopupMenus, the client detail pane (stage 2),
  *By year* in the tree (stage 4).

**Added 2026-11-17, stages 2–3:**

- **Interims gone.** All menu handlers now come from the designer: the recipe double-clicks most items in the menu
  editor (`Item.Click`, which names them `<name>_Click`) and types the rest into onClick (stubs written when the editor
  closes). The guard checks that every handler a form file names was written into the recipe project's code-behind by
  the designer, and that the committed code has a method of that name with the same parameter types. The guard now runs
  New Application in `TestResults/Tally` (one folder below the root, like `refapp/Tally`), so `nuget.config` comes out
  with the committed relative path `../../artifacts/packages`; the guard compares it byte for byte and write-back copies
  it. `TallyBuildTests` packs with `pack.ps1`/`pack.sh`.
- **Stage 2** (client master/detail by hand): a bottom-aligned detail pane under the client list (11 fields, Save/Revert,
  the client's invoices), Client menu New/Save (Mod+S)/Revert/Delete, a `PopupMenu` on the list sharing the menu's
  handlers. Copy-in/copy-out for the client: 11 lines each way (each field appears twice in hand-written code, plus one
  shared `onChange` per control in the form file), and 55 lines in all with the re-entry guard, the dirty flag, the
  shared change handler and the leave prompt; keeping the list selection on the pane's client (Cancel puts it back)
  adds 24 more.
- **Stage 3** (InvoiceForm): made with File › New Form; DateTimePickers for the dates (no ISO edits needed); a
  StringGrid of lines (Columns editor, in-place editing, validation in `onSetEditText`, the last empty row adds a line),
  totals, `ShowModalAsync` from the main window, `CloseQuery` asking before discarding. Numbers are parsed and shown in
  the current culture without thousands separators (so `1,5` is refused in en-US instead of read as 15); tests pin en-US
  or de-DE.
- **Bugs met** (minimal repros were run on 2026-11-17; framework fixes belong to other branches):
  1. Typing a handler name into an Events row and pressing Enter writes no stub (only a double-click does): the form then
     names a missing method (RAD1001 at build). Interim: the driver double-clicks the row after typing. Medium.
  2. `Form.ModalResult` keeps the value of a close that `CloseQuery` refused (Delphi resets it to `mrNone`); a later close
     from the title bar or `Close()` then reports, e.g., Ok for an unsaved dialog. Not hit by Tally (Save sets the result in
     code after saving). Medium.
  3. Setting `ModalResult` inside a `CloseQuery` handler re-enters `Close()`: `CloseQuery` runs twice and `Closed` fires
     twice, so "Save changes? Yes/No/Cancel" that saves and closes from `CloseQuery` can't be written cleanly. Interim:
     InvoiceForm asks "Discard the changes?" (Yes/No) instead. Low–medium.
  4. `CodeIntegration.EnsureHandler` inserts `\n` line breaks into a file that uses `\r\n` (mixed line endings after the
     first stub on Windows checkouts) and leaves a line of only spaces in the stub body. Low.
  5. Still present, not hit: `itemIndex` set in the designer is lost on load (`FormWriter.Save` then `FormReader.Load` of
     a ComboBox with items and itemIndex 2 gives -1). Tally sets every combo's index in code.
- A menu item's onClick is `(MenuItem sender)` and a button's `(Control sender)`, so one command on a menu item, a toolbar
  button and a panel button needs one small handler per surface (Tally: `NewClient()`, `SaveClient()` called from two
  each); the context menu can share the main menu's handler. This is what `Action` (stage 7) removes.
- The test host checks that each answered dialog shows the expected caption or text, so a different dialog appearing
  first fails the test instead of receiving its answer.

---

## 6. Risks and open questions (decisions for you)

| # | Decision | Recommendation |
|---|---|---|
| 1 | **Storage:** SQLite (`Microsoft.Data.Sqlite`, a fourth native library of 1.8 MB, needs the F9 fix) or a single JSON document (no new dependency; works in F9 today only with hand-written `JsonDocument` code) | **SQLite.** It's what a real Delphi or WinForms user reaches for, it makes async loading genuine, and it forces the F9 and packaging paths to handle real packages before users find the gaps |
| 2 | **Where Tally lives:** `refapp/Tally` in this repo (made by File › New Application, consuming packed packages like a user), `samples/` with `ProjectReference`s, or its own repo | **`refapp/Tally` in this repo**, with a CI job that packs first, then builds and tests it. Samples use project references, which hides packaging problems |
| 3 | **Binding for 1.0:** the code-side `Binder` only, the full M15.3 (form-file syntax + generator + list binding), or nothing | **The code-side `Binder`.** It meets the "no copy-in/copy-out" bar at S–M; declarative binding moves to after 1.0 |
| 4 | **Frames out of 1.0**, even though `gap-analysis.md` rates reusable UI P0 (#9) | **Out**, but fix the data loss for project controls and put them on the palette (M). Tally's reuse need is met by copy/paste plus a helper. Revisit if a second app needs real frames |
| 5 | **Which designer builds Tally:** main-line `RadSharp.Designer`, or the unmerged `ide/main` branch (58 commits: embedded editor, language service, netcoredbg debugger) | **Main line**, with the external IDE. Decide the `ide/main` merge on its own; if it merges before stage 6, run the §4.4 hand check in it too |
| 6 | **Is a recipe-driven designer "the designer"** for M17's "built using only the designer plus code"? | **Yes**, provided the driver uses real input paths, every bypass is listed, and the §4.4 hand check passes. The byte-identical guard test is the proof |
| 7 | **Printing** | **Not in 1.0.** Export HTML and let the browser print; list it as a known limit. Real printing is per-OS and large |
| 8 | **M16's done-when** names localized flow/grid panels, which this cut removes | **Rewrite it** around what stays in: menu fields, F9 widening, the debugging story, and the alignment and palette tests that already exist |
| 9 | **App name** "Tally" | Fine as a working name; it's internal |

Real risks:
- **F9 widening is the deepest item.** Native assets (`e_sqlite3`) must load inside a collectible
  `AssemblyLoadContext` (`LoadUnmanagedDll`), and references and analyzers must come from the project's restore
  output (`obj/project.assets.json` or `dotnet build -getItem:ReferencePath;Analyzer`). Spike it first; if it slips,
  stage 4 still works with F5, so it doesn't block the app.
- **Project controls in the designer** mean compiling the project in the designer process and reloading open
  documents when it changes. That shares plumbing with F9, so build both together.
- **The driver could hide designer bugs** that only show with a real mouse (hover, popup placement, DPI). The §4.4
  hand check is the mitigation; keep it short so it actually gets done.

---

## 7. Size estimates for the recommended 1.0 pieces

| Piece | Source | Size |
|---|---|---|
| F9 with the project's packages, native assets, analyzers/generators and project references | M16 (widened) | **M** |
| Project controls on the palette; unknown classes kept as opaque nodes | M15.1 (part) + new | **M** (keep-unknown alone **S**) |
| `DateTimePicker` + `Calendar` (date only, goldens, designer palette icon) | M14.7 | **M** |
| `Binder<T>`, buffered two-way, with format/parse for decimal and date | M15.3 (reduced) | **S–M** |
| `Action` component, `action` link on Button/ToolButton/MenuItem, `onUpdate` on idle, form-format minor version + vector | M15.4 | **S–M** |
| Menu editor separator and `altShortcut` fields | M16 | **S** |
| New Form asks for a name; Project › Rename Form | new | **S** |
| `itemIndex` before `items` load bug | M14 known bug | **S** |
| Debugging story: launch/tasks files in `RadProject.Create`, a docs page | M16 | **S** |
| `DesignerDriver` + recipes + guard test (test infrastructure) | M17 | **S** |
| Tally itself, stages 0–7 (about 1.5k lines of app code, 4 forms) | M17 | **M** |

Out of 1.0, for reference: frames (M–L), visual inheritance (L), declarative and list binding (L), localization
(M), FlowPanel/GridPanel (M), AutoSize (S), zoom (S–M), generic collection editor (M), MaskEdit (S–M), printing (L).

---

## Appendix: experiments (2026-11-17, Windows x64, .NET SDK 10.0.404)

**A. NativeAOT with SQLite and System.Text.Json source generation.** A console app with `PublishAot`,
`EnableAotAnalyzer`, `EnableTrimAnalyzer` and `TrimmerSingleWarn=false`, referencing `Microsoft.Data.Sqlite`
10.0.0. It creates a table, inserts rows, runs a parameterized async query with an async reader, and round-trips the
result through a `[JsonSerializable]` context. `dotnet publish -c Release -r win-x64`: **no warnings**. Output
`SqliteAot.exe` 3.4 MB plus `e_sqlite3.dll` 1.8 MB; it ran and printed the rows (SQLite 3.49.1). Not yet run on
Linux or macOS.

**B. What compiles under F9.** A console app referencing `src/RadSharp.Design.Code`, so its trusted assemblies
match the designer's (BCL + RadSharp + Roslyn). It calls `RadProject.Create` and then `LiveSession.Compile` with one
extra source file:

| Extra file | Result |
|---|---|
| plain class | success |
| `JsonDocument.Parse(...)` | success |
| `[JsonSerializable] partial class AppJson : JsonSerializerContext` | **fails**: CS0534 (`GeneratedSerializerOptions`, `GetTypeInfo` not implemented), CS0117 (no `Default`); the STJ generator never runs |
| `new Microsoft.Data.Sqlite.SqliteConnection(...)` | **fails**: CS0234, the namespace doesn't exist (no package references in F9) |

Both scripts lived in the session scratchpad and are not part of the repo. The checks are small enough to become
real tests in `tests/RadSharp.Design.Code.Tests` when the F9 work starts.
