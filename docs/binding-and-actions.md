# Binding and actions

Two small pieces that remove the copy-in/copy-out and enable/disable code of an ordinary data form:

- **`Binder<T>`** (`RadSharp.Core`, code only): buffered two-way binding of edit controls to one object, through typed
  lambdas. No form-file syntax, no generator work, no list item-source binding (the 1.0 cut).
- **`Action`** (C# type `RadSharp.ActionItem`, a non-visual component): a light Delphi `TAction` that buttons, tool buttons and
  menu items link to by name.

Both are AOT- and trim-safe: no reflection, no expression trees, no runtime code generation.

## 1. `Binder<T>`

### 1.1 Shape

```csharp
var binder = new Binder<Client>();                                   // T is a class
binder.Bind(edtName,   c => c.Name,   (c, v) => c.Name = v)
      .Validate(v => v.Trim().Length > 0 ? null : "Enter a name.");
binder.Bind(memNotes,  c => c.Notes,  (c, v) => c.Notes = v);        // Edit or Memo: string
binder.Bind(chkActive, c => c.Active, (c, v) => c.Active = v);       // CheckBox / RadioButton: bool
binder.BindRadios(c => c.Kind, (c, v) => c.Kind = v,                 // one value per radio button
    (rbPerson, ClientKind.Person), (rbCompany, ClientKind.Company));
binder.Bind(spnSeats,  c => c.Seats,  (c, v) => c.Seats = v);        // SpinEdit: int
binder.Bind(trkRating, c => c.Rating, (c, v) => c.Rating = v);       // TrackBar: int
binder.Bind(cboCity,   c => c.City,   (c, v) => c.City = v);         // ComboBox: text
binder.BindIndex(cboTier, c => c.Tier, (c, v) => c.Tier = v);        // ComboBox: item index
binder.Bind(cboTerms,  c => c.TermsDays, (c, v) => c.TermsDays = v,  // ComboBox: item i <-> values[i]
    [14, 30, 60]);
binder.Bind(dtpSince,  c => c.Since,  (c, v) => c.Since = v);        // DateTimePicker: DateOnly
binder.Bind(dtpLast,   c => c.LastOrder, (c, v) => c.LastOrder = v); // DateTimePicker: DateOnly? (check box = no date)
binder.Bind(edtRate,   c => c.Rate,   (c, v) => c.Rate = v,          // text <-> decimal, culture-aware
    BindFormat.Decimal("0.00", CultureInfo.GetCultureInfo("de-DE")));

binder.Current = client;            // loads the controls; not dirty
binder.IsDirty                      // any control differs from what was loaded
binder.DirtyChanged += ...          // IsDirty flipped
if (!binder.Commit())               // validate every field, then write the edited ones (all or none)
    Dialogs.ShowMessage(binder.Errors[0].Message);
binder.Revert();                    // reload the controls from Current
```

Every `Bind` overload takes a getter and an optional setter (`Action<T, TValue>?`; without one the field is read-only:
shown, never dirty, never written) and returns a `BoundField<TValue>` (`Control`, `Controls`, `IsDirty`, `IsReadOnly`,
`Error`, and `Validate(Func<TValue, string?>)` for chained checks: return a message, or null when fine).
`binder.Fields` lists them.

`BindFormat<TValue>` is a format/parse pair for text boxes: `new BindFormat<TValue>(format, parse, errorMessage)`, plus
ready-made `BindFormat.Decimal`, `NullableDecimal` (empty text = null), `Int32` and `Double`, each with a .NET numeric
format string and a culture (default: the current culture, read at each format/parse).

### 1.2 What "buffered" means

**The controls are the buffer.** `T` is never cloned (it needn't be cloneable) and never written before `Commit`:

| Operation | What happens |
|---|---|
| `Current = x` | Every field is loaded from `x` (getter → format → control). Each field remembers the control's value right after loading (its *baseline*, read back from the control, so a control's own normalisation — clamping, `MaxLength` — never counts as an edit) and the model value it loaded. Unsaved edits are discarded, errors cleared. Setting the same object again reloads. |
| user edits | Nothing is written. A field is **dirty** while its control's raw value (text, checked state, index, date) differs from its baseline, so typing a change and typing it back makes the field clean again. `IsDirty` = any writable field dirty. |
| `Commit()` | **Every writable field is validated** (a field bound without a setter is not): a *dirty* field is parsed (text → value) and its parsed value checked by its validators; a *clean* one is parsed and checked as its control shows it (nothing is written), so a new record whose required `Name` is still empty doesn't save even if the user typed nothing there, and a value-mapped combo or a radio group showing no choice is an error, not a silent save of the old value. Parsing and writing still touch only the dirty fields. **All or nothing:** if any fails, nothing is written, `Errors` lists every problem (`BindingError`: `Field`, `Control`, `Message`), each field's `Error` is set, and the result is `false`. Bad input never throws. On success every dirty field's setter runs on the object that was `Current` when `Commit` started (even if a handler sets `Current` meanwhile), then each such control is reloaded from the model only if the text differs (so `1.5` becomes `1.50`, and an unchanged text keeps its caret), the fields the user didn't edit are re-read (a setter may have changed them), baselines are reset and the binder is clean. A setter that throws ends the commit with that exception: the setters before it have run (the binder can't undo them) and the binder stays dirty. With `Current == null`, `Commit` writes nothing and returns `true`. |
| `Validate()` | The validation half of `Commit`, writing nothing: the same fields, the same `Errors`. |
| `Revert()` | Same as `Current = Current`. |
| `FocusFirstError()` | Focuses the control of `Errors[0]`. |

Cross-field rules (end date after start date) belong in the Save handler, after `Validate()` or around `Commit()`.
Immutable models (positional records with `init` properties) can't be written through `Action<T, TValue>`; bind a
mutable edit model instead.

### 1.3 Control-specific rules

- **SpinEdit** is bound by its *text*, so a number typed but not yet confirmed (the spin edit takes typed text only on Enter
  or when it loses focus) is what `Commit` writes — a toolbar button or a shortcut doesn't move focus. Text that isn't a
  whole number, or is outside `MinValue..MaxValue` (when limits are set), is a validation error.
- **ComboBox**: text binding uses the edit's text in a `DropDown` combo and the chosen item in a `DropDownList`
  (a value that is no item selects nothing). The mapped overload treats item *i* as `values[i]`; a model value not in the
  map selects nothing, and committing with nothing chosen is an error.
- **RadioButton**: `Bind(radio, bool)` treats one button like a check box. `BindRadios` treats a group as one value; the
  buttons need not share a parent (the binder unchecks the others itself, on load and when the user checks one). None checked at commit is an error.
- **DateTimePicker**: `DateOnly` uses the shown date; an unchecked check box at commit is an error. `DateOnly?` maps
  null to "check box shown and unchecked".
- **TrackBar** clamps to its range; the clamped position is the baseline.

### 1.4 `Current == null`

Every bound control is cleared (text empty, unchecked, the bar's minimum, no combo selection, a check-box date picker
unchecked, a spin edit empty) and **disabled**. When `Current` becomes non-null again the binder re-enables only the
controls *it* disabled, so a control the application disabled itself stays disabled. `IsDirty` is false.

### 1.5 Change events and the user's handlers

The binder subscribes to the controls' own change events (`Change`; for a spin edit also `onValueChange`) like any
other handler; it never replaces or suppresses them.

- When the binder sets a control (load, revert, refresh, reformat after commit), the control raises `Change` as it
  does for any change made from code. `binder.IsLoading` is true meanwhile, so a handler can skip those.
- Generated `InitializeComponent()` subscribes form-file handlers before the form's constructor creates the binder, so
  user handlers run first. That doesn't matter: **`IsDirty` (and each field's `IsDirty`) is computed when read**, so it
  is correct inside any handler. `DirtyChanged` is raised by the binder's own handler, from within the control's
  `Change`, after it.

### 1.6 `INotifyPropertyChanged`

If `T` implements `INotifyPropertyChanged`, the binder listens to `Current` (and stops when `Current` changes or the
binder is disposed). The rule on a `PropertyChanged` event (any property name; the binder doesn't parse names, it
re-reads every getter, which is cheap and works for computed properties):

- A field whose getter now returns a different value than it loaded, and **that the user hasn't edited**, is reloaded
  (the control shows the new value; the field stays clean).
- A field **the user has edited keeps the edit**: the control isn't touched, the new model value becomes the field's
  baseline (so the field is dirty unless the edit happens to equal it), and `Commit` writes the user's value over the
  outside change (last writer wins). `Revert` shows the new value.
- Events raised by the binder's own `Commit` are ignored; after writing, `Commit` re-reads the fields the user didn't edit, so a setter that changes other properties shows up.

`Refresh()` applies the same rule on demand, for models that don't raise the event. Raise `PropertyChanged` on the UI
thread (marshal with `Application.Queue` otherwise).

`Dispose()` ends the binder for good: it unsubscribes from the controls, the model and the owner form (the controls keep what
they show), and afterwards setting `Current`, `Commit`, `Validate`, `Revert` and `Refresh` throw `ObjectDisposedException`.
Disposing twice is safe. Create the binder with its form (`new Binder<Client>(this)`, or `new Binder<Client>(this, client)`)
and the form's lifetime is handled without ending the binder: when the form closes (`Closed`) the binder stops listening to
the model, so a closed dialog can't keep a long-lived model's `PropertyChanged` alive, and when the form is shown again
(`Shown`) it listens again. A closed form doesn't listen, so setting `Current` on a closed dialog subscribes nothing until it
is shown. A dialog can therefore be created once and shown many times (the Delphi pattern): set `Current` before each
`ShowModal`, `Commit` on OK. On `Shown` the controls are reloaded from the model only when `Current` was *not* set since the
form last closed (the same record shown again drops an edit left from a cancelled show); after a new `Current`, values the
code or the form's own `onShow` put into the controls survive, and only untouched fields the model changed are refreshed. The constructors are `Binder<T>()` and `Binder<T>(Form owner)` (plus `(owner, current)`); to start with a model
without an owner write `new Binder<T> { Current = model }`. Dispose a binder with no owner yourself.

Every getter is read before any control changes: if one throws, the exception propagates and the controls, `Current` and the
model subscription stay as they were.

## 2. `Action`

### 2.1 The component

`Action` is a non-visual `Component`, like `Timer` and `ImageList`: it lives in the form file as a child of the form, gets
a generated field, and sits in the designer's component tray. Its C# type is `RadSharp.ActionItem` because a
`RadSharp.Action` would collide with `System.Action` in every file that has `using RadSharp;` (and inside RadSharp
itself); form files, the palette and the component registry say `Action` (like Delphi's `TAction`), and code rarely needs the
type name. The `[Component("Action")]` attribute on the class publishes it under that name: the metadata generator registers it as
`Action`, and the form generator resolves `"class": "Action"` to `ActionItem` and emits `global::RadSharp.ActionItem` fields.

| Member | Form file | Meaning |
|---|---|---|
| `Caption` | `caption` | Shown by linked controls; `&` marks a mnemonic. |
| `Hint` | `hint` | Tooltip of linked buttons. |
| `Shortcut` | `shortcut` | `"Mod+S"`, `"F5"` (spec, Appendix C). |
| `Enabled` | `enabled` | Default true. |
| `Checked` | `checked` | Menu check mark, tool button `down`. |
| `Visible` | `visible` | Default true. |
| `ImageIndex` | `imageIndex` | Default -1; the control's own (or its tool bar's) image list supplies the picture. |
| `AutoCheck` | `autoCheck` | Running toggles `Checked` first. |
| `Executed` | `onExecute` | The command. Default event (double-click in the designer). |
| `Update` | `onUpdate` | Set `Enabled`/`Checked`/`Caption` from application state. |

Methods: `Execute()` (raises `onUpdate`, then — if enabled — toggles `Checked` when `AutoCheck`, then raises
`onExecute`; returns false if disabled or without a handler), `UpdateState()`. While `onExecute` runs,
`ActionComponent` is the button or `MenuItem` that ran it (null for a shortcut or code). `Clients` lists the linked
controls. `Form.UpdateActions()` runs every action's `onUpdate` on that form.

**Why standalone actions, not an `ActionList`:** Delphi groups actions in a `TActionList` mainly for categories, a shared
image list and the action-list editor. For the light 1.0 version a list adds a container component, a collection editor
and a nested form-file shape for no behaviour the reference app needs; standalone actions reuse what components already
have (tray, inspector, generated fields, handlers). A later `ActionList` can be added as a grouping component without
changing how controls link (by action name). The palette puts `Action` under **Standard**, where Delphi has `TActionList`.

### 2.2 Linking

`Button` (and so `ToolButton`) gains a published `action` (`ActionName`), like `popupMenu`: the name of an `Action` on the
same form. Menu items gain `"action"` in their item objects (`MenuItem.ActionName`). From code, `button.Action = act` /
`item.Action = act` links directly (to an action on any form). Linked members:

| Action | Button | ToolButton | MenuItem |
|---|---|---|---|
| Caption | caption | caption (drawn without the `&`) | caption |
| Hint | hint | hint | — |
| Enabled, Visible | enabled, visible | enabled, visible | enabled, visible |
| Checked | — | down | checked |
| ImageIndex | imageIndex | imageIndex | — |
| Shortcut | — | — | shortcut (shown, and matched by the menu bar) |

Semantics (Delphi's):

- **Linking copies** the action's values to the control. Linking from code (`Action = a`, or `ActionName = "actSave"` on a
  button or menu item) or in the Object Inspector copies all of them. A link made while a form **loads** (the name is resolved after the file is read) copies only over values that are
  still the class default (or a caption equal to the control's name), so a value the form file sets explicitly wins.
- **Later changes propagate** to a control *while its value still equals the action's old value* — that equality is
  what "linked" means per member. A control whose caption was set to something else keeps it.
- **Stored only when it differs:** the form writer skips a linked member whose value equals the action's (it comes back
  from the action on load). So a button linked to `actSave` saves just `"action": "actSave"` unless it overrides
  something. Save → load → save stays byte-identical. Only links that loading can restore count: a
  control linked from code to another form's action saves its own values (loading can't get them back from there).
