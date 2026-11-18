using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>ListView v2 (M14 item 3): columns, sorting, check boxes, virtual mode, type-ahead, form files, and the v1 API.</summary>
public class ListViewTests
{
    const KeyModifiers Ctrl = KeyModifiers.Ctrl, Shift = KeyModifiers.Shift;

    public ListViewTests() => Shortcut.CommandModifier = KeyModifiers.Ctrl;

    // A 400 × 300 view at the form's origin: the header is y 1–26, row i spans 27 + 24·i.
    static (Form, ListView) Make(Action<ListView>? setup = null)
    {
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        var lv = new ListView { Columns = ["Tile:100", "Cost:60", "Biome:100"] };
        lv.SetBounds(0, 0, 400, 300);
        f.Add(lv);
        string[] tiles = ["Grass", "Water", "Sand", "Stone", "Lava", "Snow"];
        string[] costs = ["10", "2", "10", "100", "7", "2"];
        string[] biomes = ["Plains", "Coast", "Desert", "Hills", "Volcano", "Tundra"];
        for (int i = 0; i < tiles.Length; i++) lv.Items.Add(new ListViewItem(tiles[i], costs[i], biomes[i]));
        setup?.Invoke(lv);
        f.PerformLayout();
        return (f, lv);
    }

    static int RowY(int i) => 27 + 24 * i + 12;

    static string[] Captions(ListView lv) => [.. lv.Items.Select(i => i.Caption)];

    [Fact]
    public void The_v1_api_still_works_columns_from_strings_items_selection_and_events()
    {
        var (f, lv) = Make();
        int changes = 0, doubleClicks = 0;
        int? clicked = null;
        lv.Change += _ => changes++;
        lv.DoubleClick += _ => doubleClicks++;
        lv.ColumnClick += (_, c) => clicked = c;

        Assert.Equal(["Tile", "Cost", "Biome"], Enumerable.Range(0, 3).Select(lv.ColumnCaption));
        Assert.Equal((100, 60, 100), (lv.ColumnWidth(0), lv.ColumnWidth(1), lv.ColumnWidth(2)));
        lv.Columns = ["A", "B:abc", "C:42"];   // no width or an unreadable one: 100, as before
        Assert.Equal(("A", 100, "B", 100, 42), (lv.ColumnCaption(0), lv.ColumnWidth(0), lv.ColumnCaption(1), lv.ColumnWidth(1), lv.ColumnWidth(2)));

        f.Simulate.Click(150, 10);
        Assert.Equal(1, clicked);
        Assert.Equal("Grass", lv.Items[0].Caption);   // without AutoSort a header click doesn't reorder

        lv.ItemIndex = 2;
        Assert.Equal("Sand", lv.Selected?.Caption);
        Assert.Equal(1, changes);
        Assert.Equal(-1, lv.IndexAt(10));             // the header
        Assert.Equal(1, lv.IndexAt(RowY(1)));
        lv.Focus();
        f.Simulate.Key(Key.Enter);
        Assert.Equal(1, doubleClicks);

        lv.Items.RemoveRange(1, 5);
        lv.ItemsChanged();
        Assert.Equal(0, lv.ItemIndex);
        f.Simulate.DoubleClick(20, RowY(0));
        Assert.Equal(2, doubleClicks);
    }

    [Fact]
    public void The_header_stays_on_top_and_scrolls_sideways_with_the_columns()
    {
        var (f, lv) = Make(v =>
        {
            v.Columns = ["Tile:200", "Cost:150", "Biome:200"];
            for (int i = 0; i < 40; i++) v.Items.Add(new ListViewItem($"Extra {i}"));
        });
        Assert.True(lv.HorizontalBarVisible && lv.VerticalBarVisible);
        Assert.Equal(26 + 46 * 24, lv.ContentHeight);
        Assert.Equal(550, lv.ContentWidth);

        int? clicked = null;
        lv.ColumnClick += (_, c) => clicked = c;
        f.Simulate.Wheel(100, RowY(2), -2);
        Assert.Equal(144, lv.ScrollY);
        f.Simulate.Click(250, 10);                  // the header didn't move down
        Assert.Equal(1, clicked);

        f.Simulate.Wheel(100, RowY(2), -1);
        lv.Focus();
        f.Simulate.Key(Key.Right);                  // Left/Right scroll sideways
        f.Simulate.Key(Key.Right);
        Assert.Equal(32, lv.ScrollX);
        lv.ScrollX = 150;
        f.Simulate.Click(120, 10);                  // content x 269: still "Cost"
        Assert.Equal(1, clicked);
        f.Simulate.Click(230, 10);                  // content x 379: "Biome"
        Assert.Equal(2, clicked);

        // A keyboard move keeps the row clear of the pinned header.
        f.Simulate.Click(20, RowY(0));
        int top = lv.TopIndex;
        f.Simulate.Key(Key.Up);
        Assert.Equal(top - 1, lv.ItemIndex);
        Assert.True(lv.ItemRect(lv.ItemIndex).Y >= lv.Viewport.Y + 26);
    }

