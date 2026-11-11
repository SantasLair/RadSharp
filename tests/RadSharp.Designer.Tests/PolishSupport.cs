using System.Collections.Concurrent;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Threading;

namespace RadSharp.Tests;

/// <summary>A platform host for the I9 tests: posts run inline (SimpleHost) or queue for the test to pump (QueueHost); modals answer by button name.</summary>
class PolishHost : IPlatformServices
{
    public readonly List<Form> Shown = [];
    public readonly List<string> Prompts = [];
    public Func<Form, string?>? Answer;   // the name of the button to press in a modal

    public virtual void PostToUiThread(Action action) => action();
    public void ShowForm(Form form) { Shown.Add(form); form.PerformLayout(); }
    public void RunModal(Form form)
    {
        form.PerformLayout();
        Prompts.Add(form.Caption);
        if (Answer?.Invoke(form) is { } name && form.FindControl(name) is Button b) form.Simulate.Click(b);
        if (form.IsModal) form.Close();
    }
    public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    public void CloseForm(Form form) => Shown.Remove(form);
    public void ProcessEventsUntil(Func<bool> done) { }
    public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
}

/// <summary>Posted work waits until the test pumps it (as on the real UI thread); every post and awaited task wakes the pump.</summary>
sealed class PolishQueueHost : PolishHost
{
    readonly ConcurrentQueue<Action> _queue = new();
    readonly SemaphoreSlim _wake = new(0);
    public override void PostToUiThread(Action action) { _queue.Enqueue(action); _wake.Release(); }

    public void PumpUntil(Func<bool> done, string what, int seconds = 120)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (true)
        {
            while (_queue.TryDequeue(out var action)) action();
            if (done()) return;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !_wake.Wait(left)) throw new TimeoutException($"Timed out waiting for {what}.");
        }
    }

    public void PumpUntil(Task task, string what)
    {
        task.ContinueWith(_ => _wake.Release(), TaskScheduler.Default);
        PumpUntil(() => task.IsCompleted, what);
    }

    public void SetUiContext() => SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(PostToUiThread));
}

static class PolishSupport
{
    public static string TempDir() => Directory.CreateTempSubdirectory("radsharp-i9-").FullName;
    public static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static PolishHost NewHost()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var host = new PolishHost();
        PlatformHost.Current = host;
        return host;
    }

    public static DesignerForm Start(string? project, string? settings = null, double width = 1220, double height = 780)
    {
        var d = new DesignerForm(project, settings ?? Path.Combine(TempDir(), "designer.json"));
        d.Settings.Editor = EditorKind.Custom;
        d.Settings.CustomEditorCommand = "dotnet --version";
        d.SetBounds(0, 0, (int)width, (int)height);
        d.PerformLayout();
        return d;
    }

    public static RadProject NewProject(string name = "App") => RadProject.Create(TempDir(), name);
}
