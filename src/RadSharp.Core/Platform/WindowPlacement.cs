using System.Text;
using System.Text.Json;

namespace RadSharp;

/// <summary>
/// Where a form's window was: its normal (restored) bounds, its state and the monitor it was on, so an application
/// can reopen it in the same place (Delphi keeps this per form in an INI file; RadSharp leaves the storage to you, or
/// does it with <see cref="Remember"/>). <see cref="Capture"/> takes it from a form, <see cref="ApplyTo"/> gives it back
/// (falling back sensibly when that monitor is gone or the rect would be off screen), and <see cref="ToJson"/> /
/// <see cref="TryParse"/> store it as a small JSON string, without reflection.
/// </summary>
/// <example>
/// <code>
/// // Reopen where it was last time, and remember it when it closes:
/// WindowPlacement.Remember(mainForm, WindowPlacement.DefaultPath("MyApp"));
/// </code>
/// </example>
public sealed class WindowPlacement
{
    /// <summary>The window's normal bounds, screen coordinates in the host's window units (<see cref="Form.RestoredBounds"/>).</summary>
    public Rect Bounds { get; init; }

    /// <summary>Normal, maximised or full screen (a minimised window is captured as normal).</summary>
    public WindowState State { get; init; }

    /// <summary>The form's scale when captured; <see cref="ApplyTo"/> converts the size if the form now has another.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>The name of the monitor the window was on, or empty if unknown.</summary>
    public string MonitorName { get; init; } = "";

    /// <summary>That monitor's bounds when captured (to tell two monitors of the same model apart and to follow a monitor that moved).</summary>
    public Rect MonitorBounds { get; init; }

    /// <summary>
    /// How much of the window must be on a monitor's work area for its saved rect to be used as it is: at least this
    /// many pixels each way. Otherwise it is centred on the monitor.
    /// </summary>
    public const int MinVisible = 48;

    // The scales TryParse accepts; anything else is read as 100 %.
    const double MinScale = 0.25, MaxScale = 16;

    /// <summary>Takes the placement of <paramref name="form"/> (open or closed).</summary>
    public static WindowPlacement Capture(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var monitor = form.Monitor;
        return new WindowPlacement
        {
            Bounds = form.RestoredBounds,
            State = form.WindowState == WindowState.Minimized ? WindowState.Normal : form.WindowState,
            Scale = form.Scale,
            MonitorName = monitor?.Name ?? "",
            MonitorBounds = monitor?.Bounds ?? default,
        };
    }

    /// <summary>
    /// Puts <paramref name="form"/> where this placement says, on the monitors attached now. Before the form is shown,
    /// it sets <see cref="Form.StartPosition"/> to Manual with the bounds, <see cref="Form.StartMonitor"/> and
    /// <see cref="Form.WindowState"/>; an open window is moved (<see cref="Form.MoveToMonitor"/>'s host call). The saved
    /// monitor is looked for by name and bounds, then by name, then as the monitor with most of the rect, then the
    /// primary monitor. If that monitor moved, the rect moves with it; a rect not on it far enough
    /// (<see cref="MinVisible"/>) is centred on it, and one larger than its work area is shrunk to fit.
    /// </summary>
    public void ApplyTo(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var host = PlatformHost.Current;
        var monitors = host?.GetMonitors() ?? [];
        var (rect, monitor) = Locate(monitors);

        // The size was in the form's pixels at Scale. Keep it in design units at the scale the window will have on that
        // monitor (the monitor's, for a form that follows monitor scale), and fit it there at that size.
        double target = monitor != null && form.FollowsMonitorScale ? monitor.Scale : form.Scale;
        if (Scale > 0 && target != Scale)
            rect = new Rect(rect.X, rect.Y, LayoutMath.Round(rect.Width / Scale * target), LayoutMath.Round(rect.Height / Scale * target));
        if (monitor != null) rect = Fit(rect, monitor);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // An open window gets the rect as it should end up (the host's MoveForm, like Form.MoveToMonitor).
        if (host?.MoveForm(form, rect) == true)
        {
            form.NotifyRestoredBounds(rect);
            form.WindowState = State;
            return;
        }
        // Not open: the window is created at the form's scale, and the host rescales it on the monitor.
        if (target != form.Scale)
            rect = new Rect(rect.X, rect.Y, LayoutMath.Round(rect.Width / target * form.Scale), LayoutMath.Round(rect.Height / target * form.Scale));
        form.StartPosition = FormStartPosition.Manual;
        form.StartMonitor = monitor;
        form.SetBounds(rect.X, rect.Y, rect.Width, rect.Height);
        form.NotifyRestoredBounds(rect);
        form.WindowState = State;
    }

    /// <summary>The rect to use and the monitor it lands on, for the given monitors (see <see cref="ApplyTo"/>).</summary>
    public (Rect Bounds, MonitorInfo? Monitor) Resolve(IReadOnlyList<MonitorInfo> monitors)
    {
        var (rect, monitor) = Locate(monitors);
        return (monitor != null ? Fit(rect, monitor) : rect, monitor);
    }

