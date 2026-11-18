namespace RadSharp.DesignHost;

/// <summary>
/// RadSharp's platform services without a window system. Forms shown with Show / ShowModal become host windows
/// the client is told about (<c>WindowOpened</c> with the owner window); modal loops and
/// <see cref="ProcessEventsUntil"/> pump the host's UI queue. File dialogs report "cancelled" (v1).
/// </summary>
internal sealed class HeadlessPlatform(DesignHostServer server) : IPlatformServices
{
    public void ShowForm(Form form) => server.ShowForm(form, modal: false);

    public void RunModal(Form form)
    {
        var window = server.ShowForm(form, modal: true);
        server.Pump(() => !server.IsOpen(window));
    }

    public void ShowModal(Form form, Action onClosed)
    {
        void Closed(Control _)
        {
            form.Closed -= Closed;
            onClosed();
        }
        form.Closed += Closed;
        server.ShowForm(form, modal: true);
    }

    public void CloseForm(Form form) => server.CloseForm(form);

    public void ProcessEventsUntil(Func<bool> done) => server.Pump(done);

    public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) =>
        server.Post(() => done(null));

    public void PostToUiThread(Action action) => server.Post(action);

    public bool OpenUrl(string url) => false;

    // The design host's windows live in the client's process: it has no monitors of its own to report, and the
    // protocol has no message to move a window, so these answer "nothing" (Screen.Monitors is empty here).
    public IReadOnlyList<MonitorInfo> GetMonitors() => [];

    public MonitorInfo? GetMonitorOf(Form form) => null;

    public bool MoveForm(Form form, Rect bounds) => false;
}