    [Fact]
    public void AutoSort_header_clicks_sort_stably_reverse_and_keep_the_selection_on_its_items()
    {
        var (f, lv) = Make(v => { v.AutoSort = true; v.MultiSelect = true; });
        var sand = lv.Items[2];
        var lava = lv.Items[4];
        lv.SetSelected(2, true);
        lv.SetSelected(4, true);
        lv.Selection.Current = 4;
        var clicks = new List<(int Col, int Sort, bool Desc)>();
        lv.ColumnClick += (_, c) => clicks.Add((c, lv.SortColumn, lv.SortDescending));

        f.Simulate.Click(130, 10);                  // Cost: numbers by value, ties keep their order
        Assert.Equal(["Water", "Snow", "Lava", "Grass", "Sand", "Stone"], Captions(lv));
        Assert.Equal((1, false), (lv.SortColumn, lv.SortDescending));
        Assert.Equal([2, 4], lv.SelectedIndices);   // Lava and Sand, wherever they went
        Assert.Same(lava, lv.Selected);
        Assert.Same(sand, lv.Items[4]);

        f.Simulate.Click(130, 10);                  // again: descending, still stable
        Assert.Equal(["Stone", "Grass", "Sand", "Lava", "Water", "Snow"], Captions(lv));
        Assert.Equal([(1, 1, false), (1, 1, true)], clicks);

        lv.Compare += (_, e) => e.Result = e.Item1.Caption.Length.CompareTo(e.Item2.Caption.Length);
        lv.SortItems(0);
        Assert.Equal(["Sand", "Lava", "Snow", "Stone", "Grass", "Water"], Captions(lv));
        Assert.Equal((0, false), (lv.SortColumn, lv.SortDescending));
    }

    [Fact]
    public void In_virtual_mode_a_header_click_moves_the_arrow_and_the_app_sorts()
    {
        var (f, lv) = Make(v => v.AutoSort = true);
        var order = Enumerable.Range(0, 1000).ToArray();
        lv.GetItem += (_, e) => e.Item.Caption = $"Row {order[e.Index]}";
        lv.ItemCount = 1000;
        lv.ColumnClick += (_, _) => Array.Reverse(order);
        Assert.True(lv.IsVirtual);
        Assert.Equal(1000, lv.ItemCount);

        f.Simulate.Click(20, 10);
        Assert.Equal((0, false), (lv.SortColumn, lv.SortDescending));
        lv.ItemIndex = 0;
        Assert.Equal("Row 999", lv.Selected?.Caption);
        Assert.Equal("Grass", lv.Items[0].Caption);   // the in-memory items are left alone
    }

    [Fact]
    public void SortColumn_is_kept_until_columns_shrink_past_it_like_the_grids()
    {
        var lv = new ListView { SortColumn = 2 };     // before the columns exist
        Assert.Equal(2, lv.SortColumn);
        lv.Columns.Add("A");
        lv.Columns.Add("B");
        lv.Columns.Add("C");
        Assert.Equal(2, lv.SortColumn);
        lv.Columns.RemoveAt(2);
        Assert.Equal(-1, lv.SortColumn);
        lv.Columns.Add("C");
        Assert.Equal(-1, lv.SortColumn);              // growing never brings it back
        lv.SortColumn = 1;
        lv.Columns = ["X", "Y", "Z", "W"];            // replacing with more columns keeps it
        Assert.Equal(1, lv.SortColumn);
        lv.Columns = ["X"];
        Assert.Equal(-1, lv.SortColumn);
    }

    [Fact]
    public void Check_boxes_toggle_from_a_click_or_space_without_moving_the_selection()
    {
        var (f, lv) = Make(v => { v.Checkboxes = true; v.MultiSelect = true; });
        var events = new List<(int, bool)>();
        lv.ItemChecked += (_, i, on) => events.Add((i, on));
        lv.ItemIndex = 0;

        f.Simulate.Click(12, RowY(3));             // on the box
        Assert.True(lv.Items[3].Checked);
        Assert.Equal(0, lv.ItemIndex);
        Assert.Equal([(3, true)], events);
        f.Simulate.Click(12, RowY(3));
        Assert.False(lv.Items[3].Checked);

        // Space toggles the current item, and every selected item with it.
        lv.Focus();
        f.Simulate.Click(80, RowY(1));
        f.Simulate.Click(80, RowY(2), MouseButton.Left, Shift);
        events.Clear();
        f.Simulate.Key(Key.Space);
        Assert.True(lv.Items[1].Checked && lv.Items[2].Checked);
        Assert.Equal([(1, true), (2, true)], events);

        // Virtual mode: the handler gets the new state and stores it.
        var checks = new bool[100];
        lv.GetItem += (_, e) => { e.Item.Caption = $"V{e.Index}"; e.Item.Checked = checks[e.Index]; };
        lv.ItemChecked += (_, i, on) => checks[i] = on;
        lv.ItemCount = 100;
        f.PerformLayout();
        f.Simulate.Click(12, RowY(4));
        Assert.True(checks[4]);
    }


