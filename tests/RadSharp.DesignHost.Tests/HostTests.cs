using RadSharp.Design.Code;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.DesignHost.Tests;

/// <summary>The headless host driven over an in-memory duplex stream, as the Visual Studio client drives it.</summary>
[Collection("DesignHost")]
public class HostTests
{
    const int Doc = 1;

    internal static (HostClient Client, HostClient.Panes Panes) OpenHello(string? path = null)
    {
        var client = new HostClient();
        client.Send(new P.Hello { ProtocolVersion = P.ProtocolInfo.Version, ClientName = "tests" });
        client.Expect<P.HelloAck>();
        var panes = client.Open(Doc, path ?? TestFiles.HelloForm());
        client.Show(panes.Surface, 800, 600);
        client.Settle();
        return (client, panes);
    }

    [Fact]
    public void Hello_is_acknowledged_with_the_protocol_version()
    {
        using var client = new HostClient();
        client.Send(new P.Hello { ProtocolVersion = P.ProtocolInfo.Version, ClientName = "tests" });
        var ack = client.Expect<P.HelloAck>();
        Assert.Equal(P.ProtocolInfo.Version, ack.ProtocolVersion);
        Assert.False(string.IsNullOrEmpty(ack.HostVersion));
    }

    [Fact]
    public void OpenDocument_announces_surface_inspector_and_palette_windows()
    {
        using var client = new HostClient();
        client.Send(new P.OpenDocument { DocId = 7, Path = TestFiles.HelloForm() });
        var windows = Enumerable.Range(0, 3).Select(_ => client.Expect<P.WindowOpened>()).ToList();
        Assert.Equal([P.WindowRole.Surface, P.WindowRole.Inspector, P.WindowRole.Palette], windows.Select(w => w.Role));
        Assert.All(windows, w => Assert.Equal(7, w.DocId));
        Assert.All(windows, w => Assert.Equal((0, 0), (w.Width, w.Height)));
        Assert.Equal(3, windows.Select(w => w.WindowId).Distinct().Count());
        Assert.Equal("MainForm", windows[0].Title);
        var state = client.Expect<P.DocumentState>(s => s.DocId == 7);
        Assert.False(state.Dirty);
        Assert.False(state.CanUndo);
    }

    [Fact]
    public void Opening_a_missing_file_reports_an_error()
    {
        using var client = new HostClient();
        client.Send(new P.OpenDocument { DocId = 3, Path = Path.Combine(TestFiles.TempDir(), "Nope.radform.json") });
        var error = client.Expect<P.Error>();
        Assert.Equal(3, error.DocId);
        Assert.Contains("Nope.radform.json", error.Text);
    }

    [Fact]
    public void Attach_and_resize_send_a_full_frame_of_the_form()
    {
        using var client = new HostClient();
        var panes = client.Open(Doc, TestFiles.HelloForm());
        var frame = client.Show(panes.Surface, 640, 480);
        Assert.Equal((0, 0, 640, 480), (frame.X, frame.Y, frame.Width, frame.Height));
        Assert.Equal(640 * 480 * 4, frame.Pixels.Length);
        Assert.True(TestFiles.NotBlank(frame.Pixels));

        // A new size gets a new full frame; so do the other panes.
        client.Send(new P.ResizeWindow { WindowId = panes.Surface, Width = 500, Height = 300, Scale = 1 });
        client.Expect<P.Frame>(f => f.WindowId == panes.Surface && (f.X, f.Y, f.Width, f.Height) == (0, 0, 500, 300));
        Assert.True(TestFiles.NotBlank(client.Show(panes.Inspector, 300, 500).Pixels));
        Assert.True(TestFiles.NotBlank(client.Show(panes.Palette, 220, 500).Pixels));
    }