- **Code links follow their client.** A link made from code (`btn.Action = other.actSave`) lists the client in
  `Action.Clients` only while the client is on an open form: the action drops it when the client's form closes or the client
  (or the menu bar holding a menu item) is removed from its form, and links it again, taking the action's current values,
  when the form is shown again or the client is added back. The `Action` reference itself stays, so a dialog created once
  and shown many times needs no re-linking, and a closed or removed client is never kept alive by the action or written to.
  (A menu item taken out of a menu's `Items` list is only noticed when its menu leaves the form.)
- **Shortcuts** run the first enabled action with a handler; a key no action ran is not marked handled.
- **Idle updates** (`onUpdate`) run on the thread that made the form, for shown, visible forms; the idle list holds forms
  weakly, so a form the host dropped without closing can be collected.
- **Clicking** a linked button or tool button, choosing a linked menu item, or its mnemonic runs `action.Execute()`.
  If code subscribed to the control's own `Click` (e.g. an `onClick` in the form file), that runs *instead* (Delphi's
  rule). A button's `modalResult` still applies.
- A linked tool button with `allowToggle` doesn't toggle itself; its `down` follows the action's `Checked` (use
  `autoCheck`).
- Renaming the action keeps the link (the control's `ActionName` reports the new name). Removing the action from the form
  drops the links (the controls keep what they show, and the name, like a dangling `popupMenu`). Unlinking
  (`ActionName = ""`) keeps the values too.

Names are resolved lazily by the form, only when something changed (a control or component added or removed, or a name
set that couldn't be resolved yet): on its next layout, key press, idle update or `UpdateActions()`. Forms without actions
skip the walk entirely.

### 2.3 Update while idle

A form that has been shown (and not closed) and has actions joins one `Application.Idle` handler, which runs
`UpdateActions()` for each such form that is visible and not blocked by a modal form (Delphi doesn't update disabled windows;
the host answers through `IPlatformServices.IsFormBlocked`) whenever the host drains its input queue. A form leaves the list
on every close path: `Form.Close`, and a host taking the window away without it (`SdlHost.CloseForm`, host shutdown) through
`Form.NotifyHostClosed()`, so the list never keeps a form alive. The handler never sets `Done = false`,
so it doesn't keep the host awake: updates happen after input, timers and posted work, as in Delphi. `Execute()` also
runs `onUpdate` first (a shortcut runs it once, then executes), so a shortcut or click never runs a command that just became disabled. An exception in one
`onUpdate` is reported (`Application.ThreadException`) and the other actions still update. Keep `onUpdate` cheap.

### 2.4 Shortcuts and mnemonics

After the focused control and the menu bar had the key (spec, Appendix C: a modifier or F1–F12), the form offers it to
its actions in tree order: the first whose `shortcut` matches runs if it is enabled after `onUpdate`; a disabled match
lets the key go on. Hidden actions' shortcuts work (Delphi's). A menu item linked to an action shows the action's
shortcut, and the menu bar matches it first — the command runs once either way. Popup-menu items don't process
shortcuts themselves, so their actions' shortcuts are what make those commands reachable from the keyboard.
Mnemonics come with the caption: `&Save` on the action gives the linked button Alt+S and the menu item its access key.

### 2.5 Designer

`Action` is on the palette (Standard) with its own icon, lands in the component tray, double-clicks to an `onExecute`
handler, and its properties are under the **Action** category. In the Object Inspector the `action` row of a button lists
the form's actions (and `(none)`); `popupMenu` and `images` rows list popup menus and image lists the same way. Which rows do
is explicit, not by name: a published string property marked `[Published("action", References = typeof(ActionItem))]` (the
metadata generator emits it as `PropertyMeta.ReferenceType`; no reflection) gets the list. Like Delphi's combo the row also takes
typed text: the list sits under a text box, so a name that isn't on the form yet can be entered (Enter or a click elsewhere keeps
it, Escape drops it, an empty box clears the reference). A linked menu item repaints the menu bar and any open dropdown when its
action changes its caption, shortcut, enabled, checked or visible state.
Changing an action's caption, enabled state... in the inspector updates its linked controls on the surface at once.

