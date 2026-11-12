using System;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RadSharp.VisualStudio.Client;

namespace RadSharp.VisualStudio
{
    /// <summary>Host dialogs parented to Visual Studio when they have no owner view in a VS window.</summary>
    internal sealed class VsWindowPresenter : RemoteWindowPresenter
    {
        private readonly RadSharpPackage _package;

        public VsWindowPresenter(RadSharpPackage package) => _package = package;

        protected override void OnDialogCreated(Window dialog, RemoteWindowInfo info, RemoteWindowView? owner)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            base.OnDialogCreated(dialog, info, owner);
            if (dialog.Owner != null)
                return;
            if (((IServiceProvider)_package).GetService(typeof(SVsUIShell)) is IVsUIShell shell
                && ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out var hwnd)) && hwnd != IntPtr.Zero)
            {
                new WindowInteropHelper(dialog).Owner = hwnd;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
        }
    }
}
