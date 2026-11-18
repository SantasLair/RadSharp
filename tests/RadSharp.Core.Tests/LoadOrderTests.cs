namespace RadSharp.Tests;

/// <summary>
/// An index member (itemIndex, activePageIndex) depends on items or pages that the writer saves after it
/// (members are written alphabetically, children last), so the reader applies it once the control is loaded.
/// </summary>
public class LoadOrderTests
{
    const string Header = "\"format\": \"radform\", \"version\": \"1.0\"";

    static Form RoundTrip(Form form) => FormReader.Load(FormWriter.Save(form)).Form;

    [Fact]
    public void ListBox_itemIndex_survives_save_and_load()
    {
        var form = new Form { Name = "f" };
        var lb = new ListBox { Name = "lb", Items = ["a", "b", "c"] };
        form.Add(lb);
        lb.ItemIndex = 2;
        var loaded = (ListBox)RoundTrip(form).FindControl("lb")!;
        Assert.Equal(2, loaded.ItemIndex);
    }

    [Fact]
    public void CheckListBox_itemIndex_survives_save_and_load()
    {
        var form = new Form { Name = "f" };
        var lb = new CheckListBox { Name = "lb", Items = ["a", "b", "c"] };
        form.Add(lb);
        lb.ItemIndex = 1;
        var loaded = (CheckListBox)RoundTrip(form).FindControl("lb")!;
        Assert.Equal(1, loaded.ItemIndex);
    }

    [Fact]
    public void ComboBox_itemIndex_survives_save_and_load()
    {
        var form = new Form { Name = "f" };
        var cb = new ComboBox { Name = "cb", Items = ["a", "b", "c"] };
        form.Add(cb);
        cb.ItemIndex = 1;
        var loaded = (ComboBox)RoundTrip(form).FindControl("cb")!;
        Assert.Equal(1, loaded.ItemIndex);
    }

    [Fact]
    public void PageControl_activePageIndex_survives_save_and_load()
    {
        var form = new Form { Name = "f" };
        var pc = new PageControl { Name = "pc" };
        form.Add(pc);
        pc.Add(new TabSheet { Name = "p0", Caption = "One" });
        pc.Add(new TabSheet { Name = "p1", Caption = "Two" });
        pc.ActivePageIndex = 1;
        var loaded = (PageControl)RoundTrip(form).FindControl("pc")!;
        Assert.Equal(1, loaded.ActivePageIndex);
    }

    [Fact]
    public void Index_before_items_in_a_hand_written_file_still_applies()
    {
        var (form, warnings) = FormReader.Load($$"""
            { {{Header}}, "root": { "class": "Form", "children": [
                { "class": "ListBox", "name": "lb", "itemIndex": 1, "items": ["x", "y"] },
                { "class": "ComboBox", "name": "cb", "itemIndex": 0, "items": ["x"] } ] } }
            """);
        Assert.Empty(warnings);
        Assert.Equal(1, ((ListBox)form.FindControl("lb")!).ItemIndex);
        Assert.Equal(0, ((ComboBox)form.FindControl("cb")!).ItemIndex);
    }

    [Fact]
    public void Index_past_the_end_is_clamped_after_loading()
    {
        var (form, _) = FormReader.Load($$"""
            { {{Header}}, "root": { "class": "Form", "children": [
                { "class": "ListBox", "name": "lb", "itemIndex": 7, "items": ["x", "y"] } ] } }
            """);
        Assert.Equal(1, ((ListBox)form.FindControl("lb")!).ItemIndex);
    }

    [Fact]
    public void Loading_is_true_only_while_the_reader_runs()
    {
        var (form, _) = FormReader.Load($$"""
            { {{Header}}, "root": { "class": "Form", "children": [ { "class": "ListBox", "name": "lb" } ] } }
            """);
        Assert.False(form.Loading);
        Assert.False(form.FindControl("lb")!.Loading);
        Assert.False(new ListBox().Loading);
    }
}

public class LoadOrderDefectTests
{
    const string Header = "\"format\": \"radform\", \"version\": \"1.0\"";

    [Fact]
    public void ListBox_itemIndex_is_scrolled_into_view_at_a_scaled_load()
    {
        var items = string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"i{i}\""));
        var json = $$"""
            { {{Header}}, "root": { "class": "Form", "width": 300, "height": 300, "children": [
                { "class": "ListBox", "name": "lb", "width": 160, "height": 120, "itemIndex": 95, "items": [{{items}}] } ] } }
            """;
        foreach (var scale in new[] { 1.0, 2.0 })
        {
            var (form, _) = FormReader.Load(json, new FormLoadOptions { Scale = scale });
            var lb = (ListBox)form.FindControl("lb")!;
            var r = lb.ItemRect(95);
            var v = lb.Viewport;
            Assert.True(r.Y >= v.Y && r.Y + r.Height <= v.Y + v.Height, $"scale {scale}: row 95 at {r.Y}+{r.Height}, viewport {v.Y}+{v.Height}");
        }
    }

    [Fact]
    public void Editable_combo_text_matching_item_round_trips()
    {
        var form = new Form { Name = "f" };
        var cb = new ComboBox { Name = "cb", Items = ["a", "b", "c"], Style = ComboBoxStyle.DropDown };
        form.Add(cb);
        cb.Text = "c";
        var loaded = (ComboBox)FormReader.Load(FormWriter.Save(form)).Form.FindControl("cb")!;
        Assert.Equal("c", loaded.Text);
        Assert.Equal(2, loaded.ItemIndex);
    }

    [Fact]
    public void Editable_combo_custom_text_survives_stale_itemIndex()
    {
        var (form, _) = FormReader.Load($$"""
            { {{Header}}, "root": { "class": "Form", "children": [
                { "class": "ComboBox", "name": "cb", "style": "dropDown", "itemIndex": 0, "items": ["x", "y"], "text": "custom" } ] } }
            """);
        Assert.Equal("custom", ((ComboBox)form.FindControl("cb")!).Text);
    }

}