    [Fact]
    public void Type_ahead_in_a_100000_row_virtual_list_asks_for_a_bounded_number_of_rows()
    {
        var (f, lv) = Make();
        int calls = 0;
        lv.GetItem += (_, e) => { calls++; e.Item.Caption = $"Row {e.Index}"; };
        lv.ItemCount = 100_000;
        f.PerformLayout();
        lv.Focus();
        calls = 0;
        f.Simulate.Type("z");                      // matches nothing: the whole list would be scanned
        Assert.InRange(calls, 0, 5_000);
        Assert.Equal(-1, lv.ItemIndex);
    }

    [Fact]
    public void Type_ahead_in_a_virtual_list_still_finds_a_near_match_with_a_bounded_scan()
    {
        var (f, lv) = Make();
        int calls = 0;
        lv.GetItem += (_, e) => { calls++; e.Item.Caption = $"Row {e.Index}"; };
        lv.ItemCount = 100_000;
        f.PerformLayout();
        lv.Focus();
        calls = 0;
        f.Simulate.Type("r");
        Assert.InRange(calls, 0, 5_000);
        Assert.Equal(0, lv.ItemIndex);
    }

    [Fact]
    public void A_FindItem_handler_answers_type_ahead_in_a_virtual_list_without_scanning()
    {
        var (f, lv) = Make();
        int calls = 0;
        lv.GetItem += (_, e) => { calls++; e.Item.Caption = $"Row {e.Index}"; };
        lv.ItemCount = 100_000;
        var asked = new List<(string, int)>();
        lv.FindItem += (_, e) => { asked.Add((e.Text, e.StartIndex)); e.Index = 77_777; };
        f.PerformLayout();
        lv.Focus();
        calls = 0;
        f.Simulate.Type("r");
        Assert.Equal(77_777, lv.ItemIndex);
        Assert.Equal([("r", 0)], asked);
        Assert.InRange(calls, 0, 100);             // painting the new view only
    }
    [Fact]
    public void Images_come_from_the_named_image_list()
    {
        var (f, lv) = Make();
        var png = ViewImages.Square(SKColorsRed());
        var images = new ImageList { Name = "icons", Images = [png] };
        f.Add(images);
        lv.ImagesName = "icons";
        lv.Items[0].ImageIndex = 0;
        using var after = FormRenderer.RenderToBitmap(f);
        // The picture sits after the left padding, centred in row 0.
        var pixel = after.GetPixel(6 + 8, 27 + 12);
        Assert.True(pixel.Red > 200 && pixel.Green < 60, $"expected the red image, got {pixel}");
    }

    static SkiaSharp.SKColor SKColorsRed() => new(230, 40, 40);

    [Fact]
    public void Typing_jumps_to_matching_captions_and_repeating_a_letter_cycles()
    {
        var (f, lv) = Make();
        lv.Focus();
        f.Simulate.Type("s");
        Assert.Equal("Sand", lv.Selected?.Caption);
        f.Simulate.Type("t");                      // "st"
        Assert.Equal("Stone", lv.Selected?.Caption);
        f.ProcessTick(5000);                       // a pause starts a new search
        f.Simulate.Type("s");
        Assert.Equal("Snow", lv.Selected?.Caption);
        f.Simulate.Type("s");                      // "ss": the next one starting with s
        Assert.Equal("Sand", lv.Selected?.Caption);
        f.Simulate.Key(Key.Down);                  // a key ends the search
        f.Simulate.Type("w");
        Assert.Equal("Water", lv.Selected?.Caption);
    }

    [Fact]
    public void Header_borders_resize_columns_down_to_their_minimum_and_widths_stay_at_100_percent()
    {
        var (f, lv) = Make();
        f.Simulate.Drag(101, 10, 141, 10);
        Assert.Equal(140, lv.ColumnWidth(0));
        Assert.Equal(140, lv.Columns[0].Width);
        f.Simulate.Drag(141, 10, 0, 10);
        Assert.Equal(20, lv.ColumnWidth(0));       // the old floor
        lv.Columns[1].MinWidth = 50;
        f.Simulate.Drag(81, 10, 30, 10);
        Assert.Equal(50, lv.ColumnWidth(1));

        var big = new Form();
        big.SetBounds(0, 0, 600, 450);
        var scaled = new ListView { Columns = ["A:100", "B:100"] };
        scaled.SetBounds(0, 0, 400, 300);
        big.Add(scaled);
        big.ApplyScale(1.5);
        big.PerformLayout();
        big.Simulate.Drag(152, 10, 182, 10);        // 30 px at 150 %
        Assert.Equal(120, scaled.ColumnWidth(0));
    }

