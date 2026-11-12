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

    /// <summary>Alias for <see cref="Queue"/> (Delphi's <c>TThread.Queue</c>/<c>Synchronize</c> naming).</summary>
    public static void Post(Action action) => Queue(action);

    /// <summary>
    /// Opens <paramref name="url"/> in the user's default browser (or mail client). Only absolute <c>http</c>,
    /// <c>https</c> and <c>mailto</c> URLs are accepted, since a URL can otherwise launch arbitrary local files.
    /// Returns false if the URL isn't one of those or the host couldn't open it.
    /// </summary>
    /// <example><c>Application.OpenUrl("https://example.com/help");</c></example>
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto")) return false;
        return PlatformHost.Require().OpenUrl(uri.AbsoluteUri);
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
        // Laid out at 100 %; ApplyScale scales the whole tree like a loaded form file.
        const int width = 520, height = 340, margin = 12, buttonW = 84, buttonH = 28, gap = 8;

        var form = new Form { Caption = "Unhandled error" };
        form.SetBounds(0, 0, width, height);

        var message = new Label
        {
            Name = "lblMessage",
            Caption = ex.Message.ReplaceLineEndings(" "),
            Anchors = Anchors.Left | Anchors.Top | Anchors.Right,
        };
        message.SetBounds(margin, margin, width - 2 * margin, 24);

        var details = new Memo
        {
            Name = "memDetails",
            Text = ex.ToString(),
            ReadOnly = true,
            WordWrap = false,
            Anchors = Anchors.All,
        };
        int detailsTop = margin + 32;
        details.SetBounds(margin, detailsTop, width - 2 * margin, height - detailsTop - buttonH - 2 * margin);

        var quit = new Button
        {
            Name = "btnQuit",
            Caption = "Quit",
            ModalResult = ModalResult.Abort,
            Anchors = Anchors.Right | Anchors.Bottom,
        };
        quit.SetBounds(width - margin - buttonW, height - margin - buttonH, buttonW, buttonH);

        var proceed = new Button
        {
            Name = "btnContinue",
            Caption = "Continue",
            ModalResult = ModalResult.Ok,
            Default = true,
            Anchors = Anchors.Right | Anchors.Bottom,
        };
        proceed.SetBounds(quit.Left - gap - buttonW, quit.Top, buttonW, buttonH);

        form.Add(message);
        form.Add(details);
        form.Add(proceed);
        form.Add(quit);
        form.ApplyScale();

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