## 3. Master/detail without copy-in/copy-out

```csharp
public partial class ClientsForm : Form
{
    readonly List<Client> _clients = Client.Samples();
    readonly Binder<Client> _binder;   // owned by the form: disposed when it closes

    public ClientsForm()
    {
        InitializeComponent();
        _binder = new Binder<Client>(this);
        _binder.Bind(edtName, c => c.Name, (c, v) => c.Name = v).Validate(v => v.Trim().Length > 0 ? null : "Enter a name.");
        _binder.Bind(edtRate, c => c.Rate, (c, v) => c.Rate = v, BindFormat.Decimal("0.00"));
        lstClients.Items = [.. _clients.Select(c => c.Name)];
        lstClients.ItemIndex = 0;
    }

    void lstClients_Change(Control sender) =>
        _binder.Current = lstClients.ItemIndex >= 0 ? _clients[lstClients.ItemIndex] : null;

    void actSave_Update(Control sender) => actSave.Enabled = _binder.IsDirty;   // the Save action is enabled by the dirty flag
    void actSave_Execute(Control sender)
    {
        if (!_binder.Commit()) { _binder.FocusFirstError(); return; }
        lstClients.Items = [.. _clients.Select(c => c.Name)];
    }
    void actRevert_Execute(Control sender) => _binder.Revert();
}
```

The form file holds `actSave` (`"caption": "&Save"`, `"shortcut": "Mod+S"`) and the button, tool button and menu item
that link to it with `"action": "actSave"`. `samples/ClientsDemo` is this, complete; the test
`BindingMasterDetailTests` drives it through simulated input.
