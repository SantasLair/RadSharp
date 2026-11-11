using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RadSharp.DesignHost.Protocol;
using RadSharp.VisualStudio.Client;
using Task = System.Threading.Tasks.Task;

namespace RadSharp.VisualStudio
{
    /// <summary>Creates the real designer, or a placeholder that explains why there is none.</summary>
    internal sealed class ClientDesignerViewFactory : IDesignerViewFactory
    {
        private readonly RadSharpPackage _package;

        public ClientDesignerViewFactory(RadSharpPackage package) => _package = package;

        public IDesignerView Create(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!DesignHostLocator.TryGetHostPath(out var host))
            {
                return new PlaceholderDesignerView(path,
                    "The RADSharp design host was not found at\n" + host + "\n\nReinstall the extension, or set the " +
                    DesignHostLocator.EnvironmentVariable + " environment variable to RadSharpDesignHost.exe (or its folder) and restart Visual Studio.");
            }

            _package.ShowToolWindowsOnce();
            return new ClientDesignerView(_package, _package.HostService, path);
        }
    }

    /// <summary>
    /// One designer tab over a host <see cref="DesignDocument"/>: the Surface window in a <see cref="RemoteWindowView"/>,
    /// plus a banner when the host is down. The Inspector and Palette windows are shown by the tool windows while this tab
    /// is the active document. UI thread only.
    /// </summary>
    internal sealed class ClientDesignerView : IDesignerView
    {
        private readonly RadSharpPackage _package;
        private readonly DesignHostService _service;
        private readonly RemoteWindowView _surface = new RemoteWindowView();
        private readonly TextBlock _status = new TextBlock
        {
            Text = "Starting the RADSharp designer…",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
        };
        private readonly Border _banner = new Border { Visibility = Visibility.Collapsed, Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
        private readonly TextBlock _bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _restart = new Button { Content = "Restart designer host", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        private string _path;
        private DesignDocument? _doc;
        private bool _dirty;
        private bool _active;
        private bool _disposed;

        public ClientDesignerView(RadSharpPackage package, DesignHostService service, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package;
            _service = service;
            _path = path;

            _surface.ShouldForwardKey = e =>
                KeyRouting.Classify(InputMapping.RealKey(e), Keyboard.Modifiers, KeyContext.Designer) == KeyRoute.Host;
            _surface.SetResourceReference(RemoteWindowView.BackgroundProperty, EnvironmentColors.DesignerBackgroundBrushKey);
            _status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var bannerPanel = new DockPanel();
            DockPanel.SetDock(_restart, Dock.Right);
            bannerPanel.Children.Add(_restart);
            bannerPanel.Children.Add(_bannerText);
            _banner.Child = bannerPanel;
            _banner.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.SystemInfoBackgroundBrushKey);
            _banner.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            _bannerText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemInfoTextBrushKey);
            _restart.Click += (s, e) => _package.JoinableTaskFactory.RunAsync(_service.RestartAsync).FileAndForget("RadSharp/Designer/Restart");

            var body = new Grid();
            body.Children.Add(_surface);
            body.Children.Add(_status);
            var root = new DockPanel();
            DockPanel.SetDock(_banner, Dock.Top);
            root.Children.Add(_banner);
            root.Children.Add(body);
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.DesignerBackgroundBrushKey);
            Content = root;

            _service.HostUp += OnHostUp;
            _service.HostDown += OnHostDown;
            _package.JoinableTaskFactory.RunAsync(OpenAsync).FileAndForget("RadSharp/Designer/Open");
        }

        public FrameworkElement Content { get; }

        /// <summary>The host document, once the host is running.</summary>
        public DesignDocument? Document => _doc;

        public bool IsDirty => _doc?.IsDirty == true;

        public event EventHandler? DirtyChanged;

        public event EventHandler<NavigateToCodeEventArgs>? NavigateToCode;

        public event EventHandler<DesignerErrorEventArgs>? Error;

        private bool Connected => _doc != null && !_doc.IsClosed && _doc.Session.IsConnected;

        // ---- open / host state ---------------------------------------------------------------------------------------------

        private async Task OpenAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
            try
            {
                var session = await _service.GetSessionAsync();
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                EnsureDocument(session);
            }
            catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowBanner(_service.DownReason ?? ex.Message);
            }
        }

        /// <summary>Opens this tab's file in <paramref name="session"/> unless it is already open there.</summary>
        private void EnsureDocument(DesignSession session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed || (_doc != null && ReferenceEquals(_doc.Session, session) && !_doc.IsClosed))
                return;
            UnhookDocument();
            _doc = session.OpenDocument(_path);
            _doc.WindowOpened += OnWindowOpened;
            _doc.WindowClosed += OnWindowClosed;
            _doc.StateChanged += OnStateChanged;
            _doc.NavigateToCode += OnNavigateToCode;
            _doc.Error += OnError;
            if (_active)
                _doc.Activate();
            HideBanner();
        }

        private void UnhookDocument()
        {
            if (_doc == null)
                return;
            _doc.WindowOpened -= OnWindowOpened;
            _doc.WindowClosed -= OnWindowClosed;
            _doc.StateChanged -= OnStateChanged;
            _doc.NavigateToCode -= OnNavigateToCode;
            _doc.Error -= OnError;
        }

        private void OnHostUp(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_service.Session != null)
                EnsureDocument(_service.Session);
            HideBanner();
            _status.Text = "Starting the RADSharp designer…";
            if (_doc?.GetWindow(WindowRole.Surface) == null)
                _status.Visibility = Visibility.Visible;
        }

        private void OnHostDown(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowBanner(_service.DownReason ?? "The design host stopped.");
        }

        private void ShowBanner(string message)
        {
            _bannerText.Text = message + "  Restarting reloads the form from disk.";
            _banner.Visibility = Visibility.Visible;
            _status.Text = "The RADSharp design host is not running.";
            _status.Visibility = Visibility.Visible;
        }

        private void HideBanner() => _banner.Visibility = Visibility.Collapsed;

        // ---- document events -------------------------------------------------------------------------------------------------

        private void OnWindowOpened(object? sender, RemoteWindowEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_doc == null)
                return;
            if (e.Window.Role == WindowRole.Surface)
            {
                _surface.Attach(_doc.Session, e.Window.Id);
                _status.Visibility = Visibility.Collapsed;
            }

            _service.OnDesignerWindowsChanged(this);
        }

        private void OnWindowClosed(object? sender, RemoteWindowEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _service.OnDesignerWindowsChanged(this);
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dirty = IsDirty;
            if (dirty != _dirty)
            {
                _dirty = dirty;
                DirtyChanged?.Invoke(this, EventArgs.Empty);
            }

            // Undo / Redo / Cut ... enablement changed.
            if (((IServiceProvider)_package).GetService(typeof(SVsUIShell)) is IVsUIShell shell)
                shell.UpdateCommandUI(0);
        }

        private void OnNavigateToCode(object? sender, Client.NavigateToCodeEventArgs e) =>
            NavigateToCode?.Invoke(this, new NavigateToCodeEventArgs(e.File, e.Line));

        private void OnError(object? sender, HostErrorEventArgs e) => Error?.Invoke(this, new DesignerErrorEventArgs(e.Text));

        // ---- IDesignerView ---------------------------------------------------------------------------------------------------

        /// <remarks>
        /// Save Copy As: the protocol's SaveDocument always switches the host document to the new path. With no unsaved
        /// changes the file on disk is the document, so it is copied. With unsaved changes the copy is saved and the
        /// document is then saved back to its own path, so it keeps its path; the original file is written too and the
        /// tab becomes clean (reported in the Output pane).
        /// </remarks>
        public async Task SaveAsync(string path, bool remember, CancellationToken cancellationToken)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var doc = _doc;
            if (doc == null || !Connected)
                throw new InvalidOperationException("The RADSharp design host is not running. Restart it from the banner in the designer tab.");

            if (remember)
            {
                var saved = await doc.SaveAsync(path);
                if (!saved.Ok)
                    throw new IOException(saved.Error);
                _path = path;
                return;
            }

            var original = doc.Path;
            if (!doc.IsDirty)
            {
                File.Copy(original, path, overwrite: true);
                return;
            }

            var copy = await doc.SaveAsync(path);
            if (!copy.Ok)
                throw new IOException(copy.Error);
            var back = await doc.SaveAsync(original);
            if (!back.Ok)
                throw new IOException("Saved the copy to " + path + ", but could not switch the designer back to " + original + ": " + back.Error);
            _package.Output.WriteLine("Save Copy As wrote " + path + " and also saved " + Path.GetFileName(original) +
                " (the designer can't write a copy without saving).");
        }

        public void Reload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _doc?.Reload();
        }

        public bool CanExecute(string command)
        {
            if (!Connected)
                return false;
            var doc = _doc!;
            return command switch
            {
                DesignerCommands.Undo => doc.CanUndo,
                DesignerCommands.Redo => doc.CanRedo,
                DesignerCommands.Cut or DesignerCommands.Copy or DesignerCommands.Delete => doc.HasSelection,
                DesignerCommands.Paste or DesignerCommands.SelectAll => true,
                _ => false,
            };
        }

        public void ExecuteCommand(string command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Connected)
                _doc!.Command(command);
        }

        public void Activate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _active = true;
            if (Connected)
                _doc!.Activate();
            if (_surface.IsVisible && _surface.WindowId != 0 && !_surface.IsKeyboardFocusWithin)
                _surface.Focus();
        }

        /// <summary>Another document became active.</summary>
        public void Deactivate() => _active = false;

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
                return;
            _disposed = true;
            _service.HostUp -= OnHostUp;
            _service.HostDown -= OnHostDown;
            _service.OnDesignerClosed(this);
            _surface.Detach();
            var doc = _doc;
            UnhookDocument();
            _doc = null;
            // Also while disconnected: the session forgets it, so a restart doesn't reopen it.
            if (doc != null && !doc.IsClosed)
                doc.Close();
        }
    }
}