    /// <summary>The monitor the placement belongs on, and the saved rect moved with that monitor if it moved (not fitted yet).</summary>
    (Rect Bounds, MonitorInfo? Monitor) Locate(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var rect = Bounds;
        if (monitors.Count == 0) return (rect, null);

        var monitor = (MonitorName.Length > 0
                ? monitors.FirstOrDefault(d => d.Name == MonitorName && d.Bounds == MonitorBounds)
                  ?? monitors.FirstOrDefault(d => d.Name == MonitorName)
                : null)
            ?? (Screen.OverlapAreaAny(monitors, rect) ? Screen.Nearest(monitors, rect) : null)
            ?? Screen.Primary_(monitors)!;

        // The monitor was found by name but has moved: move the rect with it.
        if (monitor.Name == MonitorName && MonitorBounds is { Width: > 0, Height: > 0 } && monitor.Bounds != MonitorBounds)
            rect = new Rect(rect.X - MonitorBounds.X + monitor.Bounds.X, rect.Y - MonitorBounds.Y + monitor.Bounds.Y, rect.Width, rect.Height);
        return (rect, monitor);
    }

    /// <summary>Shrinks <paramref name="rect"/> to <paramref name="monitor"/>'s work area, and centres it there if not enough of it would show.</summary>
    static Rect Fit(Rect rect, MonitorInfo monitor)
    {
        var area = monitor.WorkArea is { Width: > 0, Height: > 0 } ? monitor.WorkArea : monitor.Bounds;
        int w = Math.Min(rect.Width, area.Width), h = Math.Min(rect.Height, area.Height);
        rect = new Rect(rect.X, rect.Y, w, h);
        int visibleW = Math.Min(rect.X + w, area.X + area.Width) - Math.Max(rect.X, area.X);
        int visibleH = Math.Min(rect.Y + h, area.Y + area.Height) - Math.Max(rect.Y, area.Y);
        bool titleOnScreen = rect.Y >= area.Y;   // the title bar must not be above the work area
        if (visibleW < Math.Min(MinVisible, w) || visibleH < Math.Min(MinVisible, h) || !titleOnScreen)
            rect = Screen.CenterIn(area, w, h);
        return rect;
    }

    /// <summary>The placement as a JSON object on one line, e.g. <c>{"x":100,"y":80,"width":800,"height":600,"state":"maximized",...}</c>.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        // Relaxed escaping keeps monitor names readable (accents and quotes as text, not escape codes); this is a file, not HTML.
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("x", Bounds.X);
            w.WriteNumber("y", Bounds.Y);
            w.WriteNumber("width", Bounds.Width);
            w.WriteNumber("height", Bounds.Height);
            w.WriteString("state", State switch
            {
                WindowState.Maximized => "maximized",
                WindowState.FullScreen => "fullScreen",
                _ => "normal",
            });
            w.WriteNumber("scale", Scale);
            if (MonitorName.Length > 0) w.WriteString("monitor", MonitorName);
            if (MonitorBounds != default)
            {
                w.WriteStartArray("monitorBounds");
                w.WriteNumberValue(MonitorBounds.X);
                w.WriteNumberValue(MonitorBounds.Y);
                w.WriteNumberValue(MonitorBounds.Width);
                w.WriteNumberValue(MonitorBounds.Height);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public override string ToString() => ToJson();

    /// <summary>Reads a placement written by <see cref="ToJson"/>. Returns false (and null) for anything else.</summary>
    public static bool TryParse(string? json, out WindowPlacement? placement)
    {
        placement = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!Int(root, "x", out int x) || !Int(root, "y", out int y) || !Int(root, "width", out int width) || !Int(root, "height", out int height)
                || width <= 0 || height <= 0)
                return false;
            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() switch
            {
                "maximized" => WindowState.Maximized,
                "fullScreen" => WindowState.FullScreen,
                _ => WindowState.Normal,
            } : WindowState.Normal;
            // A scale no monitor has (a garbled file) would blow the size up or down when it is converted: use 100 %.
            double scale = root.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetDouble(out var v)
                && v >= MinScale && v <= MaxScale ? v : 1;
            string name = Str(root, "monitor") ?? Str(root, "display") ?? "";   // "display": the key before the rename
            Rect monitorBounds = default;
            if ((root.TryGetProperty("monitorBounds", out var db) || root.TryGetProperty("displayBounds", out db)) && db.ValueKind == JsonValueKind.Array && db.GetArrayLength() == 4)
            {
                var e = db.EnumerateArray().ToArray();
                if (e.All(i => i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out _)))
                    monitorBounds = new Rect(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
            }
            placement = new WindowPlacement
            {
                Bounds = new Rect(x, y, width, height), State = state, Scale = scale, MonitorName = name, MonitorBounds = monitorBounds,
            };
            return true;
        }
        // A garbled file can also hold text JsonDocument accepts but can't hand back as a string (a lone surrogate escape).
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { placement = null; return false; }

        static string? Str(JsonElement o, string key) =>
            o.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        static bool Int(JsonElement o, string key, out int value)
        {
            value = 0;
            return o.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
        }
    }

    /// <summary>
    /// The usual place for a remembered placement: <c>window.json</c> in a folder named <paramref name="appName"/> in the
    /// user's application-data folder (<c>%APPDATA%</c> on Windows, <c>~/.config</c> on Linux, <c>~/Library/Application Support</c> on macOS).
    /// </summary>
    public static string DefaultPath(string appName, string fileName = "window.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName, fileName);
    }

    /// <summary>
    /// Remembers <paramref name="form"/>'s placement across runs in the file <paramref name="path"/>: applies the saved
    /// placement now (call it before showing the form; a missing or unreadable file is ignored) and saves it again when
    /// the form closes. Errors writing the file are ignored: a placement isn't worth failing over.
    /// </summary>
    public static void Remember(Form form, string path)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (File.Exists(path) && TryParse(File.ReadAllText(path), out var saved)) saved!.ApplyTo(form);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        form.Closed += _ =>
        {
            try
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Capture(form).ToJson() + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        };
    }
}
