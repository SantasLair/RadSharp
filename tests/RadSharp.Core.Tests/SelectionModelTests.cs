namespace RadSharp.Tests;

/// <summary>ItemSelection and CellSelection: the UI-free selection models behind lists and grids.</summary>
public class SelectionModelTests
{
    static ItemSelection Items(int count, SelectionMode mode, out List<string> log)
    {
        var s = new ItemSelection(mode) { Count = count };
        var events = new List<string>();
        s.Changed += () => events.Add("changed");
        log = events;
        return s;
    }

    static IndexRange[] R(params (int First, int Last)[] ranges) => [.. ranges.Select(r => new IndexRange(r.First, r.Last))];

    // ---- ItemSelection ----

    [Fact]
    public void Single_mode_keeps_at_most_the_current_item_selected()
    {
        var s = Items(10, SelectionMode.Single, out var log);
        Assert.Equal(-1, s.Current);
        Assert.True(s.IsEmpty);

        s.Click(3, toggle: true, extend: true);     // modifiers mean nothing here
        Assert.Equal(3, s.Current);
        Assert.Equal(R((3, 3)), s.Ranges);
        s.MoveTo(5, extend: true);
        Assert.Equal(R((5, 5)), s.Ranges);
        s.Current = 7;                               // the selection follows the current item
        Assert.Equal(R((7, 7)), s.Ranges);
        s.SelectAll();                               // no-op in single mode
        Assert.Equal(1, s.SelectedCount);
        s.SetSelected(2, true);
        Assert.Equal(R((2, 2)), s.Ranges);
        Assert.Equal(2, s.Current);
        Assert.Equal(4, log.Count);

        s.Select(2);                                 // nothing changed: no event
        Assert.Equal(4, log.Count);

        s.ClearSelection();
        Assert.Equal(-1, s.Current);
        Assert.True(s.IsEmpty);
        Assert.Equal(5, log.Count);
    }

    [Fact]
    public void Extended_clicks_select_toggle_and_span_ranges_from_the_anchor()
    {
        var s = Items(20, SelectionMode.Extended, out var log);
        s.Click(2);
        Assert.Equal(R((2, 2)), s.Ranges);
        Assert.Equal(2, s.Anchor);

        s.Click(5, toggle: true);                    // Ctrl+click adds and moves the anchor
        Assert.Equal(R((2, 2), (5, 5)), s.Ranges);
        Assert.Equal(5, s.Anchor);

        s.Click(8, extend: true);                    // Shift+click: anchor..8, replacing the rest
        Assert.Equal(R((5, 8)), s.Ranges);
        Assert.Equal(5, s.Anchor);
        Assert.Equal(8, s.Current);

        s.Click(3, extend: true);                    // the range pivots on the anchor
        Assert.Equal(R((3, 5)), s.Ranges);

        s.Click(12, toggle: true);
        s.Click(14, toggle: true, extend: true);     // Ctrl+Shift+click adds anchor..14 to what is there
        Assert.Equal(R((3, 5), (12, 14)), s.Ranges);

        s.Click(4, toggle: true);                    // Ctrl+click on a selected item removes it, splitting the range
        Assert.Equal(R((3, 3), (5, 5), (12, 14)), s.Ranges);
        Assert.Equal(5, s.SelectedCount);
        Assert.Equal([3, 5, 12, 13, 14], s.SelectedIndices);
        Assert.Equal(7, log.Count);
    }

