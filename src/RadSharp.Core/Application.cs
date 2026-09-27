namespace RadSharp;

/// <summary>
/// Core-level application state and utilities. Host-neutral (lives in Core, not Platform.Sdl).
/// </summary>
public static class Application
{
    /// <summary>Fired when an unhandled exception occurs in an event handler, timer tick, or queued callback.</summary>
    public static event EventHandler<ThreadExceptionEventArgs>? ThreadException;

    /// <summary>The process exit code (set by Terminate or before exiting).</summary>
    public static int ExitCode { get; set; }

    /// <summary>Queues an action to run on the UI thread.</summary>
    public static void Queue(Action action)
    {
        PlatformHost.Require().PostToUiThread(action);
    }

    /// <summary>Reports an unhandled exception, firing the ThreadException event with a default error dialog if no subscriber handles it.</summary>
    internal static void ReportException(Exception ex)
    {
        var args = new ThreadExceptionEventArgs(ex);
        ThreadException?.Invoke(null, args);

        if (!args.Handled)
        {
            // Show the default error dialog.
            ShowDefaultErrorDialog(ex);
        }
    }

    private static void ShowDefaultErrorDialog(Exception ex)
    {
        var form = new Form
        {
            Caption = "Error",
            Width = 500,
            Height = 300,
        };

        var messageLabel = new Label
        {
            Text = ex.Message,
            Left = 12,
            Top = 12,
            Width = 476,
            Height = 60,
            WordWrap = true,
        };

        var stackMemo = new Memo
        {
            Text = ex.StackTrace ?? "(no stack trace)",
            ReadOnly = true,
            Left = 12,
            Top = 80,
            Width = 476,
            Height = 170,
            ScrollBars = ScrollBars.Both,
        };

        var continueBtn = new Button
        {
            Caption = "Continue",
            ModalResult = ModalResult.Ok,
            Left = 332,
            Top = 260,
            Width = 75,
            Height = 28,
        };

        var quitBtn = new Button
        {
            Caption = "Quit",
            ModalResult = ModalResult.Abort,
            Left = 413,
            Top = 260,
            Width = 75,
            Height = 28,
        };

        form.Controls.Add(messageLabel);
        form.Controls.Add(stackMemo);
        form.Controls.Add(continueBtn);
        form.Controls.Add(quitBtn);

        var result = form.ShowModal();
        if (result == ModalResult.Abort)
        {
            Environment.Exit(ExitCode);
        }
    }
}

/// <summary>Arguments for the ThreadException event.</summary>
public sealed class ThreadExceptionEventArgs : EventArgs
{
    public Exception Exception { get; }

    /// <summary>Set to true to indicate the exception has been handled and the app should continue.</summary>
    public bool Handled { get; set; }

    public ThreadExceptionEventArgs(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }
}