    [Fact]
    public void Columns_round_trip_through_a_form_file_and_old_string_columns_still_load()
    {
        const string json = """
            {
              "format": "radform", "version": "1.0",
              "root": { "class": "Form", "name": "frm", "width": 400, "height": 300, "children": [
                { "class": "ListView", "name": "lvOld", "columns": ["Name:120", "Size:60", "Kind"], "left": 0, "top": 0 },
                { "class": "ListView", "name": "lvNew", "sortColumn": 1, "sortDescending": true, "autoSort": true,
                  "checkboxes": true, "multiSelect": true, "images": "icons", "onGetItem": "lvNew_GetItem",
                  "onItemChecked": "lvNew_ItemChecked", "onCompare": "lvNew_Compare",
                  "columns": [ { "title": "Tile", "width": 90 }, { "title": "Cost", "alignment": "right", "minWidth": 40 } ],
                  "left": 0, "top": 150 },
                { "class": "ListView", "name": "lvBad", "sortColumn": 3, "columns": [ { "title": "Only" } ], "left": 200, "top": 0 }
              ] }
            }
            """;
        var (form, warnings) = FormReader.Load(json);
        Assert.Equal(["lvBad.sortColumn: column 3 doesn't exist (1 column); ignored."], warnings);
        var old = (ListView)form.FindControl("lvOld")!;
        Assert.Equal(["Name", "Size", "Kind"], old.Columns.Select(c => c.Title));
        Assert.Equal([120, 60, 100], Enumerable.Range(0, 3).Select(old.ColumnWidth));
        var lv = (ListView)form.FindControl("lvNew")!;
        Assert.Equal((1, true, true, true, true, "icons"), (lv.SortColumn, lv.SortDescending, lv.AutoSort, lv.Checkboxes, lv.MultiSelect, lv.ImagesName));
        Assert.Equal((TextAlign.Right, 40), (lv.Columns[1].Alignment, lv.Columns[1].MinWidth));
        Assert.Equal("lvNew_GetItem", lv.EventHandlers["onGetItem"]);
        Assert.Equal(-1, ((ListView)form.FindControl("lvBad")!).SortColumn);

        var saved = FormWriter.Save(form);
        Assert.Contains("\"title\": \"Kind\"", saved);              // old columns are rewritten as objects
        Assert.DoesNotContain("Name:120", saved);
        Assert.Equal(saved, FormWriter.Save(FormReader.Load(saved).Form));

        var inspector = new PropertyGrid();
        inspector.SetTargets([lv]);
        int row = inspector.IndexOf("columns");
        Assert.True(inspector.IsCollection(row));
        Assert.Equal("(2 columns)", inspector.RowValue(row));
    }

    [Fact]
    public void A_column_belongs_to_one_view_and_assigning_columns_copies_owned_ones()
    {
        var a = new ListView { Columns = ["A:50"] };
        var b = new ListView { Columns = a.Columns };
        Assert.Equal("A", b.ColumnCaption(0));
        Assert.NotSame(a.Columns[0], b.Columns[0]);
        Assert.Throws<InvalidOperationException>(() => b.Columns.Add(a.Columns[0]));
        GridColumns free = ["X:10", new GridColumn("Y", 20, TextAlign.Center)];
        Assert.Equal((10, TextAlign.Center), (free[0].Width, free[1].Alignment));
    }

    [Fact]
    public void Sorting_keeps_a_toggle_mode_selection_too()
    {
        var (f, lv) = Make();
        lv.Selection.Mode = SelectionMode.Multi;
        lv.SetSelected(0, true);                       // Grass
        lv.SetSelected(3, true);                       // Stone
        lv.SortItems(0);
        Assert.Equal(["Grass", "Stone"], lv.SelectedIndices.Select(i => lv.Items[i].Caption));
    }
}

/// <summary>Small generated pictures for image-list tests (PNG data URIs).</summary>
internal static class ViewImages
{
    public static string Square(SkiaSharp.SKColor color, bool round = false)
    {
        using var bmp = new SkiaSharp.SKBitmap(16, 16);
        using (var c = new SkiaSharp.SKCanvas(bmp))
        {
            c.Clear(SkiaSharp.SKColors.Transparent);
            using var paint = new SkiaSharp.SKPaint { Color = color, IsAntialias = false };
            if (round) c.DrawCircle(8, 8, 7, paint);
            else c.DrawRect(2, 2, 12, 12, paint);
        }
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return Picture.ToDataUri(data.ToArray());
    }
}
