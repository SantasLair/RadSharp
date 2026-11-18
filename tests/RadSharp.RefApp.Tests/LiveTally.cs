using System.Globalization;

namespace RadSharp.RefApp;

/// <summary>
/// Running the committed Tally with F9 and using its live forms with simulated input: the shared helpers of the app tests.
/// A temporary copy of refapp/Tally is opened, so nothing in the repository changes.
/// </summary>
public static class LiveTally
{
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static (DesignerDriver Driver, Form Live) RunTally(double scale = 1)
    {
        var temp = RefApp.TempDir("radsharp-tally-");
        var copy = Path.Combine(temp, "Tally");
        RefApp.CopyProject(RefApp.Tally, copy);
        var d = DesignerDriver.Start(temp);
        d.OpenProject(Path.Combine(copy, "Tally.csproj"));
        Form.DefaultScale = scale;   // what a 150 % monitor gives the app at start-up
        try { return (d, d.Run()); }
        finally { Form.DefaultScale = 1; }
    }

    public static T Get<T>(Form form, string name) where T : Control => Assert.IsType<T>(form.FindControl(name), exactMatch: false);

    /// <summary>A virtual list's row text: selects the row (as a click would) and reads the selected item.</summary>
    public static string Cell(ListView lv, int row, int column)
    {
        lv.ItemIndex = row;
        return lv.Selected!.Text(column);
    }

    public static void ClickHeader(Form form, ListView lv, int column)
    {
        form.PerformLayout();
        int x = 0;
        for (int c = 0; c < column; c++) x += lv.Px(lv.ColumnWidth(c));
        var view = lv.Viewport;
        form.Simulate.Click(lv.AbsoluteLeft + view.X + x + lv.Px(20), lv.AbsoluteTop + view.Y + lv.Px(10));
    }

    /// <summary>Clicks (or right-clicks) a list row that is in view.</summary>
    public static void ClickRow(Form form, ListView lv, int index, MouseButton button = MouseButton.Left)
    {
        form.PerformLayout();
        var r = lv.ItemRect(index);
        var view = lv.Viewport;
        Assert.True(r.Y >= view.Y && r.Y + r.Height <= view.Y + view.Height, $"Row {index} of {lv.Name} isn't in view.");
        form.Simulate.Click(lv.AbsoluteLeft + r.X + lv.Px(40), lv.AbsoluteTop + r.Y + r.Height / 2, button);
    }

    /// <summary>Clicks an item of the open context menu by caption (without the &amp;).</summary>
    public static void ClickPopupItem(Form form, string caption)
    {
        var popup = form.Popups.LastOrDefault() as MenuPopup ?? throw new InvalidOperationException("No menu is open.");
        int i = popup.Items.ToList().FindIndex(m => m.DisplayText == caption);
        Assert.True(i >= 0, $"No item {caption}: {string.Join(", ", popup.Items.Select(m => m.DisplayText))}");
        var r = popup.ItemRect(i);
        form.Simulate.Click(popup.Left + r.X + 10, popup.Top + r.Y + r.Height / 2);
    }

    public static void ClickNav(Form form, string text)
    {
        var tv = Get<TreeView>(form, "tvNav");
        DesignerDriver.ClickNode(form, tv, DesignerDriver.AllNodes(tv.Nodes).Single(n => n.Text == text));
    }

    /// <summary>Clicks a button, as a person does (the centre of it).</summary>
    public static void ClickButton(Form form, string name) => form.Simulate.Click(Get<Button>(form, name));

    public static void AssertOrdered(ListView lv, int column, int rows, bool descending)
    {
        var values = Enumerable.Range(0, rows).Select(r => Cell(lv, r, column)).ToList();
        for (int i = 1; i < values.Count; i++)
        {
            int cmp = string.Compare(values[i - 1], values[i], StringComparison.CurrentCultureIgnoreCase);
            Assert.True(descending ? cmp >= 0 : cmp <= 0, $"rows {i - 1} and {i} out of order: \"{values[i - 1]}\", \"{values[i]}\"");
        }
    }

    /// <summary>Runs <paramref name="body"/> with <paramref name="culture"/> as the current culture (formats and parsing).</summary>
    public static void WithCulture(string culture, Action body)
    {
        var (before, beforeUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try { body(); }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (before, beforeUi); }
    }
}
