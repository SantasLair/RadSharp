using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RadSharp.VisualStudio
{
    /// <summary>Writes to the "RadSharp Designer" Output pane and the VS status bar. UI thread only.</summary>
    internal sealed class DesignerOutput
    {
        public const string PaneTitle = "RadSharp Designer";
        private static Guid s_paneGuid = new Guid("3464abcd-fcf4-458d-bf1c-07ed8524614b");

        private readonly IServiceProvider _services;
        private IVsOutputWindowPane? _pane;

        public DesignerOutput(IServiceProvider services) => _services = services;

        public void WriteLine(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            GetPane()?.OutputStringThreadSafe(DateTime.Now.ToString("HH:mm:ss ") + text + Environment.NewLine);
        }

        /// <summary>An error from a designer: Output pane (brought to the front of the Output window) and status bar.</summary>
        public void Error(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            WriteLine("Error: " + text);
            GetPane()?.Activate();
            SetStatus(PaneTitle + ": " + text);
        }

        public void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_services.GetService(typeof(SVsStatusbar)) is not IVsStatusbar statusBar)
                return;
            if (ErrorHandler.Succeeded(statusBar.IsFrozen(out var frozen)) && frozen != 0)
                return;
            statusBar.SetText(text);
        }

        private IVsOutputWindowPane? GetPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pane != null)
                return _pane;
            if (_services.GetService(typeof(SVsOutputWindow)) is not IVsOutputWindow output)
                return null;
            if (ErrorHandler.Failed(output.GetPane(ref s_paneGuid, out _pane)) || _pane == null)
            {
                if (ErrorHandler.Failed(output.CreatePane(ref s_paneGuid, PaneTitle, fInitVisible: 1, fClearWithSolution: 0)))
                    return null;
                output.GetPane(ref s_paneGuid, out _pane);
            }

            return _pane;
        }
    }
}
