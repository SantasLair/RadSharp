using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>Window properties: state, minimum size, start position, border.</summary>
public class WindowTests
{
    [Fact]
    public void Window_properties_default_to_a_normal_resizable_window()
    {
        var f = new Form();
        Assert.Equal(WindowState.Normal, f.WindowState);
        Assert.Equal(FormStartPosition.Default, f.StartPosition);
        Assert.True(f.Resizable);
        Assert.Equal(0, f.MinWidth);
        Assert.Equal(0, f.MinHeight);
    }

    [Fact]
    public void Window_properties_round_trip_through_the_form_file()
    {
        var f = new Form
        {
            WindowState = WindowState.Maximized,
            StartPosition = FormStartPosition.CenterOwner,
            Resizable = false,
            MinWidth = 320,
            MinHeight = 200,
        };
        var json = FormWriter.Save(f);
        Assert.Contains("windowState", json);
        var (loaded, warnings) = FormReader.Load(json);
        Assert.Empty(warnings);
        Assert.Equal(WindowState.Maximized, loaded.WindowState);
        Assert.Equal(FormStartPosition.CenterOwner, loaded.StartPosition);
        Assert.False(loaded.Resizable);
        Assert.Equal((320, 200), (loaded.MinWidth, loaded.MinHeight));
    }

    [Fact]
    public void RestoredBounds_defaults_to_the_form_bounds_and_follows_the_host()
    {
        var f = new Form();
        f.SetBounds(30, 40, 500, 300);
        Assert.Equal(new Rect(30, 40, 500, 300), f.RestoredBounds);

        int raised = 0;
        f.RestoredBoundsChanged += _ => raised++;
        f.NotifyRestoredBounds(new Rect(100, 120, 640, 480));
        Assert.Equal(new Rect(100, 120, 640, 480), f.RestoredBounds);
        f.NotifyRestoredBounds(new Rect(100, 120, 640, 480));   // unchanged: no event
        Assert.Equal(1, raised);

        f.WindowState = WindowState.Maximized;
        f.Resize(1920, 1080);                                    // maximised size is not the restored size
        Assert.Equal(new Rect(100, 120, 640, 480), f.RestoredBounds);
    }

    [Fact]
    public void WindowState_raises_an_event_only_when_it_changes_and_the_host_can_write_it_back()
    {
        var f = new Form();
        var seen = new List<WindowState>();
        f.WindowStateChanged += x => seen.Add(x.WindowState);
        f.WindowState = WindowState.Maximized;
        f.WindowState = WindowState.Maximized;
        f.NotifyWindowStateChanged(WindowState.Normal);   // the user restored the window
        Assert.Equal([WindowState.Maximized, WindowState.Normal], seen);
    }
}
