namespace RadSharp.Designer;

/// <summary>Closing the IDE: nothing it started may outlive it.</summary>
public partial class DesignerForm
{
    /// <summary>How long closing waits for the debugger and its debuggee to go away.</summary>
    public static TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Called when the IDE closes (after the session was saved): kills the build and the Ctrl+F5 app, ends the debugging
    /// session (the adapter and the debuggee), and disposes the language hosts. Blocks briefly, up to <see cref="ShutdownTimeout"/>,
    /// for the debugger's processes; nothing here waits for the UI thread, so it can't deadlock.
    /// </summary>
    void Shutdown()
    {
        try { ResetBuildState(); }   // kills the build and the app, and starts ending the debug session
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
        try { _app?.Dispose(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _app = null;
        if (_debugger != null)
        {
            try { _debugger.DisposeAsync().AsTask().Wait(ShutdownTimeout); }
            catch (Exception e) when (e is AggregateException or ObjectDisposedException or InvalidOperationException) { }
        }
        try { StopLanguage(); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
    }
}
