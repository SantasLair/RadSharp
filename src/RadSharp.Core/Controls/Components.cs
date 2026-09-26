namespace RadSharp;

/// <summary>
/// A non-visual component (timer, dialog, popup menu, image list): part of the form file and the
/// generated class like any control, but never laid out, painted or hit. The designer shows it in
/// the component tray at its <see cref="Control.Left"/>/<see cref="Control.Top"/>.
/// </summary>
public abstract class Component : Control
{
    protected Component()
    {
        SetDefaultSize(0, 0);
        Visible = false;
    }
}

/// <summary>Raises <see cref="Timer"/> every <see cref="Interval"/> milliseconds while enabled.</summary>
public class Timer : Component
{
    double _last = double.NaN;
    bool _enabled = true;

    /// <summary>Milliseconds between events; 0 stops the timer.</summary>
    [Published(Category = "Behavior")] public int Interval { get; set; } = 1000;

    /// <summary>Whether the timer runs (unlike other controls, this doesn't grey anything out).</summary>
    [Published("enabled", Category = "Behavior")]
    public new bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _last = double.NaN; }
    }

    [Published("onTimer")] public event NotifyEvent? Elapsed;

    /// <summary>Called by the form's clock.</summary>
    internal void Tick(double nowMs)
    {
        if (!_enabled || Interval <= 0) return;
        if (double.IsNaN(_last)) { _last = nowMs; return; }
        if (nowMs - _last < Interval) return;
        _last = nowMs;
        Elapsed?.Invoke(this);
    }
}
