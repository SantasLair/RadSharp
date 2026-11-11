using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client;

/// <summary>
/// Shows host popups (menus, drop-downs, tooltips) as WPF <see cref="Popup"/>s placed relative to the owner view, and host
/// dialogs as WPF <see cref="Window"/>s, each containing a <see cref="RemoteWindowView"/>. Subclass to change parenting
/// (for example to Visual Studio's dialog owner window) via <see cref="OnDialogCreated"/>.
/// </summary>
public class RemoteWindowPresenter
{
    private readonly Dictionary<int, Shown> _shown = new Dictionary<int, Shown>();

    private sealed class Shown
    {
        public Shown(RemoteWindowInfo info, RemoteWindowView view, double scale)
        {
            Info = info;
            View = view;
            Scale = scale;
        }

        public RemoteWindowInfo Info { get; }
        public RemoteWindowView View { get; }
        public double Scale { get; }
        public Popup? Popup { get; set; }
        public Window? Dialog { get; set; }
        public Window? OwnerWindow { get; set; }
        public bool ClosingByHost { get; set; }
    }

    /// <summary>The view showing a popup or dialog, if this presenter shows it.</summary>
    public RemoteWindowView? GetView(int windowId) => _shown.TryGetValue(windowId, out var s) ? s.View : null;

    /// <summary>The WPF popup for a host popup window.</summary>
    public Popup? GetPopup(int windowId) => _shown.TryGetValue(windowId, out var s) ? s.Popup : null;

    /// <summary>The WPF window for a host dialog.</summary>
    public Window? GetDialog(int windowId) => _shown.TryGetValue(windowId, out var s) ? s.Dialog : null;

    /// <summary>Shows a newly announced popup or dialog. <paramref name="owner"/> is the view of its owner window, if any.</summary>
    public virtual void Show(DesignSession session, RemoteWindowInfo info, RemoteWindowView? owner)
    {
        if (_shown.ContainsKey(info.Id)) return;
        double scale = owner?.Scale ?? 1.0;
        if (owner == null && Application.Current?.MainWindow is Window main) scale = VisualTreeHelper.GetDpi(main).DpiScaleX;
        var view = new RemoteWindowView
        {
            Width = info.Width / scale,
            Height = info.Height / scale,
            FocusOnClick = info.Role == WindowRole.Dialog,
            ShouldForwardKey = owner?.ShouldForwardKey,
            DpiScaleOverride = owner?.DpiScaleOverride,
        };
        var shown = new Shown(info, view, scale);
        _shown.Add(info.Id, shown);
        view.Attach(session, info.Id);

        if (info.Role == WindowRole.Popup) ShowPopup(session, shown, owner);
        else ShowDialog(session, shown, owner);
    }

