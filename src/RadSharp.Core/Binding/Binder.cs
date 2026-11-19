using System.ComponentModel;

namespace RadSharp;

/// <summary>
/// Buffered two-way binding of controls to one object (docs/binding-and-actions.md). Each <c>Bind</c> call ties a control to
/// a property through typed lambdas (no reflection). Setting <see cref="Current"/> loads the controls; the user's edits stay
/// in the controls (they are the buffer: <typeparamref name="T"/> is never cloned or written early) until
/// <see cref="Commit"/> parses, validates and writes them through the setters, or <see cref="Revert"/> reloads them.
/// <see cref="IsDirty"/> says whether any bound control now differs from what was loaded, and <see cref="DirtyChanged"/>
/// reports when that flips. With <see cref="Current"/> null every bound control is cleared and disabled.
/// </summary>
/// <example>
/// <code>
/// var binder = new Binder&lt;Client&gt;();
/// binder.Bind(edtName, c => c.Name, (c, v) => c.Name = v);
/// binder.Bind(chkActive, c => c.Active, (c, v) => c.Active = v);
/// binder.Bind(edtRate, c => c.Rate, (c, v) => c.Rate = v, BindFormat.Decimal("0.00"));
/// binder.Current = clients[0];
/// // Save:   if (!binder.Commit()) ShowMessage(binder.Errors[0].Message);
/// // Cancel: binder.Revert();
/// </code>
/// </example>
/// <remarks>
/// <para>Create it with the owner form (<c>new Binder&lt;T&gt;(this)</c>) and it stops listening to the model when that form
/// closes and listens again (reloading the controls) when it is shown again, so a dialog created once can be shown for many
/// records: set <see cref="Current"/> before each show. Without an owner, or to end the binder for good, call
/// <see cref="Dispose"/>; a binder nobody disposes keeps listening to its model.</para>
/// <para>When <typeparamref name="T"/> implements <see cref="INotifyPropertyChanged"/>, a change made to <see cref="Current"/>
/// elsewhere reloads every field the user hasn't edited; a field the user has edited keeps the edit (and <see cref="Commit"/>
/// writes it over the outside change). <see cref="Refresh"/> does the same on demand for other models. Raise
/// <c>PropertyChanged</c> on the UI thread.</para>
/// <para>Loading sets the controls from code, so their <c>Change</c> events fire as for any code change; handlers can test
/// <see cref="IsLoading"/>. <see cref="IsDirty"/> is computed when read, so it is already correct inside a control's own
/// <c>Change</c> handler, whichever runs first.</para>
/// </remarks>
public sealed class Binder<T> : IDisposable where T : class
{
    readonly List<BoundField> _fields = [];
    readonly System.Action _changed;
    List<BindingError> _errors = [];
    T? _current;
    int _loadDepth;   // nested loads (a handler may touch the model while one runs)
    bool _committing, _lastDirty, _disposed;
    readonly Form? _owner;
    readonly NotifyEvent? _ownerClosed, _ownerShown;
    INotifyPropertyChanged? _listening;   // the model whose PropertyChanged this binder is subscribed to

    public Binder() => _changed = OnControlChanged;

    public Binder(T? current) : this() => Current = current;

