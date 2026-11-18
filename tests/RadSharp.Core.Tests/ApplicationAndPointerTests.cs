namespace RadSharp.Tests;

/// <summary>Application.Title / Idle / Terminate / ExitCode, and the pointer kind on mouse events (M13 items 5 and 6).</summary>
[Collection("PlatformHost")]   // these touch Application's and PlatformHost's process-wide state
public class ApplicationAndPointerTests
{
    /// <summary>A host that records what the core asks of it; modal forms are inspected, then closed.</summary>
    sealed class FakeHost : IPlatformServices
    {
        public Action<Form>? OnModal;
        public int Wakes;
        public void ShowForm(Form form) => form.NotifyShown();
        public void RunModal(Form form) { form.PerformLayout(); OnModal?.Invoke(form); }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) { Wakes++; action(); }
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    static FakeHost Install() => (FakeHost)(PlatformHost.Current = new FakeHost());

    // ---- Title ----

    [Fact]
    public void Title_is_the_window_title_of_a_form_without_a_caption()
    {
        var old = Application.Title;
        try
        {
            Application.Title = "";
            Assert.Equal("", new Form().WindowTitle);
            Application.Title = "Tile Editor";
            Assert.Equal("Tile Editor", new Form().WindowTitle);
            Assert.Equal("Palette", new Form { Caption = "Palette" }.WindowTitle);
        }
        finally { Application.Title = old; }
    }

    [Fact]
    public void ShowMessage_uses_the_title_as_its_caption()
    {
        var old = Application.Title;
        var host = Install();
        try
        {
            string? caption = null;
            host.OnModal = f => caption = f.Caption;
            Application.Title = "Tile Editor";
            Dialogs.ShowMessage("Saved.");
            Assert.Equal("Tile Editor", caption);

            Application.Title = "";
            Dialogs.ShowMessage("Saved.");
            Assert.Equal("Information", caption);

            Dialogs.MessageDlg("Sure?", MessageType.Confirmation, MessageButtons.YesNo);   // typed boxes keep their own caption
            Assert.Equal("Confirm", caption);
        }
        finally { Application.Title = old; PlatformHost.Current = null; }
    }

    // ---- Idle ----

    [Fact]
    public void Idle_handlers_are_done_by_default_so_the_host_keeps_sleeping()
    {
        int calls = 0;
        IdleEventHandler h = (_, e) => { calls++; Assert.True(e.Done); };
        Application.Idle += h;
        try
        {
            Assert.False(Application.RaiseIdle());
            Assert.Equal(1, calls);
        }
        finally { Application.Idle -= h; }
        Assert.False(Application.RaiseIdle());   // nobody subscribed
    }

    [Fact]
    public void An_idle_handler_asks_for_continuous_calls_by_clearing_Done()
    {
        int calls = 0;
        IdleEventHandler greedy = (_, e) => { calls++; e.Done = calls >= 3; };
        Application.Idle += greedy;
        try
        {
            Assert.True(Application.RaiseIdle());
            Assert.True(Application.RaiseIdle());
            Assert.False(Application.RaiseIdle());   // finished: the host may sleep again
        }
        finally { Application.Idle -= greedy; }
    }

    [Fact]
    public void One_greedy_handler_is_enough_and_a_throwing_one_goes_to_ThreadException()
    {
        var seen = new List<string>();
        IdleEventHandler quiet = (_, _) => seen.Add("quiet");
        IdleEventHandler boom = (_, _) => throw new InvalidOperationException("idle boom");
        IdleEventHandler greedy = (_, e) => { seen.Add("greedy"); e.Done = false; };
        EventHandler<ThreadExceptionEventArgs> catcher = (_, e) => { seen.Add(e.Exception.Message); e.Handled = true; };
        Application.Idle += quiet; Application.Idle += boom; Application.Idle += greedy;
        Application.ThreadException += catcher;
        try
        {
            Assert.True(Application.RaiseIdle());
            Assert.Equal(["quiet", "idle boom", "greedy"], seen);
        }
        finally
        {
            Application.Idle -= quiet; Application.Idle -= boom; Application.Idle -= greedy;
            Application.ThreadException -= catcher;
        }
    }