    private void ShowPopup(DesignSession session, Shown s, RemoteWindowView? owner)
    {
        if (owner == null)
        {
            // Nothing to anchor to: place it at the mouse.
            s.Popup = new Popup { Child = s.View, Placement = PlacementMode.MousePoint, StaysOpen = true };
        }
        else
        {
            s.Popup = new Popup
            {
                Child = s.View,
                PlacementTarget = owner,
                Placement = PlacementMode.Relative,
                HorizontalOffset = s.Info.X / s.Scale,
                VerticalOffset = s.Info.Y / s.Scale,
                StaysOpen = true,
                AllowsTransparency = false,
                Focusable = false,
            };
            // The host closes popups when it sees a click outside them, but it only sees clicks on its own windows. Tell
            // it about clicks elsewhere in the owner's window and about the window losing activation.
            s.OwnerWindow = Window.GetWindow(owner);
            if (s.OwnerWindow != null)
            {
                s.OwnerWindow.PreviewMouseDown += OnOwnerWindowMouseDown;
                s.OwnerWindow.Deactivated += OnOwnerWindowDeactivated;
                s.OwnerWindow.LocationChanged += OnOwnerWindowDeactivated;
            }
        }
        s.Popup.IsOpen = true;
        OnPopupCreated(s.Popup, s.Info, owner);

        void OnOwnerWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!s.Info.IsOpen) return;
            // A click on the owner view reaches the host, which handles it (e.g. a second click on a menu bar item).
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualParent(d))
                if (ReferenceEquals(d, owner)) return;
            session.RequestCloseWindow(s.Info.Id);
        }

        void OnOwnerWindowDeactivated(object? sender, EventArgs e)
        {
            if (s.Info.IsOpen) session.RequestCloseWindow(s.Info.Id);
        }

        s.Popup.Closed += (o, e) =>
        {
            if (s.OwnerWindow == null) return;
            s.OwnerWindow.PreviewMouseDown -= OnOwnerWindowMouseDown;
            s.OwnerWindow.Deactivated -= OnOwnerWindowDeactivated;
            s.OwnerWindow.LocationChanged -= OnOwnerWindowDeactivated;
        };
    }

    private static DependencyObject? VisualParent(DependencyObject d) =>
        d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private void ShowDialog(DesignSession session, Shown s, RemoteWindowView? owner)
    {
        var win = new Window
        {
            Title = s.Info.Title,
            Content = s.View,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            UseLayoutRounding = true,
        };
        s.Dialog = win;
        if (owner != null)
        {
            var ownerWindow = Window.GetWindow(owner);
            if (ownerWindow != null) win.Owner = ownerWindow;
            if (PresentationSource.FromVisual(owner) != null)
            {
                var origin = owner.PointToScreen(new Point(0, 0)); // device pixels
                win.Left = (origin.X + s.Info.X) / s.Scale;
                win.Top = (origin.Y + s.Info.Y) / s.Scale;
            }
        }
        else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        win.Closing += (o, e) => OnDialogClosing(session, s, e);
        win.Closed += (o, e) =>
        {
            s.View.Detach();
            _shown.Remove(s.Info.Id);
        };
        OnDialogCreated(win, s.Info, owner);

        if (s.Info.Modal)
        {
            // Show it from a fresh dispatcher operation so ShowDialog's nested loop does not run inside message routing.
            win.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!s.Info.IsOpen || s.ClosingByHost) return;
                win.ShowDialog();
            }));
        }
        else win.Show();
    }

    private static void OnDialogClosing(DesignSession session, Shown s, CancelEventArgs e)
    {
        if (s.ClosingByHost || !s.Info.IsOpen || !session.IsConnected) return;
        // The user closed it (title bar X, Alt+F4): let the host decide; it answers with WindowClosed.
        e.Cancel = true;
        session.RequestCloseWindow(s.Info.Id);
    }

    /// <summary>Called after a dialog window is created (already owned by the owner view's window, when there is one) and
    /// before it is shown. Override to re-parent it, e.g. with <c>WindowInteropHelper.Owner</c> in Visual Studio.</summary>
    protected virtual void OnDialogCreated(Window dialog, RemoteWindowInfo info, RemoteWindowView? owner) { }

    /// <summary>Called after a popup is opened.</summary>
    protected virtual void OnPopupCreated(Popup popup, RemoteWindowInfo info, RemoteWindowView? owner) { }

    /// <summary>The host moved or resized a popup or dialog.</summary>
    public virtual void Move(DesignSession session, RemoteWindowInfo info)
    {
        if (!_shown.TryGetValue(info.Id, out var s)) return;
        s.View.Width = info.Width / s.Scale;
        s.View.Height = info.Height / s.Scale;
        if (s.Popup != null)
        {
            if (s.Popup.Placement == PlacementMode.Relative)
            {
                s.Popup.HorizontalOffset = info.X / s.Scale;
                s.Popup.VerticalOffset = info.Y / s.Scale;
            }
        }
        else if (s.Dialog != null && s.Dialog.Owner != null)
        {
            var ownerView = session.GetView(info.OwnerWindowId);
            if (ownerView != null && PresentationSource.FromVisual(ownerView) != null)
            {
                var origin = ownerView.PointToScreen(new Point(0, 0));
                s.Dialog.Left = (origin.X + info.X) / s.Scale;
                s.Dialog.Top = (origin.Y + info.Y) / s.Scale;
            }
        }
    }

    /// <summary>The host closed a popup or dialog (or the connection went away).</summary>
    public virtual void Close(DesignSession session, RemoteWindowInfo info)
    {
        if (!_shown.TryGetValue(info.Id, out var s)) return;
        _shown.Remove(info.Id);
        s.ClosingByHost = true;
        s.View.Detach();
        if (s.Popup != null)
        {
            s.Popup.IsOpen = false;
            s.Popup.Child = null;
        }
        s.Dialog?.Close();
    }

    /// <summary>Closes everything this presenter shows.</summary>
    public void CloseAll(DesignSession session)
    {
        foreach (var s in _shown.Values.ToList()) Close(session, s.Info);
    }
}