    /// <summary>
    /// A binder tied to <paramref name="owner"/>'s lifetime without being used up by it: when the form closes
    /// (<see cref="Form.Closed"/>) the binder stops listening to the model, so a closed dialog can't keep a record alive or hear
    /// it, and when the form is shown again (<see cref="Form.Shown"/>) it listens again and reloads the controls from
    /// <see cref="Current"/>. A dialog can therefore be created once and shown many times, with <see cref="Current"/> set
    /// before each show. Only an explicit <see cref="Dispose"/> ends the binder.
    /// </summary>
    public Binder(Form owner) : this()
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _ownerClosed = _ => Unlisten();
        _ownerShown = _ => { if (_disposed) return; Listen(); LoadAll(); };
        owner.Closed += _ownerClosed;
        owner.Shown += _ownerShown;
    }

    /// <summary>Like <see cref="Binder(Form)"/>, and starts with <paramref name="current"/> as <see cref="Current"/>.</summary>
    public Binder(Form owner, T? current) : this(owner) => Current = current;

    /// <summary>
    /// The object being edited. Setting it (even to the same object) loads every bound control from it and discards unsaved
    /// edits (check <see cref="IsDirty"/> first); null clears and disables the controls. Throws
    /// <see cref="ObjectDisposedException"/> once the binder is disposed.
    /// </summary>
    public T? Current
    {
        get => _current;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Unlisten();
            _current = value;
            Listen();
            LoadAll();
        }
    }

    /// <summary>True if any bound (writable) control differs from the value loaded into it (false while the binder is loading).</summary>
    public bool IsDirty => _loadDepth == 0 && _current != null && _fields.Any(f => f.IsDirty);

    /// <summary>True while the binder itself sets controls (loading, reverting, refreshing after a commit).</summary>
    public bool IsLoading => _loadDepth > 0;

    /// <summary>Raised when <see cref="IsDirty"/> flips (the user edits a field, or a load or commit makes the controls clean).</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>The problems the last <see cref="Commit"/> or <see cref="Validate"/> found; empty when it succeeded.</summary>
    public IReadOnlyList<BindingError> Errors => _errors;

    /// <summary>The bound fields, in the order they were bound.</summary>
    public IReadOnlyList<BoundField> Fields => _fields;

    // ---- Binding ----

    /// <summary>Binds an <see cref="Edit"/> or <see cref="Memo"/>'s text. Without a setter the field is read-only (never dirty).</summary>
    public BoundField<string> Bind(EditBase edit, Func<T, string> get, Action<T, string>? set = null) =>
        Add(new TextAdapter(edit), get, set, static v => v ?? "", static (string raw, out string v, out string? e) => { v = raw; e = null; return true; });

    /// <summary>Binds an edit's text to a value of another type through <paramref name="format"/> (e.g. <see cref="BindFormat.Decimal"/>).</summary>
    public BoundField<TValue> Bind<TValue>(EditBase edit, Func<T, TValue> get, Action<T, TValue>? set, BindFormat<TValue> format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Add(new TextAdapter(edit), get, set, format.Format, format.TryParse);
    }

    /// <summary>Binds a <see cref="CheckBox"/> or <see cref="RadioButton"/>'s <see cref="ToggleControl.Checked"/>.</summary>
    public BoundField<bool> Bind(ToggleControl box, Func<T, bool> get, Action<T, bool>? set = null) =>
        Add(new ToggleAdapter(box), get, set, static v => v, static (bool raw, out bool v, out string? e) => { v = raw; e = null; return true; });

    /// <summary>
    /// Binds a group of radio buttons to one value: each button stands for one value (an enum member, typically). A value no
    /// button stands for leaves them all unchecked; committing with none checked is a validation error.
    /// </summary>
    public BoundField<TValue> BindRadios<TValue>(Func<T, TValue> get, Action<T, TValue>? set, params (RadioButton Button, TValue Value)[] options)
    {
        if (options.Length == 0) throw new ArgumentException("Give at least one radio button.", nameof(options));
        var values = options.Select(o => o.Value).ToArray();
        return Add(new RadioAdapter([.. options.Select(o => o.Button)]), get, set,
            v => Array.FindIndex(values, x => EqualityComparer<TValue>.Default.Equals(x, v)),
            (int raw, out TValue v, out string? e) =>
            {
                bool ok = raw >= 0 && raw < values.Length;
                v = ok ? values[raw] : default!;
                e = ok ? null : "Choose one of the options.";
                return ok;
            });
    }

    /// <summary>Binds a <see cref="SpinEdit"/>'s number, including text typed but not yet confirmed; outside its limits is an error.</summary>
    public BoundField<int> Bind(SpinEdit spin, Func<T, int> get, Action<T, int>? set = null) =>
        Add(new SpinAdapter(spin), get, set, static v => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string raw, out int v, out string? e) => SpinAdapter.TryParse(spin, raw, out v, out e));

    /// <summary>Binds a <see cref="TrackBar"/>'s <see cref="TrackBar.Position"/> (values outside its range are clamped by the bar).</summary>
    public BoundField<int> Bind(TrackBar bar, Func<T, int> get, Action<T, int>? set = null) =>
        Add(new TrackAdapter(bar), get, set, static v => v, static (int raw, out int v, out string? e) => { v = raw; e = null; return true; });

    /// <summary>
    /// Binds a <see cref="ComboBox"/>'s text: the typed text of an editable box, or the chosen item of a list (a value that is
    /// no item selects nothing).
    /// </summary>
    public BoundField<string> Bind(ComboBox box, Func<T, string> get, Action<T, string>? set = null) =>
        Add(new ComboTextAdapter(box), get, set, static v => v ?? "", static (string raw, out string v, out string? e) => { v = raw; e = null; return true; });

    /// <summary>Binds a <see cref="ComboBox"/>'s <see cref="ComboBox.ItemIndex"/> (-1: nothing chosen).</summary>
    public BoundField<int> BindIndex(ComboBox box, Func<T, int> get, Action<T, int>? set = null) =>
        Add(new ComboIndexAdapter(box), get, set, static v => v, static (int raw, out int v, out string? e) => { v = raw; e = null; return true; });

    /// <summary>
    /// Binds a <see cref="ComboBox"/> to a value through a map: item <c>i</c> stands for <c>values[i]</c> (the items show the
    /// text, e.g. "Net 30" for 30). A value not in the map selects nothing; committing with nothing chosen is an error.
    /// </summary>
    public BoundField<TValue> Bind<TValue>(ComboBox box, Func<T, TValue> get, Action<T, TValue>? set, IReadOnlyList<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var map = values.ToArray();
        return Add(new ComboIndexAdapter(box), get, set,
            v => Array.FindIndex(map, x => EqualityComparer<TValue>.Default.Equals(x, v)),
            (int raw, out TValue v, out string? e) =>
            {
                bool ok = raw >= 0 && raw < map.Length;
                v = ok ? map[raw] : default!;
                e = ok ? null : "Choose a value from the list.";
                return ok;
            });
    }

    /// <summary>Binds a <see cref="DateTimePicker"/> to a date; an unchecked check box (no date) is a validation error.</summary>
    public BoundField<DateOnly> Bind(DateTimePicker picker, Func<T, DateOnly> get, Action<T, DateOnly>? set = null) =>
        Add(new DateAdapter(picker), get, set, static v => v, static (DateOnly? raw, out DateOnly v, out string? e) =>
        {
            v = raw ?? default;
            e = raw == null ? "Enter a date." : null;
            return raw != null;
        });

    /// <summary>Binds a <see cref="DateTimePicker"/> to an optional date: null turns its check box on and unchecks it.</summary>
    public BoundField<DateOnly?> Bind(DateTimePicker picker, Func<T, DateOnly?> get, Action<T, DateOnly?>? set = null) =>
        Add(new DateAdapter(picker), get, set, static v => v, static (DateOnly? raw, out DateOnly? v, out string? e) => { v = raw; e = null; return true; });

    BoundField<TValue> Add<TValue, TRaw>(ControlAdapter<TRaw> adapter, Func<T, TValue> get, Action<T, TValue>? set,
        Func<TValue, TRaw> toRaw, RawParser<TRaw, TValue> parse)
    {
        ArgumentNullException.ThrowIfNull(get);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var field = new Field<T, TValue, TRaw>(adapter, get, set, toRaw, parse);
        _fields.Add(field);
        field.Attach(_changed);
        _loadDepth++;
        try
        {
            if (_current != null) field.Load(_current);
            else field.Clear();
            field.SetAvailable(_current != null);
        }
        finally { _loadDepth--; }
        CheckDirty();
        return field;
    }

    // ---- Loading and saving ----

    /// <summary>Reloads every bound control from <see cref="Current"/>, discarding the user's edits and errors.</summary>
    public void Revert()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LoadAll();
    }

    /// <summary>
    /// Re-reads <see cref="Current"/> into every field the user hasn't edited (what a <c>PropertyChanged</c> event does
    /// automatically): for models that don't raise it, after changing them in code.
    /// </summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current == null) return;
        _loadDepth++;
        try { foreach (var f in _fields) f.Refresh(_current); }
        finally { _loadDepth--; }
        CheckDirty();
    }

    /// <summary>
    /// Validates every writable field without writing anything: an edited field is parsed and its value checked, a clean one is
    /// parsed and checked as shown (so a new record with an empty required name fails too). Returns false and fills <see cref="Errors"/> if
    /// any is invalid.
    /// </summary>
    public bool Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Prepare() is not null;
    }

    /// <summary>
    /// Writes the edited fields to <see cref="Current"/> through their setters, all or nothing: every writable field is validated
    /// first (an edited one is parsed and checked, a clean one is parsed and checked as shown), and if any fails nothing is written,
    /// <see cref="Errors"/> lists the problems and the result is false (nothing throws). On success the controls show the stored
    /// values (formatted again) and are clean. Fields the user
    /// didn't edit are not written (they are re-read, in case a setter changed them). With <see cref="Current"/> null there is
    /// nothing to write and the result is true. "All or nothing" is about validation: a setter that throws ends the commit with
    /// its exception after the setters before it have run.
    /// </summary>
    public bool Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current == null) { SetErrors([]); return true; }
        if (Prepare() is not { } dirty) return false;
        if (dirty.Count == 0) return true;
        var target = _current;   // a handler may set Current while the setters run: the edits still go to this object
        _committing = true;
        try { foreach (var f in dirty) f.WritePending(target); }
        finally { _committing = false; }
        if (!ReferenceEquals(_current, target)) return true;   // that handler loaded the other object already
        _loadDepth++;
        try
        {
            // The edited fields show the stored values; the others are re-read, since a setter may have changed them too.
            foreach (var f in _fields)
                if (dirty.Contains(f)) f.AfterCommit(target);
                else f.Refresh(target);
        }
        finally { _loadDepth--; }
        CheckDirty();
        return true;
    }

    /// <summary>Focuses the control of the first error from the last <see cref="Commit"/> or <see cref="Validate"/>.</summary>
    public bool FocusFirstError() => _errors.Count > 0 && _errors[0].Control.Focus();

    /// <summary>
    /// Every writable field validated (an edited one parsed and checked, a clean one parsed and checked as shown); returns the edited
    /// ones, or null (and <see cref="Errors"/> filled) if any failed.
    /// </summary>
    List<BoundField>? Prepare()
    {
        if (_current == null) { SetErrors([]); return []; }
        var dirty = new List<BoundField>();
        var errors = new List<BindingError>();
        foreach (var f in _fields)
        {
            if (f.IsReadOnly) { f.Error = null; continue; }
            if (!f.IsDirty)
            {
                // A clean field is checked too, against the value it loaded: a new record's empty required name must not save.
                if (f.CheckLoaded(out var loadedMessage)) continue;
                errors.Add(new BindingError(f, loadedMessage ?? "The value isn't valid."));
                continue;
            }
            if (f.Prepare(out var message)) dirty.Add(f);
            else errors.Add(new BindingError(f, message ?? "The value isn't valid."));
        }
        SetErrors(errors);
        return errors.Count == 0 ? dirty : null;
    }

    void SetErrors(List<BindingError> errors) => _errors = errors;

    void LoadAll()
    {
        if (_disposed) return;
        _loadDepth++;
        try
        {
            foreach (var f in _fields)
            {
                if (_current != null) f.Load(_current);
                else f.Clear();
                f.SetAvailable(_current != null);
            }
        }
        finally { _loadDepth--; }
        _errors = [];
        CheckDirty();
    }

    void Listen()
    {
        if (_listening != null || _disposed) return;
        if (_current is INotifyPropertyChanged model)
        {
            model.PropertyChanged += OnModelPropertyChanged;
            _listening = model;
        }
    }

    void Unlisten()
    {
        if (_listening == null) return;
        _listening.PropertyChanged -= OnModelPropertyChanged;
        _listening = null;
    }

    void OnControlChanged()
    {
        if (_loadDepth > 0 || _disposed) return;
        CheckDirty();
    }

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_committing || !ReferenceEquals(sender, _current)) return;
        Refresh();
    }

    void CheckDirty()
    {
        bool now = IsDirty;
        if (now == _lastDirty) return;
        _lastDirty = now;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ends the binder for good: stops listening to the controls, the model and the owner form (the controls keep what they
    /// show). Afterwards <see cref="Current"/> can't be set and <see cref="Commit"/>, <see cref="Validate"/>,
    /// <see cref="Revert"/> and <see cref="Refresh"/> throw <see cref="ObjectDisposedException"/>. Safe to call twice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in _fields) f.Detach();
        if (_owner != null && _ownerClosed != null) _owner.Closed -= _ownerClosed;
        if (_owner != null && _ownerShown != null) _owner.Shown -= _ownerShown;
        Unlisten();
    }
}