    [Fact]
    public void Extended_drags_sweep_add_or_remove_a_range()
    {
        var s = Items(20, SelectionMode.Extended, out var log);
        s.Click(4);
        s.DragTo(7);
        Assert.Equal(R((4, 7)), s.Ranges);
        s.DragTo(2);                                 // back past the anchor
        Assert.Equal(R((2, 4)), s.Ranges);
        s.DragTo(-50);                               // clamped to the items
        Assert.Equal(R((0, 4)), s.Ranges);
        int events = log.Count;
        s.DragTo(0);                                 // no movement, no event
        Assert.Equal(events, log.Count);

        s.Click(10, toggle: true);                   // a Ctrl+press that selected its item: the drag adds
        s.DragTo(12);
        Assert.Equal(R((0, 4), (10, 12)), s.Ranges);
        s.DragTo(10);
        Assert.Equal(R((0, 4), (10, 10)), s.Ranges);  // and gives back what it swept past

        s.Click(2, toggle: true);                    // a Ctrl+press that deselected: the drag removes
        s.DragTo(3);
        Assert.Equal(R((0, 1), (4, 4), (10, 10)), s.Ranges);
    }

    [Fact]
    public void Extended_keyboard_moves_extends_and_moves_the_focus_alone()
    {
        var s = Items(20, SelectionMode.Extended, out _);
        s.MoveTo(5);
        s.MoveTo(6, extend: true);
        s.MoveTo(8, extend: true);
        Assert.Equal(R((5, 8)), s.Ranges);
        Assert.Equal(8, s.Current);

        s.MoveTo(12, focusOnly: true);               // Ctrl+arrow: only the focus moves
        Assert.Equal(R((5, 8)), s.Ranges);
        Assert.Equal(12, s.Current);
        s.ToggleCurrent();                           // Ctrl+Space
        Assert.Equal(R((5, 8), (12, 12)), s.Ranges);
        Assert.Equal(12, s.Anchor);

        s.MoveTo(14, extend: true, focusOnly: true); // Ctrl+Shift+arrow adds the range
        Assert.Equal(R((5, 8), (12, 14)), s.Ranges);
        s.MoveTo(10, extend: true);                  // Shift+arrow replaces it with anchor..10
        Assert.Equal(R((10, 12)), s.Ranges);
        s.MoveTo(0);
        Assert.Equal(R((0, 0)), s.Ranges);
    }

    [Fact]
    public void Multi_mode_toggles_one_item_per_click_and_the_keys_move_only_the_focus()
    {
        var s = Items(10, SelectionMode.Multi, out _);
        s.Click(1);
        s.Click(3, extend: true);                    // Shift doesn't span ranges here
        s.Click(5);
        Assert.Equal(R((1, 1), (3, 3), (5, 5)), s.Ranges);
        s.Click(3);
        Assert.Equal(R((1, 1), (5, 5)), s.Ranges);
        s.DragTo(9);                                 // no sweeping
        Assert.Equal(R((1, 1), (5, 5)), s.Ranges);

        s.MoveTo(6);
        Assert.Equal(6, s.Current);
        Assert.Equal(R((1, 1), (5, 5)), s.Ranges);
        s.ToggleCurrent();                           // Space
        Assert.Equal(R((1, 1), (5, 6)), s.Ranges);
        s.SelectAll();
        Assert.Equal(R((0, 9)), s.Ranges);
    }

