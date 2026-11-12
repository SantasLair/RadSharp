using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RadSharp.DesignHost.Protocol;
using RadSharp.VisualStudio.Client;

namespace RadSharp.DesignHost.Harness;

/// <summary>Palette | surface | inspector, each a <see cref="RemoteWindowView"/>, with Save / Undo / Redo and a status bar.</summary>
internal sealed class MainWindow : Window
{
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(30);

    private readonly Program.Options _options;
    private readonly RemoteWindowView _palette = new RemoteWindowView();
    private readonly RemoteWindowView _surface = new RemoteWindowView();
    private readonly RemoteWindowView _inspector = new RemoteWindowView();
    private readonly TextBlock _stateText = new TextBlock();
    private readonly TextBlock _navText = new TextBlock();
    private readonly TextBlock _messageText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _banner = new Border();
    private readonly TextBlock _bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _restartButton = new Button { Content = "Restart host", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
    private DesignSession? _session;
    private DesignDocument? _doc;
    private bool _screenshotTaken;
    private DispatcherTimer? _screenshotDeadline;
    private bool _screenshotScheduled;

    public MainWindow(Program.Options options)
    {
        _options = options;
        Title = "RadSharp design host harness";
        Width = 1280;
        Height = 800;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = SystemColors.ControlBrush;
        Content = BuildLayout();

        foreach (var v in new[] { _palette, _surface, _inspector })
        {
            v.ShouldForwardKey = ShouldForwardKey;
            v.FrameReceived += (s, e) => CheckScreenshotReady();
        }

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (s, e) => Save(), (s, e) => e.CanExecute = _doc != null && _session?.IsConnected == true));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (s, e) => _doc?.Command(DesignCommands.Undo), (s, e) => e.CanExecute = _doc?.CanUndo == true));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (s, e) => _doc?.Command(DesignCommands.Redo), (s, e) => e.CanExecute = _doc?.CanRedo == true));

        Loaded += async (s, e) => await StartAsync();
        Closed += (s, e) => _session?.Dispose();
    }

    // Ctrl+S, Ctrl+Z, Ctrl+Y (and Ctrl+Shift+Z) are the harness's own commands, as they will be Visual Studio's.
    private static bool ShouldForwardKey(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return true;
        var k = InputMapping.RealKey(e);
        return k != Key.S && k != Key.Z && k != Key.Y;
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { LastChildFill = true };

        var menu = new Menu();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "_Save", Command = ApplicationCommands.Save });
        var reload = new MenuItem { Header = "_Reload from disk" };
        reload.Click += (s, e) => _doc?.Reload();
        file.Items.Add(reload);
        file.Items.Add(new Separator());
        var exit = new MenuItem { Header = "E_xit" };
        exit.Click += (s, e) => Close();
        file.Items.Add(exit);
        menu.Items.Add(file);
        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(new MenuItem { Header = "_Undo", Command = ApplicationCommands.Undo });
        edit.Items.Add(new MenuItem { Header = "_Redo", Command = ApplicationCommands.Redo });
        edit.Items.Add(new Separator());
        foreach (var (header, name, gesture) in new[]
        {
            ("Cu_t", DesignCommands.Cut, "Ctrl+X"), ("_Copy", DesignCommands.Copy, "Ctrl+C"), ("_Paste", DesignCommands.Paste, "Ctrl+V"),
            ("_Delete", DesignCommands.Delete, "Del"), ("Select _All", DesignCommands.SelectAll, "Ctrl+A"),
        })
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (s, e) => _doc?.Command(name);
            edit.Items.Add(item);
        }
        menu.Items.Add(edit);
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        var toolbar = new ToolBar();
        toolbar.Items.Add(new Button { Content = "Save", Command = ApplicationCommands.Save });
        toolbar.Items.Add(new Button { Content = "Undo", Command = ApplicationCommands.Undo });
        toolbar.Items.Add(new Button { Content = "Redo", Command = ApplicationCommands.Redo });
        var tray = new ToolBarTray();
        tray.ToolBars.Add(toolbar);
        DockPanel.SetDock(tray, Dock.Top);
        root.Children.Add(tray);

        var bannerPanel = new DockPanel();
        DockPanel.SetDock(_restartButton, Dock.Right);
        bannerPanel.Children.Add(_restartButton);
        bannerPanel.Children.Add(_bannerText);
        _banner.Child = bannerPanel;
        _banner.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9));
        _banner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        _banner.BorderThickness = new Thickness(0, 1, 0, 1);
        _banner.Padding = new Thickness(8, 4, 8, 4);
        _banner.Visibility = Visibility.Collapsed;
        _restartButton.Click += async (s, e) => await RestartAsync();
        DockPanel.SetDock(_banner, Dock.Top);
        root.Children.Add(_banner);

        var status = new StatusBar();
        status.Items.Add(new StatusBarItem { Content = _stateText });
        status.Items.Add(new Separator());
        status.Items.Add(new StatusBarItem { Content = _navText });
        status.Items.Add(new Separator());
        status.Items.Add(new StatusBarItem { Content = _messageText });
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200), MinWidth = 80 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 100 });
        AddPane(grid, _palette, 0);
        AddSplitter(grid, 1);
        AddPane(grid, _surface, 2);
        AddSplitter(grid, 3);
        AddPane(grid, _inspector, 4);
        root.Children.Add(grid);

        UpdateStatus();
        return root;
    }

    private static void AddPane(Grid grid, RemoteWindowView view, int column)
    {
        Grid.SetColumn(view, column);
        grid.Children.Add(view);
    }

    private static void AddSplitter(Grid grid, int column)
    {
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = SystemColors.ControlDarkBrush };
        Grid.SetColumn(splitter, column);
        grid.Children.Add(splitter);
    }

    // ---- Session ----

    private async System.Threading.Tasks.Task StartAsync()
    {
        if (_options.ScreenshotPath != null)
        {
            _screenshotDeadline = new DispatcherTimer { Interval = ScreenshotTimeout };
            _screenshotDeadline.Tick += (s, e) =>
            {
                _screenshotDeadline.Stop();
                Program.Log("timed out waiting for frames from every pane");
                TakeScreenshotAndExit(2, 0);
            };
            _screenshotDeadline.Start();
        }

        if (!File.Exists(_options.FormPath))
        {
            Fail("Form file not found: " + _options.FormPath, canRestart: false);
            return;
        }
        if (_options.HostPath == null || !File.Exists(_options.HostPath))
        {
            Fail("Design host not found: " + (_options.HostPath ?? "(no RadSharp.slnx above this exe; pass --host or set RADSHARP_DESIGNHOST)"), canRestart: false);
            return;
        }

        try
        {
            _session = await DesignSession.StartAsync(_options.HostPath, "RadSharp.DesignHost.Harness");
        }
        catch (Exception ex)
        {
            Fail("Could not start the design host: " + ex.Message, canRestart: true);
            return;
        }
        Program.Log("connected, host version " + _session.Connection.HostVersion);
        HookSession(_session);
        _doc = _session.OpenDocument(_options.FormPath);
        HookDocument(_doc);
        _doc.Activate();
        Title = "RadSharp harness - " + Path.GetFileName(_options.FormPath);
        UpdateStatus();
    }

    private void HookSession(DesignSession session)
    {
        session.Log += (s, e) => Program.Log("host[" + e.Level + "] " + e.Text);
        session.Error += (s, e) =>
        {
            Program.Log("error: " + e.Text);
            _messageText.Text = "Error: " + e.Text;
            _messageText.Foreground = Brushes.DarkRed;
        };
        session.Disconnected += (s, e) =>
        {
            Fail(e.Reason, canRestart: true);
        };
    }

    private void HookDocument(DesignDocument doc)
    {
        doc.WindowOpened += (s, e) =>
        {
            Program.Log("window opened: " + e.Window);
            var view = e.Window.Role switch
            {
                WindowRole.Surface => _surface,
                WindowRole.Inspector => _inspector,
                WindowRole.Palette => _palette,
                _ => null,
            };
            view?.Attach(doc.Session, e.Window.Id);
            CheckScreenshotReady();
        };
        doc.WindowClosed += (s, e) => Program.Log("window closed: " + e.Window);
        doc.StateChanged += (s, e) =>
        {
            UpdateStatus();
            CommandManager.InvalidateRequerySuggested();
        };
        doc.Saved += (s, e) =>
        {
            _messageText.Text = e.Ok ? "Saved " + Path.GetFileName(e.Document.Path) : "Save failed: " + e.Error;
            _messageText.Foreground = e.Ok ? Brushes.Black : Brushes.DarkRed;
        };
        doc.NavigateToCode += (s, e) =>
        {
            Program.Log("navigate: " + e.File + ":" + e.Line);
            _navText.Text = "Code: " + Path.GetFileName(e.File) + ":" + e.Line;
            _navText.ToolTip = e.File + ":" + e.Line;
        };
    }

    private async System.Threading.Tasks.Task RestartAsync()
    {
        _banner.Visibility = Visibility.Collapsed;
        try
        {
            if (_session != null && _session.CanRestart)
            {
                await _session.RestartAsync();
                return;
            }
            _session?.Dispose();
            _session = null;
            _doc = null;
            await StartAsync();
        }
        catch (Exception ex)
        {
            Fail("Could not restart the design host: " + ex.Message, canRestart: true);
        }
    }

    private void Save()
    {
        if (_doc == null) return;
        _messageText.Text = "Saving...";
        _messageText.Foreground = Brushes.Black;
        _doc.Save();
    }

    private void Fail(string message, bool canRestart)
    {
        Program.Log("FAILED: " + message);
        _bannerText.Text = message;
        _restartButton.Visibility = canRestart && _options.HostPath != null ? Visibility.Visible : Visibility.Collapsed;
        _banner.Visibility = Visibility.Visible;
        UpdateStatus();
        if (_options.ScreenshotPath != null) TakeScreenshotAndExit(1, 200);
    }

    private void UpdateStatus()
    {
        if (_doc == null)
        {
            _stateText.Text = _session?.IsConnected == true ? "Opening..." : "Not connected";
            return;
        }
        var parts = new List<string> { _doc.IsDirty ? "Modified" : "Saved" };
        parts.Add(_doc.HasSelection ? "selection" : "no selection");
        if (_doc.CanUndo) parts.Add("can undo");
        if (_doc.CanRedo) parts.Add("can redo");
        _stateText.Text = string.Join(", ", parts);
        Title = "RadSharp harness - " + Path.GetFileName(_doc.Path) + (_doc.IsDirty ? "*" : "");
    }

    // ---- Screenshot ----

    private void CheckScreenshotReady()
    {
        if (_options.ScreenshotPath == null || _screenshotScheduled || _doc == null) return;
        // Ready when the surface is announced and every announced pane we show has drawn at least once. The host announces
        // a document's panes together, before any frame, so this cannot fire between two announcements.
        if (_doc.GetWindow(WindowRole.Surface) == null) return;
        var panes = new[] { _palette, _surface, _inspector }.Where(v => v.WindowId != 0).ToList();
        if (panes.Any(v => v.FrameCount == 0)) return;
        Program.Log("first frames received from " + panes.Count + " pane(s)");
        TakeScreenshotAndExit(0, _options.DelayMs);
    }

    private void TakeScreenshotAndExit(int exitCode, int delayMs)
    {
        if (_screenshotScheduled) return;
        _screenshotScheduled = true;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            // Let pending frames and layout land first.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try
                {
                    SaveScreenshot(_options.ScreenshotPath!);
                    Program.Log("screenshot: " + _options.ScreenshotPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    Program.Log("screenshot failed: " + ex.Message);
                    exitCode = 3;
                }
                _screenshotTaken = true;
                _screenshotDeadline?.Stop();
                Application.Current.Shutdown(exitCode);
            }));
        };
        timer.Start();
    }

    private void SaveScreenshot(string path)
    {
        if (_screenshotTaken) return;
        var root = (FrameworkElement)Content;
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(root);
        int w = (int)Math.Round(root.ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(root.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("The window has no size.");
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Background first: the window's own background is not part of its content's visual.
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        rtb.Render(bg);
        rtb.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
