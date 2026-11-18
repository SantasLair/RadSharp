using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>
/// M13 item 1: the rest of the window model (full screen, border style, top-most, icon, modal defaults), the
/// <see cref="Screen"/> API, opening on a chosen display and window placement save/restore, through a fake host.
/// </summary>
[Collection("PlatformHost")]
public class WindowModelTests
{
    static readonly Display Left = new(1, "Left 4K", new Rect(-3840, 0, 3840, 2160), new Rect(-3840, 0, 3840, 2100), 2, false);
    static readonly Display Main = new(2, "Main", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), 1, true);
    static readonly Display Laptop = new(3, "Laptop", new Rect(1920, 200, 1600, 900), new Rect(1920, 200, 1600, 860), 1.5, false);

    /// <summary>A host with displays: windows are rects on them; moving one reports the new rect back like the SDL host.</summary>
    sealed class DisplayHost : IPlatformServices
    {
        public List<Display> Displays = [Main, Left, Laptop];
        public readonly List<Form> Open = [];
        public readonly List<(Form Form, Rect Bounds)> Moves = [];
        public Action<Form>? OnModal;

        public void ShowForm(Form form) { Open.Add(form); form.PerformLayout(); form.NotifyShown(); }
        public void RunModal(Form form)
        {
            ShowForm(form);
            OnModal?.Invoke(form);
            if (Open.Contains(form)) form.Close();
        }
        public void CloseForm(Form form) => Open.Remove(form);
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }

        public IReadOnlyList<Display> GetDisplays() => Displays;
        public Display? GetDisplayOf(Form form) => Open.Contains(form) ? Screen.FromRectIn(Displays, form.RestoredBounds) : null;
        public bool MoveForm(Form form, Rect bounds)
        {
            if (!Open.Contains(form)) return false;
            Moves.Add((form, bounds));
            form.NotifyRestoredBounds(bounds);
            return true;
        }
    }

    static DisplayHost Install()
    {
        var host = new DisplayHost();
        PlatformHost.Current = host;
        return host;
    }

    // ---- Window model ----

    [Fact]
    public void New_window_properties_default_to_a_normal_frame_and_are_not_written_to_the_form_file()
    {
        var f = new Form();
        Assert.Equal(FormBorderStyle.Sizable, f.BorderStyle);
        Assert.False(f.TopMost);
        Assert.Equal("", f.IconSource);
        Assert.Null(f.Icon);
        Assert.True(f.IsUserResizable);
        Assert.True(f.ScaleWithDisplay);
        var json = FormWriter.Save(f);
        Assert.DoesNotContain("borderStyle", json);
        Assert.DoesNotContain("topMost", json);
        Assert.DoesNotContain("\"icon\"", json);
    }

    [Fact]
    public void Full_screen_border_style_top_most_and_icon_round_trip_through_the_form_file()
    {
        var icon = Picture.ToDataUri(Png(2, 2));
        var f = new Form { WindowState = WindowState.FullScreen, BorderStyle = FormBorderStyle.Tool, TopMost = true, IconSource = icon };
        var json = FormWriter.Save(f);
        Assert.Contains("\"windowState\": \"fullScreen\"", json);
        Assert.Contains("\"borderStyle\": \"tool\"", json);
        Assert.Contains("\"topMost\": true", json);
        Assert.Contains("\"icon\": \"data:image/png;base64,", json);
        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        Assert.Equal(WindowState.FullScreen, loaded.WindowState);
        Assert.Equal(FormBorderStyle.Tool, loaded.BorderStyle);
        Assert.True(loaded.TopMost);
        Assert.Equal((2, 2), (loaded.Icon!.Width, loaded.Icon.Height));
        Assert.Equal(json, FormWriter.Save(loaded));
    }

    [Theory]
    [InlineData(FormBorderStyle.Sizable, true, true)]
    [InlineData(FormBorderStyle.Sizable, false, false)]
    [InlineData(FormBorderStyle.Single, true, false)]
    [InlineData(FormBorderStyle.Dialog, true, false)]
    [InlineData(FormBorderStyle.Tool, true, true)]
    [InlineData(FormBorderStyle.Tool, false, false)]
    [InlineData(FormBorderStyle.None, true, false)]
    public void Only_sizable_and_tool_frames_can_be_resized_and_only_while_Resizable(FormBorderStyle style, bool resizable, bool expected)
    {
        var f = new Form { BorderStyle = style, Resizable = resizable };
        Assert.Equal(expected, f.IsUserResizable);
        Assert.Equal(resizable, f.Resizable);   // Resizable keeps its own value
    }

    [Fact]
    public void A_modal_form_defaults_to_a_dialog_frame_centred_on_its_owner_but_keeps_explicit_choices()
    {
        var host = Install();
        var seen = new List<(FormBorderStyle, FormStartPosition, bool)>();
        host.OnModal = f => seen.Add((f.EffectiveBorderStyle, f.EffectiveStartPosition, f.IsUserResizable));

        var owner = new Form();
        var dialog = new Form { Owner = owner };
        Assert.Equal((FormBorderStyle.Sizable, FormStartPosition.Default), (dialog.EffectiveBorderStyle, dialog.EffectiveStartPosition));
        dialog.ShowModal();
        var custom = new Form { Owner = owner, BorderStyle = FormBorderStyle.Single, StartPosition = FormStartPosition.CenterScreen };
        custom.ShowModal();
        new Form().Show();   // not modal: no defaults change

        Assert.Equal((FormBorderStyle.Dialog, FormStartPosition.CenterOwner, true), seen[0]);   // still resizable: Resizable decides
        Assert.Equal((FormBorderStyle.Single, FormStartPosition.CenterScreen, false), seen[1]);
        Assert.Equal((FormBorderStyle.Sizable, FormStartPosition.Default), (dialog.EffectiveBorderStyle, dialog.EffectiveStartPosition));
        Assert.Equal(FormBorderStyle.Sizable, host.Open.Single().EffectiveBorderStyle);
    }

    [Fact]
    public void Message_dialogs_open_as_dialogs_centred_on_their_owner()
    {
        var host = Install();
        (FormBorderStyle, FormStartPosition)? seen = null;
        host.OnModal = f => { seen = (f.EffectiveBorderStyle, f.EffectiveStartPosition); f.Simulate.Click(f.FindControl("btnOk")!); };
        Assert.Equal(ModalResult.Ok, Dialogs.MessageDlg("Saved.", owner: new Form()));
        Assert.Equal((FormBorderStyle.Dialog, FormStartPosition.CenterOwner), seen);
    }

    [Fact]
    public void Icon_set_from_code_wins_over_the_source_and_a_new_source_reloads()
    {
        var f = new Form { IconSource = Picture.ToDataUri(Png(4, 4)) };
        Assert.Equal(4, f.Icon!.Width);
        var pic = Picture.FromBytes(Png(8, 8))!;
        f.Icon = pic;
        Assert.Same(pic, f.Icon);
        f.IconSource = "no-such-icon.png";
        Assert.Null(f.Icon);
        var rgba = pic.ToRgba();
        Assert.Equal(8 * 8 * 4, rgba.Length);
        Assert.Equal([10, 200, 30, 255], rgba[..4]);
    }

    static byte[] Png(int w, int h)
    {
        using var bitmap = new SkiaSharp.SKBitmap(w, h);
        bitmap.Erase(new SkiaSharp.SKColor(10, 200, 30));
        return FormRenderer.EncodePng(bitmap);
    }

    // ---- Screen ----

    [Fact]
    public void Screen_lists_the_hosts_displays_and_finds_them_by_point_rect_and_id()
    {
        Install();
        Assert.Equal([Main, Left, Laptop], Screen.Displays);
        Assert.Same(Main, Screen.Primary);
        Assert.Same(Left, Screen.FromPoint(-10, 500));
        Assert.Null(Screen.FromPoint(5000, 5000));
        Assert.Same(Laptop, Screen.FromRect(new Rect(1800, 300, 400, 300)));   // most of it is on the laptop
        Assert.Same(Laptop, Screen.FromRect(new Rect(4000, 400, 100, 100)));   // off every display: the nearest
        Assert.Same(Left, Screen.FromId(1));
        Assert.Equal("Laptop (1600 x 900 at 1920, 200, 150 %)", Laptop.ToString());

        PlatformHost.Current = null;
        Assert.Empty(Screen.Displays);
        Assert.Null(Screen.Primary);
    }

    [Fact]
    public void A_host_written_before_displays_existed_still_compiles_and_reports_none()
    {
        IPlatformServices old = new OldHost();
        Assert.Empty(old.GetDisplays());
        Assert.Null(old.GetDisplayOf(new Form()));
        Assert.False(old.MoveForm(new Form(), new Rect(0, 0, 10, 10)));
        PlatformHost.Current = old;
        var f = new Form();
        Assert.Null(f.Display);
        Assert.False(f.MoveToDisplay(Main));
        Assert.Same(Main, f.StartDisplay);   // it will open there instead
    }

    sealed class OldHost : IPlatformServices
    {
        public void ShowForm(Form form) { }
        public void RunModal(Form form) { }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) { }
        public void PostToUiThread(Action action) { }
        public void ShowModal(Form form, Action onClosed) { }
    }

    [Fact]
    public void A_form_knows_its_display_and_moves_to_another_centred_in_its_work_area_at_its_scale()
    {
        var host = Install();
        var f = new Form();
        f.SetBounds(0, 0, 800, 600);
        f.ApplyScale(1);
        f.Show();
        f.NotifyRestoredBounds(new Rect(100, 100, 800, 600));
        Assert.Same(Main, f.Display);

        Assert.True(f.MoveToDisplay(Laptop));                         // 150 %: the window grows to keep its size
        Assert.Equal(new Rect(2120, 200, 1200, 860), host.Moves[^1].Bounds);   // 900 high is shrunk to the work area
        Assert.Same(Laptop, f.Display);

        f.WindowState = WindowState.Maximized;                        // maximised on the laptop; move it on as it is
        Assert.True(f.MoveToDisplay(Main));
        Assert.Equal(WindowState.Maximized, f.WindowState);
        Assert.Same(Main, f.Display);
    }

    [Fact]
    public void A_form_that_is_not_open_opens_on_the_display_it_was_moved_to()
    {
        Install();
        var f = new Form();
        f.SetBounds(0, 0, 400, 300);
        Assert.False(f.MoveToDisplay(Left));
        Assert.Same(Left, f.StartDisplay);
        Assert.Same(Main, f.Display);   // not open: the display with most of RestoredBounds
    }

    // ---- Placement ----

    [Fact]
    public void Placement_round_trips_through_its_json_string()
    {
        var p = new WindowPlacement
        {
            Bounds = new Rect(-3000, 120, 1600, 1000), State = WindowState.Maximized, Scale = 2,
            DisplayName = "Left \"4K\" é", DisplayBounds = Left.Bounds,
        };
        var json = p.ToJson();
        Assert.Equal("{\"x\":-3000,\"y\":120,\"width\":1600,\"height\":1000,\"state\":\"maximized\",\"scale\":2,\"display\":\"Left \\\"4K\\\" é\",\"displayBounds\":[-3840,0,3840,2160]}", json);
        Assert.True(WindowPlacement.TryParse(json, out var back));
        Assert.Equal((p.Bounds, p.State, p.Scale, p.DisplayName, p.DisplayBounds), (back!.Bounds, back.State, back.Scale, back.DisplayName, back.DisplayBounds));

        Assert.False(WindowPlacement.TryParse("", out _));
        Assert.False(WindowPlacement.TryParse("not json", out _));
        Assert.False(WindowPlacement.TryParse("""{"x":1,"y":2,"width":0,"height":5}""", out _));
        Assert.True(WindowPlacement.TryParse("""{"x":1,"y":2,"width":3,"height":4}""", out var minimal));
        Assert.Equal((WindowState.Normal, 1.0, ""), (minimal!.State, minimal.Scale, minimal.DisplayName));
    }

    [Fact]
    public void Capture_takes_the_restored_bounds_state_scale_and_display()
    {
        Install();
        var f = new Form();
        f.ApplyScale(1.5);
        f.NotifyRestoredBounds(new Rect(2000, 300, 900, 600));
        f.WindowState = WindowState.Minimized;
        var p = WindowPlacement.Capture(f);
        Assert.Equal(new Rect(2000, 300, 900, 600), p.Bounds);
        Assert.Equal(WindowState.Normal, p.State);   // minimised comes back as normal
        Assert.Equal(1.5, p.Scale);
        Assert.Equal(("Laptop", Laptop.Bounds), (p.DisplayName, p.DisplayBounds));
    }

    [Fact]
    public void Resolve_keeps_a_visible_rect_and_falls_back_when_the_display_is_gone_moved_or_the_rect_is_off_screen()
    {
        var onLaptop = new WindowPlacement { Bounds = new Rect(2000, 300, 900, 600), DisplayName = "Laptop", DisplayBounds = Laptop.Bounds };
        Assert.Equal((new Rect(2000, 300, 900, 600), Laptop), onLaptop.Resolve([Main, Laptop]));

        // The laptop is gone: centred on the primary display.
        Assert.Equal((new Rect(510, 220, 900, 600), Main), onLaptop.Resolve([Main, Left]));

        // The laptop moved to the left of the main display: the rect moves with it.
        var moved = Laptop with { Bounds = new Rect(-1600, 0, 1600, 900), WorkArea = new Rect(-1600, 0, 1600, 860) };
        Assert.Equal((new Rect(-1520, 100, 900, 600), moved), onLaptop.Resolve([Main, moved]));

        // Mostly off the display's work area, or the title bar above it: centred on it.
        var offEdge = new WindowPlacement { Bounds = new Rect(1890, 500, 800, 400), DisplayName = "Main", DisplayBounds = Main.Bounds };
        Assert.Equal(new Rect(560, 320, 800, 400), offEdge.Resolve([Main]).Bounds);
        var above = new WindowPlacement { Bounds = new Rect(100, -20, 800, 400), DisplayName = "Main", DisplayBounds = Main.Bounds };
        Assert.Equal(new Rect(560, 320, 800, 400), above.Resolve([Main]).Bounds);

        // Larger than the work area: shrunk to fit.
        var huge = new WindowPlacement { Bounds = new Rect(0, 0, 5000, 3000), DisplayName = "Main", DisplayBounds = Main.Bounds };
        Assert.Equal(new Rect(0, 0, 1920, 1040), huge.Resolve([Main]).Bounds);

        // No name (another host wrote it): the display with most of the rect.
        var unnamed = new WindowPlacement { Bounds = new Rect(-2000, 100, 800, 600) };
        Assert.Same(Left, unnamed.Resolve([Main, Left]).Display);

        // No displays at all: unchanged.
        Assert.Equal((new Rect(2000, 300, 900, 600), (Display?)null), onLaptop.Resolve([]));
    }

    [Fact]
    public void ApplyTo_before_showing_opens_the_form_there_at_its_own_scale_and_state()
    {
        Install();
        var saved = new WindowPlacement
        {
            Bounds = new Rect(2000, 300, 900, 600), State = WindowState.Maximized, Scale = 1.5,
            DisplayName = "Laptop", DisplayBounds = Laptop.Bounds,
        };
        var f = new Form();
        f.ApplyScale(1);                              // loaded at 100 %; the host rescales it on the 150 % laptop
        saved.ApplyTo(f);
        Assert.Equal(FormStartPosition.Manual, f.StartPosition);
        Assert.Same(Laptop, f.StartDisplay);
        Assert.Equal(new Rect(2000, 300, 600, 400), f.Bounds);   // 900 x 600 at 150 % is 600 x 400 at 100 %
        Assert.Equal(new Rect(2000, 300, 600, 400), f.RestoredBounds);
        Assert.Equal(WindowState.Maximized, f.WindowState);
    }

    [Fact]
    public void ApplyTo_an_open_form_moves_its_window()
    {
        var host = Install();
        var f = new Form();
        f.ApplyScale(1);
        f.Show();
        new WindowPlacement { Bounds = new Rect(-3000, 100, 800, 500), DisplayName = "Left 4K", DisplayBounds = Left.Bounds, State = WindowState.FullScreen }
            .ApplyTo(f);
        Assert.Equal(new Rect(-3000, 100, 800, 500), host.Moves.Single().Bounds);
        Assert.Equal(WindowState.FullScreen, f.WindowState);
        Assert.Same(Left, f.Display);
    }

    [Fact]
    public void Remember_saves_the_placement_when_the_form_closes_and_applies_it_next_time()
    {
        var host = Install();
        var path = Path.Combine(Directory.CreateTempSubdirectory("radsharp-placement-").FullName, "sub", "window.json");

        var first = new Form();
        first.ApplyScale(1);
        WindowPlacement.Remember(first, path);   // no file yet: nothing changes
        Assert.Equal(FormStartPosition.Default, first.StartPosition);
        first.Show();
        first.NotifyRestoredBounds(new Rect(300, 200, 700, 500));
        first.WindowState = WindowState.Maximized;
        first.Close();
        Assert.True(File.Exists(path));

        var second = new Form();
        second.ApplyScale(1);
        WindowPlacement.Remember(second, path);
        Assert.Equal(FormStartPosition.Manual, second.StartPosition);
        Assert.Equal(new Rect(300, 200, 700, 500), second.Bounds);
        Assert.Equal(WindowState.Maximized, second.WindowState);
        Assert.Same(Main, second.StartDisplay);

        File.WriteAllText(path, "garbage");
        var third = new Form();
        WindowPlacement.Remember(third, path);   // unreadable: ignored
        Assert.Equal(FormStartPosition.Default, third.StartPosition);
        Assert.Empty(host.Moves);
    }
}
