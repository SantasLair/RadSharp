namespace RadSharp.Diagnostics;

/// <summary>
/// Exception guard: wraps action invocations with try/catch, reporting exceptions
/// through Application.ThreadException instead of letting them escape. Host implementations
/// use this to wrap dispatch, ticks, timers and queued actions.
/// </summary>
public static class Guard
{
    /// <summary>Runs an action, catching any exception and reporting it via Application.ReportException.</summary>
    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Application.ReportException(ex);
        }
    }
}