    [Fact]
    public void None_mode_moves_the_current_item_and_never_selects()
    {
        var s = Items(10, SelectionMode.None, out _);
        s.Click(4, toggle: true);
        s.MoveTo(6, extend: true);
        s.SetSelected(2, true);
        s.SelectAll();
        Assert.Equal(6, s.Current);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Adjacent_selections_merge_and_removals_split_ranges()
    {
        var s = Items(30, SelectionMode.Extended, out _);
        s.SetSelected(5, true);
        s.SetSelected(7, true);
        Assert.Equal(R((5, 5), (7, 7)), s.Ranges);
        s.SetSelected(6, true);                      // fills the gap: one range
        Assert.Equal(R((5, 7)), s.Ranges);
        s.SelectRange(8, 10, add: true);             // touches the end: merged
        Assert.Equal(R((5, 10)), s.Ranges);
        Assert.Equal(8, s.Anchor);
        Assert.Equal(10, s.Current);
        s.SelectRange(20, 2, add: true);             // swallows everything in between; either order
        Assert.Equal(R((2, 20)), s.Ranges);
        s.SetSelected(11, false);
        Assert.Equal(R((2, 10), (12, 20)), s.Ranges);
        Assert.True(s.IsSelected(10));
        Assert.False(s.IsSelected(11));
        Assert.True(s.IsSelected(12));
        Assert.False(s.IsSelected(21));
        s.SelectRange(0, 3);                         // without add: replaces
        Assert.Equal(R((0, 3)), s.Ranges);
    }

    [Fact]
    public void Select_all_on_a_hundred_thousand_items_is_one_range()
    {
        var s = Items(100_000, SelectionMode.Extended, out var log);
        s.SelectAll();
        Assert.Single(s.Ranges);
        Assert.Equal(100_000, s.SelectedCount);
        Assert.True(s.IsSelected(99_999));
        Assert.Single(log);

        s.Toggle(50_000);
        Assert.Equal(R((0, 49_999), (50_001, 99_999)), s.Ranges);

        // Every other item: 50 000 ranges, still answered by binary search.
        s.ClearSelection();
        for (int i = 0; i < 100_000; i += 2) s.SetSelected(i, true);
        Assert.Equal(50_000, s.Ranges.Count);
        Assert.True(s.IsSelected(77_776));
        Assert.False(s.IsSelected(77_777));
        s.SelectAll();
        Assert.Single(s.Ranges);

        s.Count = 10;                                // truncating drops the tail
        Assert.Equal(R((0, 9)), s.Ranges);
    }

    [Fact]
    public void Inserting_items_moves_and_splits_the_selection_with_its_items()
    {
        var s = Items(10, SelectionMode.Extended, out var log);
        s.SelectRange(2, 5);                         // anchor 2, current 5
        s.SetSelected(8, true);
        log.Clear();

        s.Insert(4, 2);                              // inside the range: the new items are not selected
        Assert.Equal(12, s.Count);
        Assert.Equal(R((2, 3), (6, 7), (10, 10)), s.Ranges);
        Assert.Equal(2, s.Anchor);
        Assert.Equal(7, s.Current);
        Assert.Single(log);

        s.Insert(0);
        Assert.Equal(R((3, 4), (7, 8), (11, 11)), s.Ranges);
        s.Insert(13);                                // at the end: nothing moves
        Assert.Equal(R((3, 4), (7, 8), (11, 11)), s.Ranges);
        Assert.Equal(14, s.Count);
    }

    [Fact]
    public void Removing_items_drops_their_selection_and_closes_the_gap()
    {
        var s = Items(12, SelectionMode.Extended, out _);
        s.SelectRange(1, 3);
        s.SelectRange(6, 8, add: true);
        s.SetSelected(10, true);

        s.Remove(4, 2);                              // the unselected gap: ranges become adjacent and merge
        Assert.Equal(10, s.Count);
        Assert.Equal(R((1, 6), (8, 8)), s.Ranges);
        Assert.Equal(6, s.Current);                  // was 8

        s.Remove(5, 4);                              // takes the current item (6) and part of the range
        Assert.Equal(R((1, 4)), s.Ranges);
        Assert.Equal(5, s.Current);                  // the item now at the removed index
        Assert.Equal(6, s.Count);

        s.Remove(0, 100);                            // clamped: all of them
        Assert.Equal(0, s.Count);
        Assert.True(s.IsEmpty);
        Assert.Equal(-1, s.Current);
    }

    [Fact]
    public void Single_mode_selects_the_item_that_replaces_a_removed_current_one()
    {
        var s = Items(5, SelectionMode.Single, out _);
        s.Select(4);
        s.Remove(4);
        Assert.Equal(3, s.Current);
        Assert.Equal(R((3, 3)), s.Ranges);

        s.Count = 2;                                 // shrinking past it: the new last item
        Assert.Equal(1, s.Current);
        Assert.Equal(R((1, 1)), s.Ranges);

        s.Reset(8);
        Assert.Equal(-1, s.Current);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Switching_mode_collapses_the_selection()
    {
        var s = Items(10, SelectionMode.Extended, out _);
        s.SelectRange(1, 6);
        s.Mode = SelectionMode.Single;
        Assert.Equal(R((6, 6)), s.Ranges);
        s.Mode = SelectionMode.Extended;
        Assert.Equal(R((6, 6)), s.Ranges);
        s.Mode = SelectionMode.None;
        Assert.True(s.IsEmpty);
        Assert.Equal(6, s.Current);
    }

    // ---- CellSelection ----

    static CellSelection Cells(int cols, int rows, CellSelectionMode mode, out List<string> log)
    {
        var s = new CellSelection(mode);
        s.SetSize(cols, rows);
        var events = new List<string>();
        s.Changed += () => events.Add("changed");
        log = events;
        return s;
    }

    static GridRect G(int l, int t, int r, int b) => new(l, t, r, b);

    [Fact]
    public void A_grid_with_cells_always_has_a_current_cell()
    {
        var s = new CellSelection();
        Assert.Equal(new GridCoord(-1, -1), s.Current);
        Assert.Null(s.ActiveRange);
        s.SetSize(5, 4);
        Assert.Equal(new GridCoord(0, 0), s.Current);
        Assert.Equal([G(0, 0, 0, 0)], s.Ranges);
        s.Current = new GridCoord(9, 9);             // clamped
        Assert.Equal(new GridCoord(4, 3), s.Current);
        s.SetSize(0, 4);
        Assert.Equal(new GridCoord(-1, -1), s.Current);
        Assert.Empty(s.Ranges);
    }

    [Fact]
    public void Range_mode_spans_one_rectangle_from_the_anchor()
    {
        var s = Cells(10, 10, CellSelectionMode.Range, out var log);
        s.Click(2, 3);
        s.Click(5, 1, extend: true);                 // Shift+click
        Assert.Equal([G(2, 1, 5, 3)], s.Ranges);
        Assert.Equal(new GridCoord(2, 3), s.Anchor);
        Assert.Equal(new GridCoord(5, 1), s.Current);

        s.MoveTo(6, 1, extend: true);                // Shift+Right
        Assert.Equal([G(2, 1, 6, 3)], s.Ranges);
        s.Click(7, 7, toggle: true);                 // Ctrl means nothing in Range mode
        Assert.Equal([G(7, 7, 7, 7)], s.Ranges);
        s.DragTo(4, 9);
        s.DragTo(40, 9);                             // clamped
        Assert.Equal([G(7, 7, 9, 9)], s.Ranges);
        Assert.True(s.IsSelected(8, 8));
        Assert.False(s.IsSelected(6, 8));
        int events = log.Count;
        s.DragTo(9, 9);
        Assert.Equal(events, log.Count);
        s.MoveTo(0, 0);
        Assert.Equal([G(0, 0, 0, 0)], s.Ranges);
    }

    [Fact]
    public void Single_mode_selects_only_the_current_cell()
    {
        var s = Cells(10, 10, CellSelectionMode.Single, out _);
        s.Click(3, 3);
        s.Click(6, 6, extend: true);
        s.MoveTo(7, 6, extend: true);
        s.SelectAll();
        Assert.Equal([G(7, 6, 7, 6)], s.Ranges);
        s.SelectRange(new GridCoord(0, 0), new GridCoord(4, 4));
        Assert.Equal([G(4, 4, 4, 4)], s.Ranges);
    }

    [Fact]
    public void MultiRange_mode_adds_rectangles_with_ctrl_and_shift_changes_only_the_last()
    {
        var s = Cells(10, 10, CellSelectionMode.MultiRange, out _);
        s.Click(1, 1);
        s.DragTo(2, 2);
        s.Click(5, 5, toggle: true);                 // Ctrl+press starts a second rectangle...
        s.DragTo(6, 7);                              // ...that the drag spans
        Assert.Equal([G(1, 1, 2, 2), G(5, 5, 6, 7)], s.Ranges);
        s.Click(8, 5, extend: true);                 // Shift: only the active (last) one changes
        Assert.Equal([G(1, 1, 2, 2), G(5, 5, 8, 5)], s.Ranges);
        s.SelectRange(new GridCoord(0, 9), new GridCoord(9, 9), add: true);   // a header click adding a row
        Assert.Equal(3, s.Ranges.Count);
        Assert.Equal(G(0, 9, 9, 9), s.ActiveRange);
        s.Mode = CellSelectionMode.Range;            // narrowing collapses to the current cell
        Assert.Equal([G(9, 9, 9, 9)], s.Ranges);
    }

    [Fact]
    public void Select_all_on_a_hundred_thousand_row_grid_is_one_rectangle()
    {
        var s = Cells(26, 100_000, CellSelectionMode.Range, out var log);
        s.Click(3, 50_000);
        s.SelectAll();
        Assert.Equal([G(0, 0, 25, 99_999)], s.Ranges);
        Assert.True(s.IsSelected(25, 99_999));
        Assert.Equal(new GridCoord(3, 50_000), s.Current);   // the current cell stays
        Assert.Equal(2, log.Count);
        s.MoveTo(4, 50_000, extend: true);           // Shift+arrow afterwards spans from the anchor again
        Assert.Equal([G(3, 50_000, 4, 50_000)], s.Ranges);
    }

    [Fact]
    public void Inserted_and_removed_rows_and_columns_keep_the_selection_on_its_cells()
    {
        var s = Cells(10, 10, CellSelectionMode.MultiRange, out _);
        s.Click(1, 2);
        s.DragTo(3, 4);                              // G(1,2,3,4), current (3,4)
        s.Click(6, 8, toggle: true);                 // G(6,8,6,8), current (6,8)

        s.InsertRows(3, 2);                          // inside the first: it grows; the second moves down
        Assert.Equal([G(1, 2, 3, 6), G(6, 10, 6, 10)], s.Ranges);
        Assert.Equal(new GridCoord(6, 10), s.Current);
        Assert.Equal(12, s.RowCount);

        s.InsertColumns(0);
        Assert.Equal([G(2, 2, 4, 6), G(7, 10, 7, 10)], s.Ranges);

        s.RemoveRows(9, 3);                          // takes the second (active) range and the current cell
        Assert.Equal(new GridCoord(7, 8), s.Current);  // the last row now
        Assert.Equal([G(7, 8, 7, 8)], s.Ranges);     // the active range went: collapse to the current cell
        Assert.Equal(9, s.RowCount);

        s.Click(2, 2);
        s.DragTo(4, 6);                              // G(2,2,4,6), current (4,6)
        s.RemoveColumns(3, 1);                       // a range spanning removed columns shrinks
        Assert.Equal([G(2, 2, 3, 6)], s.Ranges);
        Assert.Equal(new GridCoord(3, 6), s.Current);
        s.RemoveColumns(0, 100);                     // all of them
        Assert.Equal(new GridCoord(-1, -1), s.Current);
        Assert.Empty(s.Ranges);
    }

    [Fact]
    public void Removing_every_selected_row_falls_back_to_the_current_cell()
    {
        var s = Cells(5, 10, CellSelectionMode.Range, out _);
        s.Click(0, 2);
        s.Click(4, 3, extend: true);
        s.RemoveRows(2, 2);
        Assert.Equal(new GridCoord(4, 2), s.Current);
        Assert.Equal([G(4, 2, 4, 2)], s.Ranges);

        s.SelectRange(new GridCoord(0, 5), new GridCoord(4, 7));
        s.SetSize(3, 6);                             // shrinking clips the range
        Assert.Equal([G(0, 5, 2, 5)], s.Ranges);
        Assert.Equal(new GridCoord(2, 5), s.Current);
    }

    // ---- Review regressions ----

    [Fact]
    public void Removing_the_active_range_collapses_to_the_current_cell_so_shift_never_rewrites_an_earlier_range()
    {
        var s = Cells(10, 10, CellSelectionMode.MultiRange, out _);
        s.Click(0, 0);
        s.DragTo(2, 2);                              // A = G(0,0,2,2)
        s.Click(5, 5, toggle: true);
        s.DragTo(6, 6);                              // B = G(5,5,6,6), active, current (6,6)
        s.RemoveRows(5, 2);                          // B goes; current maps to row 5
        Assert.Equal(new GridCoord(6, 5), s.Current);
        Assert.Equal([G(6, 5, 6, 5)], s.Ranges);
        Assert.Equal(s.Current, s.Anchor);
        s.MoveTo(6, 6, extend: true);                // Shift+Down spans from the current cell, A is not rewritten
        Assert.Equal([G(6, 5, 6, 6)], s.Ranges);
    }

    [Fact]
    public void Removing_the_active_range_collapses_even_when_the_current_cell_lands_in_an_earlier_range()
    {
        var s = Cells(10, 10, CellSelectionMode.MultiRange, out _);
        s.Click(0, 0);
        s.DragTo(3, 9);                              // A = G(0,0,3,9)
        s.Click(2, 5, toggle: true);                 // B = G(2,5,2,5), active
        s.RemoveRows(5);                             // B goes; current (2,5) is now inside A
        Assert.Equal([G(2, 5, 2, 5)], s.Ranges);
        Assert.Equal(new GridCoord(2, 5), s.Anchor);
    }

    [Fact]
    public void A_surviving_active_range_that_holds_the_current_cell_stays_and_keeps_its_anchor_inside()
    {
        var s = Cells(10, 10, CellSelectionMode.Range, out _);
        s.Click(1, 1);
        s.Click(4, 8, extend: true);                 // G(1,1,4,8), anchor (1,1), current (4,8)
        s.RemoveRows(0, 2);                          // the anchor's row goes: the range shrinks, still holds current
        Assert.Equal([G(1, 0, 4, 6)], s.Ranges);
        Assert.Equal(new GridCoord(4, 6), s.Current);
        Assert.True(s.ActiveRange!.Value.Contains(s.Anchor));

        s.Click(1, 1);
        s.Click(3, 3, extend: true);                 // anchor (1,1), current (3,3)
        s.RemoveRows(3);                             // the current cell's row goes: current maps to row 3, below the range
        Assert.Equal(new GridCoord(3, 3), s.Current);
        Assert.Equal([G(3, 3, 3, 3)], s.Ranges);
    }

    [Fact]
    public void Shrinking_the_grid_past_the_current_cell_collapses_to_it()
    {
        var s = Cells(10, 10, CellSelectionMode.MultiRange, out _);
        s.Click(0, 0);
        s.DragTo(1, 1);                              // A
        s.Click(8, 8, toggle: true);                 // B active, current (8,8)
        s.SetSize(5, 5);                             // B clipped away, current clamped to (4,4)
        Assert.Equal(new GridCoord(4, 4), s.Current);
        Assert.Equal([G(4, 4, 4, 4)], s.Ranges);

        s.Click(0, 0);
        s.DragTo(4, 4);
        s.SetSize(3, 3);                             // clipped, but still holds the clamped current
        Assert.Equal([G(0, 0, 2, 2)], s.Ranges);
        Assert.Equal(new GridCoord(2, 2), s.Current);
    }

    [Fact]
    public void Cell_ctrl_click_on_an_already_selected_cell_adds_no_duplicate_range_and_raises_nothing()
    {
        var s = new CellSelection(CellSelectionMode.MultiRange);
        s.SetSize(5, 5);
        int changes = 0;
        s.Changed += () => changes++;
        s.Click(0, 0, toggle: true);
        Assert.Equal(0, changes);
        Assert.Single(s.Ranges);
    }
}