    // ---- Terminate / ExitCode ----

    [Fact]
    public void Terminate_raises_one_request_the_host_consumes_and_wakes_the_host()
    {
        var host = Install();
        var oldCode = Application.ExitCode;
        try
        {
            Application.ConsumeTerminateRequest();
            Assert.False(Application.TerminateRequested);

            Application.Terminate();
            Assert.True(Application.TerminateRequested);
            Assert.Equal(1, host.Wakes);                       // a sleeping host is woken to notice it
            Assert.True(Application.ConsumeTerminateRequest());
            Assert.False(Application.ConsumeTerminateRequest()); // once per Terminate call
            Assert.False(Application.TerminateRequested);
        }
        finally { Application.ExitCode = oldCode; PlatformHost.Current = null; }
    }

    [Fact]
    public void Terminate_with_a_code_sets_ExitCode_and_works_without_a_host()
    {
        PlatformHost.Current = null;
        var oldCode = Application.ExitCode;
        try
        {
            Application.ConsumeTerminateRequest();
            Application.Terminate(3);
            Assert.Equal(3, Application.ExitCode);
            Assert.True(Application.ConsumeTerminateRequest());
        }
        finally { Application.ExitCode = oldCode; Application.ConsumeTerminateRequest(); }
    }

    // ---- PointerKind ----

    [Fact]
    public void Mouse_events_default_to_the_mouse_and_report_touch_and_pen_when_simulated()
    {
        var f = new Form();
        f.SetBounds(0, 0, 200, 100);
        var kinds = new List<PointerKind>();
        var area = new Panel();
        area.SetBounds(0, 0, 200, 100);
        area.MouseMove += (_, e) => kinds.Add(e.Pointer);
        area.MouseDown += (_, e) => kinds.Add(e.Pointer);
        area.MouseUp += (_, e) => kinds.Add(e.Pointer);
        f.Add(area);
        f.PerformLayout();

        f.Simulate.Click(20, 20);
        Assert.Equal(new[] { PointerKind.Mouse, PointerKind.Mouse, PointerKind.Mouse }, kinds);   // move, down, up

        kinds.Clear();
        f.Simulate.Click(20, 20, pointer: PointerKind.Pen);
        Assert.All(kinds, k => Assert.Equal(PointerKind.Pen, k));
        Assert.Equal(3, kinds.Count);

        kinds.Clear();
        f.Simulate.Tap(30, 30);
        Assert.All(kinds, k => Assert.Equal(PointerKind.Touch, k));
        Assert.Equal(3, kinds.Count);

        Assert.Equal(PointerKind.Mouse, new MouseEventArgs(0, 0, MouseButton.Left, KeyModifiers.None).Pointer);
    }

    [Fact]
    public void A_touch_tap_still_clicks_a_button_and_toggles_a_check_box()
    {
        var f = new Form();
        f.SetBounds(0, 0, 200, 100);
        var button = new Button { Caption = "Go" };
        button.SetBounds(10, 10, 80, 28);
        var check = new CheckBox { Caption = "On" };
        check.SetBounds(10, 50, 80, 20);
        f.Add(button); f.Add(check);
        f.PerformLayout();
        int clicks = 0; button.Click += _ => clicks++;

        f.Simulate.Tap(button.AbsoluteLeft + 5, button.AbsoluteTop + 5);
        Assert.Equal(1, clicks);
        f.Simulate.Click(check, pointer: PointerKind.Pen);
        Assert.True(check.Checked);
        Assert.True(check.Focused);
    }

    [Fact]
    public void A_touch_drag_reports_touch_on_every_move()
    {
        var f = new Form();
        f.SetBounds(0, 0, 200, 100);
        var kinds = new HashSet<PointerKind>();
        var area = new Panel();
        area.SetBounds(0, 0, 200, 100);
        area.MouseMove += (_, e) => kinds.Add(e.Pointer);
        f.Add(area);
        f.PerformLayout();
        f.Simulate.Drag(10, 10, 100, 50, pointer: PointerKind.Touch);
        Assert.Equal([PointerKind.Touch], kinds);
    }
}