/// <summary>A problem <see cref="Binder{T}.Commit"/> found in one field: a value that doesn't parse or fails its validator.</summary>
public sealed class BindingError(BoundField field, string message)
{
    public BoundField Field { get; } = field;
    public string Message { get; } = message;
    /// <summary>The (first) control of the field, e.g. to focus it.</summary>
    public Control Control => Field.Control;
    public override string ToString() => Message;
}

/// <summary>One binding made by a <c>Bind</c> call on a <see cref="Binder{T}"/>.</summary>
public abstract class BoundField
{
    private protected BoundField() { }

    /// <summary>The bound control (the first of a radio group).</summary>
    public abstract Control Control { get; }

    /// <summary>Every control of the field (one, or a radio group).</summary>
    public abstract IReadOnlyList<Control> Controls { get; }

    /// <summary>True if the user changed the control since it was loaded (always false for a field without a setter).</summary>
    public abstract bool IsDirty { get; }

    /// <summary>Bound without a setter: shown but never written.</summary>
    public abstract bool IsReadOnly { get; }

    /// <summary>The field's problem from the last commit or validation, or null.</summary>
    public string? Error { get; internal set; }

    internal abstract void Load(object model);
    internal abstract void Clear();
    internal abstract void Refresh(object model);
    internal abstract bool Prepare(out string? error);
    internal abstract bool CheckLoaded(out string? error);
    internal abstract void WritePending(object model);
    internal abstract void AfterCommit(object model);
    internal abstract void SetAvailable(bool available);
    internal abstract void Attach(System.Action changed);
    internal abstract void Detach();
}