    [Fact]
    public void A_new_scale_rebuilds_the_pane_at_that_scale()
    {
        using var client = new HostClient();
        var panes = client.Open(Doc, TestFiles.HelloForm());
        client.Show(panes.Surface, 1000, 700, scale: 2);
        Assert.Equal(2.0, client.Ui(() => client.Server!.Window(panes.Surface)!.Form.Scale));
        Assert.Equal(2.0, Form.DefaultScale);
        // The designed form is drawn at twice its size: a control's centre is twice as far from the origin.
        var (x, _) = client.ControlPoint(Doc, "btnOk");
        var origin = client.DesignPoint(Doc, 0, 0);
        int designX = client.Ui(() => { var c = client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!; return c.AbsoluteLeft + c.Width / 2; });
        Assert.InRange(x - origin.X, 2 * designX - 2, 2 * designX + 2);
        Form.DefaultScale = 1;
    }

    [Fact]
    public void Clicking_a_control_selects_it_and_repaints()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        client.Click(panes.Surface, x, y);
        var state = client.Expect<P.DocumentState>(s => s.HasSelection);
        Assert.False(state.Dirty);
        client.Expect<P.Frame>(f => f.WindowId == panes.Surface && f.Pixels.Length == f.Width * f.Height * 4);
        Assert.Equal("btnOk", client.Ui(() => client.Server!.Document(Doc)!.Doc.Primary.Name));
        // The inspector follows the selection.
        Assert.Equal("btnOk", client.Ui(() => client.Server!.Document(Doc)!.GridProps!.Target?.Name));
    }

    [Fact]
    public void The_designed_form_keeps_the_default_theme_when_the_designer_is_dark()
    {
        var saved = RadSharp.Drawing.Theme.Current;
        RadSharp.Drawing.Theme.Current = RadSharp.Drawing.Theme.Dark;
        try
        {
            var (client, panes) = OpenHello();
            using var _ = client;
            Assert.Same(RadSharp.Drawing.Theme.Default, client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.Theme));
            Assert.Same(RadSharp.Drawing.Theme.Dark, client.Ui(() => client.Server!.Window(panes.Surface)!.Form.Theme));

            // Undo rebuilds the form from a snapshot; the new form must get the default theme too.
            var (x, y) = client.ControlPoint(Doc, "btnOk");
            client.Drag(panes.Surface, x, y, x - 32, y - 20);
            client.Expect<P.DocumentState>(s => s.Dirty);
            client.Send(new P.Command { DocId = Doc, Name = "Undo" });
            client.Expect<P.DocumentState>(s => !s.Dirty);
            Assert.Same(RadSharp.Drawing.Theme.Default, client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.Theme));
        }
        finally { RadSharp.Drawing.Theme.Current = saved; }
    }

    [Fact]
    public void Dragging_a_control_moves_it_and_undo_reverts()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        var before = client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!.Bounds);
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        client.Drag(panes.Surface, x, y, x - 64, y - 40);
        var dirty = client.Expect<P.DocumentState>(s => s.Dirty);
        Assert.True(dirty.CanUndo);
        var after = client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!.Bounds);
        Assert.NotEqual(before, after);

        client.Send(new P.Command { DocId = Doc, Name = "Undo" });
        var undone = client.Expect<P.DocumentState>(s => !s.Dirty);
        Assert.True(undone.CanRedo);
        Assert.Equal(before, client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!.Bounds));

        client.Send(new P.Command { DocId = Doc, Name = "Redo" });
        client.Expect<P.DocumentState>(s => s.Dirty);
        Assert.Equal(after, client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!.Bounds));
    }

    [Fact]
    public void Commands_delete_copy_paste_and_select_all()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        client.Click(panes.Surface, x, y);
        client.Expect<P.DocumentState>(s => s.HasSelection);

        client.Send(new P.Command { DocId = Doc, Name = "Copy" });
        client.Send(new P.Command { DocId = Doc, Name = "Paste" });
        client.Expect<P.DocumentState>(s => s.Dirty);
        Assert.NotNull(client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("Button1")));

        client.Send(new P.Command { DocId = Doc, Name = "Delete" });
        client.Expect<P.DocumentState>(s => !s.HasSelection);
        Assert.Null(client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("Button1")));

        client.Send(new P.Command { DocId = Doc, Name = "SelectAll" });
        client.Expect<P.DocumentState>(s => s.HasSelection);
        Assert.True(client.Ui(() => client.Server!.Document(Doc)!.Doc.Selection.Count) > 1);
    }

    [Fact]
    public void SaveDocument_writes_the_file_and_clears_dirty()
    {
        var path = TestFiles.HelloForm();
        var (client, panes) = OpenHello(path);
        using var _ = client;
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        client.Drag(panes.Surface, x, y, x + 48, y + 16);
        client.Expect<P.DocumentState>(s => s.Dirty);

        client.Send(new P.SaveDocument { DocId = Doc, Path = "" });
        var saved = client.Expect<P.DocumentSaved>();
        Assert.True(saved.Ok, saved.Error);
        client.Expect<P.DocumentState>(s => !s.Dirty);
        Assert.Equal(client.Ui(() => client.Server!.Document(Doc)!.Doc.Save()), File.ReadAllText(path));

        // Save As goes to the new path.
        var other = Path.Combine(Path.GetDirectoryName(path)!, "Other.radform.json");
        client.Send(new P.SaveDocument { DocId = Doc, Path = other });
        Assert.True(client.Expect<P.DocumentSaved>().Ok);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void ReloadDocument_rereads_the_file()
    {
        var path = TestFiles.HelloForm();
        var (client, _) = OpenHello(path);
        using var __ = client;
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Hello RADSharp\"", "\"Reloaded\""));
        client.Send(new P.ReloadDocument { DocId = Doc });
        client.Settle();
        Assert.Equal("Reloaded", client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.Caption));
    }

    [Fact]
    public void Picking_from_the_palette_then_clicking_the_surface_creates_a_control()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        client.Show(panes.Palette, 240, 600);

        // Filter the palette by typing, then pick "CheckBox".
        var (fx, fy) = client.Ui(() =>
        {
            var f = client.Server!.Document(Doc)!.PaletteFilter!;
            return (f.AbsoluteLeft + f.Width / 2, f.AbsoluteTop + f.Height / 2);
        });
        client.Click(panes.Palette, fx, fy);
        client.Send(new P.TextInput { WindowId = panes.Palette, Text = "Check" });
        client.Settle();
        var (px, py) = client.Ui(() =>
        {
            var tree = client.Server!.Document(Doc)!.PaletteTree!;
            var rows = tree.VisibleNodes();
            int i = rows.FindIndex(n => n.Tag is ComponentClass { Name: "CheckBox" });
            Assert.True(i >= 0, "CheckBox is in the filtered palette");
            return (tree.AbsoluteLeft + tree.Width / 2, tree.AbsoluteTop + 1 + i * 24 + 12);
        });
        client.Click(panes.Palette, px, py);
        client.Settle();
        Assert.Equal("CheckBox", client.Ui(() => client.Server!.Document(Doc)!.Surface!.PendingClass?.Name));

        var (sx, sy) = client.DesignPoint(Doc, 300, 120);
        client.Click(panes.Surface, sx, sy);
        client.Expect<P.DocumentState>(s => s.Dirty && s.HasSelection);
        Assert.IsType<CheckBox>(client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("CheckBox1")));
        Assert.Null(client.Ui(() => client.Server!.Document(Doc)!.Surface!.PendingClass));
    }

    [Fact]
    public void Editing_a_value_in_the_inspector_changes_the_control()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        client.Show(panes.Inspector, 320, 900);
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        client.Click(panes.Surface, x, y);
        client.Expect<P.DocumentState>(s => s.HasSelection);

        var (vx, vy) = client.Ui(() =>
        {
            var grid = client.Server!.Document(Doc)!.GridProps!;
            int i = Enumerable.Range(0, grid.RowCount).First(r => !grid.IsHeader(r) && grid.RowName(r).Equals("caption", StringComparison.OrdinalIgnoreCase));
            var cell = grid.ValueRect(i);
            return (grid.AbsoluteLeft + cell.X + cell.Width / 2, grid.AbsoluteTop + cell.Y + cell.Height / 2);
        });
        // A click on the selected row's value edits it; on another row, it selects the row first.
        client.Click(panes.Inspector, vx, vy);
        client.Settle();
        if (!client.Ui(() => client.Server!.Document(Doc)!.GridProps!.IsEditing))
        {
            client.Click(panes.Inspector, vx, vy);
            client.Settle();
        }
        Assert.True(client.Ui(() => client.Server!.Document(Doc)!.GridProps!.IsEditing));
        client.Send(new P.TextInput { WindowId = panes.Inspector, Text = "Done" });
        client.Key(panes.Inspector, "Enter");
        client.Expect<P.DocumentState>(s => s.Dirty && s.CanUndo);
        Assert.Equal("Done", client.Ui(() => ((Button)client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!).Caption));
    }

    [Fact]
    public void Double_click_outside_a_project_reports_an_error()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        client.DoubleClick(panes.Surface, x, y);
        var error = client.Expect<P.Error>();
        Assert.Equal(Doc, error.DocId);
        Assert.Contains("not part of a RADSharp project", error.Text);
    }

    /// <summary>A project with a MainForm and a Button1 on it, its surface and inspector shown.</summary>
    internal static (HostClient Client, HostClient.Panes Panes, RadProject Project, string Code) OpenNavProject()
    {
        var project = RadProject.Create(TestFiles.TempDir(), "NavApp");
        var form = project.FormFileFor("MainForm")!;
        var client = new HostClient();
        var panes = client.Open(Doc, form);
        client.Show(panes.Surface, 800, 600);
        client.Show(panes.Inspector, 320, 900);
        client.Ui(() => { var d = client.Server!.Document(Doc)!.Doc; d.Create(ComponentRegistry.Find("Button")!, d.Form, 40, 40); });
        client.Settle();
        return (client, panes, project, Path.Combine(project.Directory, "MainForm.cs"));
    }

    internal static void DoubleClickButton(HostClient client, HostClient.Panes panes)
    {
        var (x, y) = client.ControlPoint(Doc, "Button1");
        client.DoubleClick(panes.Surface, x, y);
    }

    /// <summary>Double-clicks the onClick row of the inspector's Events tab.</summary>
    static void DoubleClickClickEvent(HostClient client, HostClient.Panes panes)
    {
        var (ex, ey) = client.Ui(() =>
        {
            var doc = client.Server!.Document(Doc)!;
            doc.InspectorPages!.ActivePageIndex = 1;
            doc.InspectorPages.OwningForm!.PerformLayout();
            var grid = doc.GridEvents!;
            int i = Enumerable.Range(0, grid.RowCount).First(r => grid.RowName(r) == "onClick");
            var row = grid.RowRect(i);
            return (grid.AbsoluteLeft + row.X + 20, grid.AbsoluteTop + row.Y + row.Height / 2);
        });
        client.DoubleClick(panes.Inspector, ex, ey);
    }

    static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Double_click_edits_the_editors_buffer_not_the_file()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;
        var onDisk = File.ReadAllText(code);
        var buffer = onDisk.Replace("class MainForm", "// unsaved edit\nclass MainForm");
        Assert.NotEqual(onDisk, buffer);

        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();
        Assert.Equal(Doc, request.DocId);
        Assert.Equal(code, request.File);
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = true, Text = buffer });

        var edit = client.Expect<P.ApplyCodeEdit>();
        Assert.Equal(Doc, edit.DocId);
        Assert.Equal(code, edit.File);
        Assert.Contains("// unsaved edit", edit.Text);
        Assert.Contains("void Button1_Click(Control sender)", edit.Text);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(buffer))).ToLowerInvariant(), edit.BaseHash);
        var lines = Lines(edit.Text);
        int signature = Array.FindIndex(lines, l => l.Contains("void Button1_Click(Control sender)"));
        Assert.Equal("{", lines[signature + 1].Trim());
        Assert.Equal(signature + 3, edit.Line);   // 1-based: the empty line inside the body
        Assert.Equal("}", lines[edit.Line].Trim());
        Assert.Equal(onDisk, File.ReadAllText(code));   // the host never writes the file
        Assert.Equal("Button1_Click", client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("Button1")!.EventHandlers["onClick"]));
        Assert.True(client.Ui(() => client.Server!.Document(Doc)!.Doc.Modified));
    }

    [Fact]
    public void Double_click_without_an_editor_buffer_reads_the_disk()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;
        var onDisk = File.ReadAllText(code);

        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = false, Text = "" });

        var edit = client.Expect<P.ApplyCodeEdit>();
        Assert.Equal(code, edit.File);
        Assert.Contains("void Button1_Click(Control sender)", edit.Text);
        Assert.Equal("}", Lines(edit.Text)[edit.Line].Trim());
        Assert.Equal(onDisk, File.ReadAllText(code));
    }

    [Fact]
    public void An_existing_handler_navigates_without_an_edit()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;

        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = false, Text = "" });
        var edit = client.Expect<P.ApplyCodeEdit>();

        // The IDE applied the edit; double-clicking the event row again finds the handler in the buffer.
        client.Settle();
        DoubleClickClickEvent(client, panes);
        var again = client.Expect<P.CodeRequest>();
        Assert.NotEqual(request.RequestId, again.RequestId);
        client.Send(new P.CodeResponse { RequestId = again.RequestId, Found = true, Text = edit.Text });
        var nav = client.Expect<P.NavigateToCode>();
        Assert.Equal(Doc, nav.DocId);
        Assert.Equal(code, nav.File);
        Assert.Equal(edit.Line, nav.Line);
        client.Settle();
        Assert.DoesNotContain("Button1_Click", File.ReadAllText(code));
    }

    static string Sha256Hex(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static bool HasClickHandler(HostClient client) =>
        client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("Button1")!.EventHandlers.ContainsKey("onClick"));

    [Fact]
    public void An_unanswered_code_request_reports_an_error_and_rolls_the_handler_back()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;
        var onDisk = File.ReadAllText(code);
        client.Ui(() => client.Server!.CodeRequestTimeout = TimeSpan.FromMilliseconds(200));

        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();   // never answered
        var error = client.Expect<P.Error>(e => e.Text.Contains("code editor did not respond"));
        Assert.Equal(Doc, error.DocId);
        Assert.False(HasClickHandler(client));
        Assert.Equal(onDisk, File.ReadAllText(code));

        // A late answer is ignored: no edit from the disk text.
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = false, Text = "" });
        client.Settle();
        Assert.Throws<TimeoutException>(() => client.Expect<P.ApplyCodeEdit>(timeoutMs: 300));
    }

    [Fact]
    public void An_unreadable_code_file_rolls_the_handler_back()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;
        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();
        File.Delete(code);
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = false, Text = "" });
        client.Expect<P.Error>(e => e.Text.Contains("Can't create the handler"));
        Assert.False(HasClickHandler(client));
    }

    [Fact]
    public void A_buffer_the_handler_cannot_be_added_to_rolls_the_handler_back()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;
        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = true, Text = "// no class here\n" });
        client.Expect<P.Error>(e => e.Text.Contains("Can't create the handler"));
        Assert.False(HasClickHandler(client));
    }

    [Fact]
    public void ApplyCodeEdit_carries_the_hash_of_the_text_it_was_computed_from()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;
        var onDisk = File.ReadAllText(code);
        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = false, Text = "" });
        var edit = client.Expect<P.ApplyCodeEdit>();
        Assert.Equal(Sha256Hex(onDisk), edit.BaseHash);
        Assert.NotEqual(Sha256Hex(edit.Text), edit.BaseHash);
    }

    [Fact]
    public void Closing_the_document_drops_a_pending_code_request()
    {
        var (client, panes, project, code) = OpenNavProject();
        using var _ = client;
        DoubleClickButton(client, panes);
        var request = client.Expect<P.CodeRequest>();
        client.Send(new P.CloseDocument { DocId = Doc });
        client.Send(new P.CodeResponse { RequestId = request.RequestId, Found = false, Text = "" });
        client.Settle();
        Assert.Throws<TimeoutException>(() => client.Expect<P.ApplyCodeEdit>(timeoutMs: 300));
    }

    [Fact]
    public void Tab_order_opens_as_a_modal_dialog_over_the_surface()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        client.Send(new P.Command { DocId = Doc, Name = "TabOrder" });
        var opened = client.Expect<P.WindowOpened>(w => w.Role == P.WindowRole.Dialog);
        Assert.True(opened.Modal);
        Assert.Equal(panes.Surface, opened.OwnerWindowId);
        Assert.True(TestFiles.NotBlank(client.Show(opened.WindowId, opened.Width, opened.Height).Pixels));

        client.Send(new P.CloseWindow { WindowId = opened.WindowId });
        Assert.Equal(opened.WindowId, client.Expect<P.WindowClosed>().WindowId);
        Assert.Equal(0, client.Ui(() => 0));   // the modal loop ended
    }

    [Fact]
    public void Bring_to_front_and_send_to_back_reorder_the_selection_as_one_undo_step()
    {
        var (client, _) = OpenHello();
        using var __ = client;
        int Index() => client.Ui(() =>
        {
            var ok = client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!;
            return ok.Parent!.Children.ToList().IndexOf(ok);
        });
        client.Ui(() => { var d = client.Server!.Document(Doc)!.Doc; d.Select(d.Form.FindControl("btnOk")!); });
        int start = Index();

        client.Send(new P.Command { DocId = Doc, Name = "SendToBack" });
        client.Settle();
        Assert.Equal(0, Index());
        client.Send(new P.Command { DocId = Doc, Name = "BringToFront" });
        client.Settle();
        int last = client.Ui(() => client.Server!.Document(Doc)!.Doc.Form.FindControl("btnOk")!.Parent!.Children.Count - 1);
        Assert.Equal(last, Index());
        client.Send(new P.Command { DocId = Doc, Name = "Undo" });
        client.Send(new P.Command { DocId = Doc, Name = "Undo" });
        client.Settle();
        Assert.Equal(start, Index());
    }

    [Fact]
    public async Task A_modal_form_becomes_a_dialog_window_over_its_owner()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        var result = new TaskCompletionSource<ModalResult>();
        client.Ui(() => client.Server!.Post(() =>
        {
            var dialog = new Form { Caption = "Question" };
            dialog.SetBounds(0, 0, 200, 100);
            dialog.ApplyScale(1);
            result.SetResult(dialog.ShowModal());   // a nested loop on the host's UI thread
        }));
        var opened = client.Expect<P.WindowOpened>(w => w.Role == P.WindowRole.Dialog);
        Assert.True(opened.Modal);
        Assert.Equal(panes.Surface, opened.OwnerWindowId);
        Assert.Equal((200, 100), (opened.Width, opened.Height));
        Assert.Equal((300, 250), (opened.X, opened.Y));   // centred on the 800 x 600 surface
        Assert.Equal("Question", opened.Title);
        Assert.Equal(200 * 100 * 4, client.Show(opened.WindowId, 200, 100).Pixels.Length);

        // While it is open, the surface takes no clicks.
        var (x, y) = client.ControlPoint(Doc, "btnOk");
        client.Click(panes.Surface, x, y);
        client.Settle();
        Assert.Empty(client.Ui(() => client.Server!.Document(Doc)!.Doc.Selection));

        client.Send(new P.CloseWindow { WindowId = opened.WindowId });
        Assert.Equal(opened.WindowId, client.Expect<P.WindowClosed>().WindowId);
        Assert.Equal(ModalResult.Cancel, await result.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void CloseDocument_closes_its_windows()
    {
        var (client, panes) = OpenHello();
        using var _ = client;
        client.Send(new P.CloseDocument { DocId = Doc });
        var closed = Enumerable.Range(0, 3).Select(_ => client.Expect<P.WindowClosed>().WindowId).Order().ToList();
        Assert.Equal(new[] { panes.Surface, panes.Inspector, panes.Palette }.Order(), closed);
        Assert.Null(client.Ui(() => client.Server!.Document(Doc)));
    }

    [Fact]
    public void Closing_the_stream_stops_the_host()
    {
        var client = new HostClient();
        client.Send(new P.Hello { ProtocolVersion = 1 });
        client.Expect<P.HelloAck>();
        client.Dispose();
        Assert.False(client.HostThread!.IsAlive);
        Assert.Null(PlatformHost.Current);
    }
}
