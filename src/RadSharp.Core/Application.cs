namespace RadSharp;

/// <summary>
/// Core-level application state and utilities. Host-neutral (lives in Core, not Platform.Sdl).
/// </summary>
public static class Application
{
    /// <summary>Fired when an unhandled exception occurs in an event handler, timer tick, or queued callback.</summary>
    public static event EventHandler<ThreadExceptionEventArgs>? ThreadException;

    /// <summary>
    /// The process exit code: set by <see cref="Terminate(int)"/> or directly. The SDL host returns it from
    /// <c>SdlHost.Run</c> and (when non-zero) also stores it in <see cref="Environment.ExitCode"/>, so a <c>Main</c>
    /// that just calls <c>SdlHost.Run(form)</c> exits with it.
    /// </summary>
    public static int ExitCode { get; set; }

    /// <summary>
    /// The application's name (Delphi's <c>Application.Title</c>). Used as the caption of <see cref="Dialogs.ShowMessage"/>
    /// boxes and as <see cref="Form.WindowTitle"/> (the OS window / taskbar title) of a form whose
    /// <see cref="Form.Caption"/> is empty. Empty by default.
    /// </summary>
    public static string Title
    {
        get => _title;
        set { value ??= ""; if (value == _title) return; _title = value; TitleChanged?.Invoke(); }
    }

    static string _title = "";

    /// <summary>Raised when <see cref="Title"/> changes, so hosts can retitle windows whose caption is empty.</summary>
    public static event Action? TitleChanged;

    /// <summary>
    /// Raised when the host has drained its input queue and is about to wait (Delphi's <c>OnIdle</c>). Each handler may set
    /// <see cref="IdleEventArgs.Done"/> to false to be called again at once instead of after the next event or timer
    /// deadline. By default (Done = true) the host sleeps until something happens, as before, so a handler that
    /// does continuous background work must opt in, and costs a full CPU core while it does. Exceptions go to
    /// <see cref="ThreadException"/>.
    /// </summary>
    public static event IdleEventHandler? Idle;

    /// <summary>For hosts: raises <see cref="Idle"/> once. Returns true if any handler wants to be called again immediately.</summary>
    public static bool RaiseIdle()
    {
        var handlers = Idle;
        if (handlers == null) return false;
        bool more = false;
        foreach (var h in handlers.GetInvocationList().Cast<IdleEventHandler>())
        {
            var args = new IdleEventArgs();
            try { h(null, args); }
            catch (Exception ex) { ReportException(ex); }
            more |= !args.Done;
        }
        return more;
    }

    volatile static bool _terminateRequested;

    /// <summary>
    /// Asks the application to quit at the next opportunity, with <see cref="ExitCode"/> unchanged: the host asks every open form
    /// to close, modal ones first, exactly as when the user closes the window (so <see cref="Form.CloseQuery"/>
    /// can still veto, in which case the application keeps running). Safe to call from any thread, and before the loop has started.
    /// </summary>
    public static void Terminate()
    {
        _terminateRequested = true;
        PlatformHost.Current?.PostToUiThread(static () => { });   // wake a host that is waiting for events
    }

    /// <summary>Sets <see cref="ExitCode"/>, then <see cref="Terminate()"/>s.</summary>
    public static void Terminate(int exitCode)
    {
        ExitCode = exitCode;
        Terminate();
    }

    /// <summary>True after <see cref="Terminate()"/> until the host has acted on it.</summary>
    public static bool TerminateRequested => _terminateRequested;

    /// <summary>For hosts: returns true once per <see cref="Terminate()"/> call and clears the request.</summary>
    public static bool ConsumeTerminateRequest()
    {
        if (!_terminateRequested) return false;
        _terminateRequested = false;
        return true;
    }

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

/// <summary>Handler for <see cref="Application.Idle"/>.</summary>
public delegate void IdleEventHandler(object? sender, IdleEventArgs e);

/// <summary>Arguments for <see cref="Application.Idle"/>.</summary>
public sealed class IdleEventArgs : EventArgs
{
    /// <summary>True (the default): nothing more to do, the host may sleep until the next event or timer. False: call Idle again immediately.</summary>
    public bool Done { get; set; } = true;
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
