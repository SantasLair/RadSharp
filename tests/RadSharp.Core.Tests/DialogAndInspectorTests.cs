namespace RadSharp.Tests;

/// <summary>Windows, dialogs, the property grid and the view controls. Uses a fake platform host.</summary>
public class DialogAndInspectorTests
{
    /// <summary>A platform host whose modal loop runs a script instead of waiting for a user.</summary>
    sealed class FakePlatform : IPlatformServices
    {
        public Action<Form>? OnModal;
        public string[]? DialogResult;
        public FileDialogKind? LastDialog;
        public readonly List<Form> Open = [];

        public void ShowForm(Form form) { Open.Add(form); form.NotifyShown(); }

        public void RunModal(Form form)
        {
            ShowForm(form);
            form.PerformLayout();
            OnModal?.Invoke(form);
            if (Open.Contains(form)) form.Close();  // a script that leaves it open "closes the window"
        }

        public void CloseForm(Form form) => Open.Remove(form);
        public void ProcessEventsUntil(Func<bool> done) { }

        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<string[]?> done)
        {
            LastDialog = kind;
            done(DialogResult);
        }
    }

    static FakePlatform Install() => (FakePlatform)(PlatformHost.Current = new FakePlatform());

    [Fact]
    public void Modal_form_returns_the_result_of_the_button_pressed()
    {
        var host = Install();
        host.OnModal = f => f.Simulate.Click(f.FindControl("btnNo")!);
        var result = Dialogs.MessageDlg("Save changes?", MessageType.Confirmation, MessageButtons.YesNoCancel);
        Assert.Equal(ModalResult.No, result);
        Assert.Empty(host.Open);
    }

    [Fact]
    public void Message_form_has_a_default_and_a_cancel_button()
    {
        Install();
        var form = Dialogs.CreateMessageForm("Line one\nLine two", MessageType.Warning, MessageButtons.OkCancel);
        Assert.True(((Button)form.FindControl("btnOk")!).Default);
        Assert.True(((Button)form.FindControl("btnCancel")!).Cancel);
        Assert.Equal("Line two", ((Label)form.FindControl("lblLine1")!).Caption);
    }

    [Fact]
    public void CloseQuery_can_keep_a_modal_form_open_and_closing_it_otherwise_means_cancel()
    {
        var host = Install();
        var form = new Form();
        form.SetBounds(0, 0, 200, 100);
        bool allow = false;
        form.CloseQuery += (_, e) => e.CanClose = allow;
        host.OnModal = f =>
        {
            Assert.False(f.Close());   // vetoed
            Assert.Contains(f, host.Open);
            allow = true;
        };
        Assert.Equal(ModalResult.Cancel, form.ShowModal());
        Assert.Empty(host.Open);
    }

    [Fact]
    public void File_dialogs_return_the_chosen_paths()
    {
        var host = Install();
        var open = new OpenDialog { Filter = "Maps|*.map" };
        host.DialogResult = ["/tmp/a.map"];
        Assert.True(open.Execute());
        Assert.Equal("/tmp/a.map", open.FileName);
        Assert.Equal(FileDialogKind.Open, host.LastDialog);

        host.DialogResult = null;
        Assert.False(new SaveDialog().Execute());
        Assert.Equal(FileDialogKind.Save, host.LastDialog);
        Assert.Equal([("Maps", "*.map")], new FileDialogOptions { Filter = "Maps|*.map" }.ParseFilter());
    }

    static (Form Form, PropertyGrid Grid, Button Target) Inspector()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        var f = new Form();
        f.SetBounds(0, 0, 600, 700);
        var target = new Button { Caption = "Go" };
        target.SetBounds(400, 10, 80, 28);
        f.Add(target);
        var grid = new PropertyGrid();
        grid.SetBounds(10, 10, 300, 660);
        f.Add(grid);
        f.PerformLayout();
        grid.Target = target;
        return (f, grid, target);
    }

    static void ClickValue(Form f, PropertyGrid grid, int row)
    {
        var cell = grid.ValueRect(row);
        f.Simulate.Click(grid.AbsoluteLeft + cell.X + 10, grid.AbsoluteTop + cell.Y + cell.Height / 2);
    }

    [Fact]
    public void PropertyGrid_lists_published_properties_and_edits_text_inline()
    {
        var (f, grid, target) = Inspector();
        int caption = grid.IndexOf("caption");
        Assert.True(caption >= 0);
        Assert.Equal("Go", grid.RowValue(caption));
        Assert.Equal("80", grid.RowValue(grid.IndexOf("width")));

        ClickValue(f, grid, caption);   // select
        ClickValue(f, grid, caption);   // edit
        Assert.True(grid.IsEditing);
        f.Simulate.Type("Stop");        // replaces the selected text
        f.Simulate.Key(Key.Enter);
        Assert.False(grid.IsEditing);
        Assert.Equal("Stop", target.Caption);

        int width = grid.IndexOf("width");
        grid.BeginEdit(width);
        f.Simulate.Type("abc");
        f.Simulate.Key(Key.Enter);
        Assert.Equal(80, target.Width);   // invalid text changes nothing

        grid.BeginEdit(width);
        f.Simulate.Type("120");
        f.Simulate.Key(Key.Escape);
        Assert.Equal(80, target.Width);   // Escape cancels
    }

    [Fact]
    public void PropertyGrid_picks_enums_from_a_list_and_toggles_booleans()
    {
        var (f, grid, target) = Inspector();
        var changed = new List<string>();
        grid.PropertyChanged += (_, name) => changed.Add(name);

        grid.BeginEdit(grid.IndexOf("align"));
        var list = Assert.IsType<ListBox>(f.Popups.Single());
        var row = list.ItemRect(Array.IndexOf(list.Items, "client"));
        f.Simulate.MoveTo(list.Left + 10, list.Top + row.Y + 5);
        f.Simulate.Click(list.Left + 10, list.Top + row.Y + 5);
        Assert.Equal(Align.Client, target.Align);
        Assert.Empty(f.Popups);

        ClickValue(f, grid, grid.IndexOf("enabled"));
        Assert.False(target.Enabled);
        Assert.Equal(["align", "enabled"], changed);
    }

    [Fact]
    public void PropertyGrid_events_mode_edits_handler_names()
    {
        var (f, grid, target) = Inspector();
        grid.Mode = PropertyGridMode.Events;
        int click = grid.IndexOf("onClick");
        EventMeta? doubleClicked = null;
        grid.EventDoubleClick += (_, ev) => doubleClicked = ev;

        grid.BeginEdit(click);
        f.Simulate.Type("btnGo_Click");
        f.Simulate.Key(Key.Enter);
        Assert.Equal("btnGo_Click", target.EventHandlers["onClick"]);

        var cell = grid.ValueRect(click);
        f.Simulate.DoubleClick(grid.AbsoluteLeft + cell.X + 10, grid.AbsoluteTop + cell.Y + 5);
        Assert.Equal("onClick", doubleClicked?.Name);
    }

    [Fact]
    public void PropertyGrid_shows_components_only_their_own_properties()
    {
        var grid = new PropertyGrid { Target = new Timer() };
        var names = Enumerable.Range(0, grid.RowCount).Select(grid.RowName).ToList();
        Assert.Equal(["enabled", "interval"], names);   // Timer redeclares "enabled"; no left/align/anchors...
        grid.Mode = PropertyGridMode.Events;
        Assert.Equal(["onTimer"], Enumerable.Range(0, grid.RowCount).Select(grid.RowName));
    }

    [Fact]
    public void ListView_columns_rows_and_header_clicks()
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var lv = new ListView { Columns = ["Name:120", "Size:60", "Kind"] };
        lv.SetBounds(0, 0, 400, 300);
        f.Add(lv);
        lv.Items.Add(new ListViewItem("a.map", "12 KB", "Map"));
        lv.Items.Add(new ListViewItem("b.png", "3 KB", "Image"));
        f.PerformLayout();
        int? clicked = null;
        lv.ColumnClick += (_, c) => clicked = c;

        Assert.Equal("Name", lv.ColumnCaption(0));
        Assert.Equal((120, 60, 100), (lv.ColumnWidth(0), lv.ColumnWidth(1), lv.ColumnWidth(2)));
        f.Simulate.Click(150, 10);                 // header of "Size"
        Assert.Equal(1, clicked);
        f.Simulate.Drag(121, 10, 161, 10);          // widen "Name" by dragging its border
        Assert.Equal(160, lv.ColumnWidth(0));
        f.Simulate.Click(20, 26 + 24 + 10);          // second row
        Assert.Equal("b.png", lv.Selected?.Caption);
    }

    [Fact]
    public void TreeView_expands_collapses_and_navigates_with_keys()
    {
        var f = new Form();
        f.SetBounds(0, 0, 300, 300);
        var tree = new TreeView();
        tree.SetBounds(0, 0, 300, 300);
        f.Add(tree);
        var maps = tree.Add("Maps");
        maps.Add("level1.map");
        maps.Add("level2.map");
        tree.Add("Sprites");
        tree.Focus();
        f.PerformLayout();

        tree.Selected = maps;
        Assert.Equal(2, tree.VisibleNodes().Count);
        f.Simulate.Key(Key.Right);                 // expand
        Assert.Equal(4, tree.VisibleNodes().Count);
        f.Simulate.Key(Key.Right);                 // to first child
        Assert.Equal("level1.map", tree.Selected?.Text);
        f.Simulate.Key(Key.Left);                  // to parent
        f.Simulate.Key(Key.Left);                  // collapse
        Assert.Equal(2, tree.VisibleNodes().Count);
        f.Simulate.Key(Key.End);
        Assert.Equal("Sprites", tree.Selected?.Text);
    }
}
