using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RadSharp.VisualStudio.Client;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// The one design host per Visual Studio instance: a <see cref="DesignSession"/> started on first use, shared by every
    /// designer tab and the tool windows. Tracks the active designer for the tool windows, restarts the host after a
    /// crash, and follows the VS theme. UI thread only.
    /// </summary>
    internal sealed class DesignHostService : IDisposable
    {
        private readonly RadSharpPackage _package;
        private Task<DesignSession>? _starting;
        private DesignSession? _session;
        private ClientDesignerView? _active;
        private bool? _sessionIsDark;
        private bool _disposed;

        public DesignHostService(RadSharpPackage package)
        {
            _package = package;
            VSColorTheme.ThemeChanged += OnThemeChanged;
        }

        /// <summary>The running session, or null before the first start or after a failed start.</summary>
        public DesignSession? Session => _session;

        /// <summary>The message of the last disconnect or failed start, while the host is down; otherwise null.</summary>
        public string? DownReason { get; private set; }

        /// <summary>The active designer tab, or null when the active document is something else.</summary>
        public ClientDesignerView? ActiveDesigner => _active;

        /// <summary>The host went down (crash, or a failed start). Designer tabs show a banner with a restart button.</summary>
        public event EventHandler? HostDown;

        /// <summary>A session is available (first start or restart). Tabs whose document isn't open in it open it now.</summary>
        public event EventHandler? HostUp;

        /// <summary>The active designer, or the panes of its document, changed. The tool windows re-attach.</summary>
        public event EventHandler? ActiveDesignerChanged;

        /// <summary>Starts the host the first time; later calls return the same session.</summary>
        public async Task<DesignSession> GetSessionAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
            if (_disposed)
                throw new ObjectDisposedException(nameof(DesignHostService));
            if (_session != null)
                return _session;

            // Concurrent opens share one start; a failed start is forgotten so the next open (or Restart) retries.
            var starting = _starting ??= StartAsync();
            try
            {
                return await starting;
            }
            finally
            {
                if (ReferenceEquals(_starting, starting) && starting.IsCompleted)
                    _starting = null;
            }
        }

        /// <summary>Restarts a crashed host (documents reopen under the same ids), or retries a failed start.</summary>
        public async Task RestartAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
            if (_disposed)
                return;
            try
            {
                if (_session != null && _session.CanRestart)
                {
                    var dark = IsVsThemeDark();
                    using (WithHostTheme(dark))
                        await _session.RestartAsync();
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                    _sessionIsDark = dark;
                    DownReason = null;
                    _package.Output.WriteLine("Design host restarted.");
                    HostUp?.Invoke(this, EventArgs.Empty);
                    return;
                }

                _session?.Dispose();
                _session = null;
                _starting = null;
                await GetSessionAsync();
            }
            catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                OnDown("Could not restart the design host: " + ex.Message);
            }
        }

        private async Task<DesignSession> StartAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
            if (!DesignHostLocator.TryGetHostPath(out var hostPath))
            {
                throw new InvalidOperationException("The design host was not found at " + hostPath + ".");
            }

            DesignSession session;
            var dark = IsVsThemeDark();
            try
            {
                _package.Output.WriteLine("Starting design host " + hostPath + (dark ? " (dark theme)" : ""));
                using (WithHostTheme(dark))
                    session = await DesignSession.StartAsync(hostPath, "RadSharp.VisualStudio");
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
            }
            catch (Exception ex) when (!Microsoft.VisualStudio.ErrorHandler.IsCriticalException(ex))
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                OnDown("Could not start the design host: " + ex.Message);
                throw;
            }

            if (_disposed)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(DesignHostService));
            }

            session.CodeBuffers = new VsCodeBufferProvider(_package, session);
            session.Presenter = new VsWindowPresenter(_package);
            session.Disconnected += (s, e) => OnDown(e.Reason);
            session.Log += OnSessionLog;
            session.Error += OnSessionError;
            _session = session;
            _sessionIsDark = dark;
            DownReason = null;
            _package.Output.WriteLine("Design host connected (version " + session.Connection.HostVersion + ").");
            HostUp?.Invoke(this, EventArgs.Empty);
            return session;
        }

        private void OnDown(string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DownReason = reason;
            _package.Output.Error(reason);
            HostDown?.Invoke(this, EventArgs.Empty);
            ActiveDesignerChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnSessionLog(object? sender, HostLogEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Level >= 1)
                _package.Output.WriteLine("host: " + e.Text);
        }

        private void OnSessionError(object? sender, HostErrorEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Document errors reach the user through that tab's IDesignerView.Error; report the rest here.
            if (e.Document == null)
                _package.Output.Error(e.Text);
        }

        // ---- active designer ---------------------------------------------------------------------------------------------

        public void SetActiveDesigner(ClientDesignerView? view)
        {
            if (ReferenceEquals(_active, view))
                return;
            _active?.Deactivate();
            _active = view;
            ActiveDesignerChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>A designer's document announced or lost panes (open, or after a restart).</summary>
        public void OnDesignerWindowsChanged(ClientDesignerView view)
        {
            if (ReferenceEquals(_active, view))
                ActiveDesignerChanged?.Invoke(this, EventArgs.Empty);
        }

        public void OnDesignerClosed(ClientDesignerView view)
        {
            if (ReferenceEquals(_active, view))
                SetActiveDesigner(null);
        }

        // ---- theme ----------------------------------------------------------------------------------------------------------

        private static bool IsVsThemeDark()
        {
            var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            return ThemeDetection.IsDark(c.R, c.G, c.B);
        }

        /// <summary>
        /// Sets RADSHARP_THEME in this process while the host is launched (the child inherits the environment), then
        /// restores it, so programs VS starts later (F5 of a RadSharp app) don't inherit the designer's theme.
        /// </summary>
        private static IDisposable WithHostTheme(bool dark)
        {
            var previous = Environment.GetEnvironmentVariable(ThemeDetection.EnvironmentVariable);
            Environment.SetEnvironmentVariable(ThemeDetection.EnvironmentVariable, dark ? "dark" : null);
            return new Restore(() => Environment.SetEnvironmentVariable(ThemeDetection.EnvironmentVariable, previous));
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var session = _session;
            if (session == null || !session.IsConnected || _sessionIsDark == IsVsThemeDark())
                return;

            // The host picks its theme at startup. Restarting reloads every form from disk, so only do it when nothing
            // would be lost.
            if (session.Documents.Any(d => d.IsDirty))
            {
                _package.Output.WriteLine("The Visual Studio theme changed. Save your forms and use \"Restart designer host\" (or reopen Visual Studio) to apply it to the designer.");
                return;
            }

            _package.Output.WriteLine("The Visual Studio theme changed; restarting the design host to apply it.");
            _package.JoinableTaskFactory.RunAsync(RestartAsync).FileAndForget("RadSharp/Designer/ThemeRestart");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            _session?.Dispose();
            _session = null;
        }

        private sealed class Restore : IDisposable
        {
            private Action? _action;

            public Restore(Action action) => _action = action;

            public void Dispose()
            {
                _action?.Invoke();
                _action = null;
            }
        }
    }
}
