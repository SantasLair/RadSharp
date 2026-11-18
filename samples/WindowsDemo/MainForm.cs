using RadSharp;

namespace WindowsDemo;

/// <summary>
/// The window model: which monitor the form is on and at what scale, the monitors attached, moving or maximising the
/// form on one of them, full screen, always on top, the border style, a dialog centred on its owner, and the
/// placement remembered across runs (see Program.cs).
/// </summary>
public partial class MainForm : Form
{
    IReadOnlyList<MonitorInfo> _monitors = [];

    public MainForm()
    {
        InitializeComponent();
        ScaleChanged += (_, change) => { lblHint.Caption = $"Rescaled from {change.From * 100:0} % to {change.To * 100:0} %."; ShowStatus(); };
        MonitorChanged += _ => { ShowStatus(); ShowMonitors(); };
        WindowStateChanged += _ => { chkFullScreen.Checked = WindowState == WindowState.FullScreen; ShowStatus(); };
        RestoredBoundsChanged += _ => ShowStatus();
        Shown += _ => { ShowMonitors(); ShowStatus(); };
    }

    /// <summary>Where the placement is saved; shown on the form.</summary>
    public string PlacementPath
    {
        get => _placementPath;
        set { _placementPath = value; lblPlacement.Caption = $"The window's place is remembered in {value}"; }
    }
    string _placementPath = "";

    void ShowStatus()
    {
        var d = Monitor;
        lblCurrent.Caption = d == null
            ? $"Scale {Scale * 100:0} % (the host reports no monitors)"
            : $"On {d.Name}{(d.IsPrimary ? " (primary)" : "")}, form scale {Scale * 100:0} %, monitor scale {d.Scale * 100:0} %";
        var r = RestoredBounds;
        lblState.Caption = $"{WindowState}; normal bounds {r.Width} x {r.Height} at {r.X}, {r.Y}";
    }

    void ShowMonitors()
    {
        _monitors = Screen.Monitors;
        var current = Monitor;
        int keep = lstMonitors.ItemIndex;
        lstMonitors.Items = [.. _monitors.Select(d =>
            $"{d.Name}{(d.IsPrimary ? ", primary" : "")}: {d.Bounds.Width} x {d.Bounds.Height} at {d.Bounds.X}, {d.Bounds.Y}, " +
            $"{d.Scale * 100:0} %{(current != null && d.Id == current.Id ? "  (this window)" : "")}")];
        lstMonitors.ItemIndex = keep >= 0 && keep < _monitors.Count ? keep : _monitors.ToList().FindIndex(d => d.Id == current?.Id);
    }

    MonitorInfo? Chosen => lstMonitors.ItemIndex is var i and >= 0 && i < _monitors.Count ? _monitors[i] : null;

    void btnMove_Click(Control sender)
    {
        if (Chosen is not { } d) return;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        MoveToMonitor(d);
        ShowMonitors();
    }

    void btnMaximize_Click(Control sender)
    {
        if (Chosen is not { } d) return;
        MoveToMonitor(d);
        WindowState = WindowState.Maximized;
        ShowMonitors();
    }

    void btnRefresh_Click(Control sender)
    {
        ShowMonitors();
        ShowStatus();
    }

    void chkFullScreen_Change(Control sender)
    {
        var wanted = chkFullScreen.Checked ? WindowState.FullScreen : WindowState.Normal;
        if (WindowState != wanted && (wanted == WindowState.FullScreen || WindowState == WindowState.FullScreen)) WindowState = wanted;
    }

    void chkTopMost_Change(Control sender) => TopMost = chkTopMost.Checked;

    void cboBorder_Change(Control sender) => BorderStyle = cboBorder.ItemIndex switch
    {
        1 => FormBorderStyle.Single,
        2 => FormBorderStyle.Dialog,
        3 => FormBorderStyle.Tool,
        4 => FormBorderStyle.None,
        _ => FormBorderStyle.Sizable,
    };

    void btnDialog_Click(Control sender)
    {
        // No StartPosition or BorderStyle set: a modal form opens centred on its owner with a dialog frame.
        var dialog = new InfoDialog { Owner = this };
        dialog.lblInfo.Caption = $"Opened on {Monitor?.Name ?? "this window's monitor"} at {dialog.Scale * 100:0} %. " +
                                 "It follows its owner's monitor when you move it.";
        dialog.ShowModal();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
