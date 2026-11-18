using System.Collections.Concurrent;

namespace RadSharp.RefApp;

/// <summary>
/// The window system for <see cref="DesignerDriver"/>: no windows, modal dialogs answered by queued handlers (an
/// unexpected dialog fails the test with its text), file dialogs answered from <see cref="NextFiles"/>, posted actions
/// queued until <see cref="Pump"/>. Like the SDL host it raises <see cref="Form.Shown"/> when a form is shown.
/// </summary>
public sealed class DriverHost : IPlatformServices
{
    readonly Queue<(string What, Func<Form, string?> Answer)> _modals = new();
    readonly ConcurrentQueue<Action> _posted = new();

    /// <summary>Forms shown with <see cref="Form.Show"/> (the live form of an F9 run), oldest first.</summary>
    public List<Form> Shown { get; } = [];

    /// <summary>Paths the next file or folder dialogs return, in order; an empty queue means "cancelled".</summary>
    public Queue<string> NextFiles { get; } = new();

    /// <summary>Every modal dialog shown, by caption, in order.</summary>
    public List<string> ModalLog { get; } = [];

    /// <summary>
    /// Answers the next modal dialog: <paramref name="answer"/> drives it like a person (typing, clicking) and returns
    /// the name of the button to press last, or null to leave it with whatever it already decided.
    /// </summary>
    public void Expect(string what, Func<Form, string?> answer) => _modals.Enqueue((what, answer));

    public int PendingModals => _modals.Count;

    public void ShowForm(Form form)
    {
        Shown.Add(form);
        form.PerformLayout();
        form.NotifyShown();
    }

    public void RunModal(Form form)
    {
        ModalLog.Add(form.Caption);
        form.PerformLayout();
        form.NotifyShown();
        if (!_modals.TryDequeue(out var next))
            throw new InvalidOperationException($"Unexpected modal dialog \"{form.Caption}\": {Describe(form)}");
        string? button;
        try { button = next.Answer(form); }
        catch (Exception e) { throw new InvalidOperationException($"While answering \"{form.Caption}\" ({next.What}): {e.Message}", e); }
        if (button != null)
        {
            var b = form.FindControl(button) as Button
                ?? throw new InvalidOperationException($"\"{form.Caption}\" has no button {button}.");
            form.Simulate.Click(b);
        }
        if (form.IsModal) form.Close();
    }

    public void ShowModal(Form form, Action onClosed)
    {
        RunModal(form);
        onClosed();
    }

    public void CloseForm(Form form) => Shown.Remove(form);

    public void ProcessEventsUntil(Func<bool> done)
    {
        for (int i = 0; i < 1000 && !done(); i++)
            if (!PumpOne()) break;
    }

    public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done)
    {
        if (!NextFiles.TryDequeue(out var path)) { done(null); return; }
        done([new FileResult(path, () => File.OpenRead(path), () => File.Create(path))]);
    }

    public void PostToUiThread(Action action) => _posted.Enqueue(action);

    /// <summary>Runs every posted action (and those they post), as the SDL event loop would between events.</summary>
    public void Pump()
    {
        for (int i = 0; i < 10_000 && PumpOne(); i++) { }
    }

    bool PumpOne()
    {
        if (!_posted.TryDequeue(out var action)) return false;
        action();
        return true;
    }

    /// <summary>The text a person would read on a dialog: its labels and buttons.</summary>
    public static string Describe(Form form) =>
        string.Join(" | ", form.Descendants().Select(c => c switch
        {
            Label l when l.Caption.Length > 0 => l.Caption,
            Button b => $"[{b.Caption}]",
            _ => null,
        }).Where(s => s != null));
}
