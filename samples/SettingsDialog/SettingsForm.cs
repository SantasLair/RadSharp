using RadSharp;

namespace SettingsDialog;

/// <summary>
/// A second form in the same project; its OK/Cancel buttons set ModalResult, which closes it. The controls are bound to the
/// settings with a <see cref="Binder{T}"/>: nothing is copied in or out by hand, Cancel writes nothing (the binder is
/// buffered), and OK commits.
/// </summary>
public partial class SettingsForm : Form
{
    readonly Binder<Settings> _binder;   // owned by this form: disposed when it closes

    public SettingsForm()
    {
        InitializeComponent();
        _binder = new Binder<Settings>(this);
        _binder.Bind(edtUser, s => s.User, (s, v) => s.User = v.Trim());
        _binder.Bind(cboTheme, s => s.Theme, (s, v) => s.Theme = v);
        _binder.Bind(chkAutosave, s => s.Autosave, (s, v) => s.Autosave = v);
        _binder.Bind(trkVolume, s => s.Volume, (s, v) => s.Volume = v);
        _binder.Bind(spnHistory, s => s.History, (s, v) => s.History = v);
        // OK closes the dialog; write the edits then, or stay open if one doesn't validate.
        CloseQuery += (_, e) =>
        {
            if (ModalResult != ModalResult.Ok || _binder.Commit()) return;
            e.CanClose = false;
            Dialogs.ShowMessage(_binder.Errors[0].Message, this);
            _binder.FocusFirstError();
        };
    }

    /// <summary>Shows the dialog on <paramref name="settings"/>; true if the user pressed OK (and the settings were updated).</summary>
    public bool Edit(Settings settings)
    {
        _binder.Current = settings;
        return ShowModal() == ModalResult.Ok;
    }
}