/// <summary>A binding of a value of type <typeparamref name="TValue"/>; add checks with <see cref="Validate"/>.</summary>
public abstract class BoundField<TValue> : BoundField
{
    private protected Func<TValue, string?>? Validator;

    private protected BoundField() { }

    /// <summary>
    /// Adds a check run on commit: return an error message, or null when the value is fine. Several checks run in order and
    /// the first message wins. Returns this field, for chaining.
    /// </summary>
    public BoundField<TValue> Validate(Func<TValue, string?> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        var previous = Validator;
        Validator = previous == null ? validator : v => previous(v) ?? validator(v);
        return this;
    }
}

internal delegate bool RawParser<TRaw, TValue>(TRaw raw, out TValue value, out string? error);

/// <summary>A field: a model property (get/set) on one side, a control's raw value on the other, and the value loaded last.</summary>
internal sealed class Field<TModel, TValue, TRaw>(
    ControlAdapter<TRaw> adapter, Func<TModel, TValue> get, Action<TModel, TValue>? set,
    Func<TValue, TRaw> toRaw, RawParser<TRaw, TValue> parse) : BoundField<TValue>
{
    static readonly EqualityComparer<TRaw> RawEq = EqualityComparer<TRaw>.Default;
    static readonly EqualityComparer<TValue> ValueEq = EqualityComparer<TValue>.Default;

    TRaw _baseline = default!;     // the control's raw value right after the last load
    TValue _loaded = default!;     // the model value loaded last
    TValue _pending = default!;    // parsed by Prepare, written by WritePending
    readonly List<Control> _disabled = [];
    NotifyEvent? _handler;

    public override Control Control => adapter.Controls[0];
    public override IReadOnlyList<Control> Controls => adapter.Controls;
    public override bool IsReadOnly => set == null;
    public override bool IsDirty => set != null && !RawEq.Equals(adapter.Read(), _baseline);

    internal override void Load(object model)
    {
        var v = get((TModel)model);
        adapter.Write(toRaw(v));
        _baseline = adapter.Read();   // read back: the control may normalize (clamp, cut to MaxLength...)
        _loaded = v;
        Error = null;
    }

    internal override void Clear()
    {
        adapter.Clear();
        _baseline = adapter.Read();
        _loaded = default!;
        Error = null;
    }

    internal override void Refresh(object model)
    {
        var v = get((TModel)model);
        if (ValueEq.Equals(v, _loaded)) return;
        _loaded = v;
        if (!IsDirty)
        {
            adapter.Write(toRaw(v));
            _baseline = adapter.Read();
        }
        else _baseline = toRaw(v);   // keep the user's edit; it is dirty against the new value
    }

    internal override bool Prepare(out string? error)
    {
        if (!parse(adapter.Read(), out var v, out error)) { Error = error; return false; }
        error = Validator?.Invoke(v);
        Error = error;
        if (error != null) return false;
        _pending = v;
        return true;
    }

    internal override bool CheckLoaded(out string? error)
    {
        // Parsed like an edit (a combo whose value isn't in the map has nothing chosen), but nothing is written or kept.
        if (!parse(adapter.Read(), out var v, out error)) { Error = error; return false; }
        error = Validator?.Invoke(v);
        Error = error;
        return error == null;
    }

    internal override void WritePending(object model) => set?.Invoke((TModel)model, _pending);

    internal override void AfterCommit(object model)
    {
        var v = get((TModel)model);
        var raw = toRaw(v);
        if (!RawEq.Equals(adapter.Read(), raw)) adapter.Write(raw);
        _baseline = adapter.Read();
        _loaded = v;
        Error = null;
    }

    internal override void SetAvailable(bool available)
    {
        if (available)
        {
            foreach (var c in _disabled) c.Enabled = true;
            _disabled.Clear();
            return;
        }
        foreach (var c in adapter.Controls)
        {
            if (!c.Enabled) continue;
            c.Enabled = false;
            _disabled.Add(c);
        }
    }

    internal override void Attach(System.Action changed)
    {
        _handler = _ => changed();
        adapter.Attach(_handler);
    }

    internal override void Detach()
    {
        if (_handler != null) adapter.Detach(_handler);
        _handler = null;
    }
}
