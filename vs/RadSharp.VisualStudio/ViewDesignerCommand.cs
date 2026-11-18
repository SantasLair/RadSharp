using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// View Designer (Shift+F7, and the item's context menu in Solution Explorer) on X.cs opens X.radform.json in the
    /// designer, as it opens Form1.cs's designer in a WinForms project.
    /// </summary>
    /// <remarks>
    /// A priority command target: VS asks it before the project system, which knows nothing about RadSharp forms and
    /// would hide or disable the command for a plain .cs item. It claims the command only when the single selected item
    /// (the item in Solution Explorer, or the document whose editor has focus) is a .cs file with a form file next to
    /// it; everything else gets OLECMDERR_E_NOTSUPPORTED and goes on to the normal handlers, so WinForms and every other
    /// designer keep their View Designer.
    /// </remarks>
    internal sealed class ViewDesignerCommand : IOleCommandTarget
    {
        private static readonly Guid s_std97 = VSConstants.GUID_VSStandardCommandSet97;

        private readonly RadSharpPackage _package;
        private readonly IVsMonitorSelection _selection;

        public ViewDesignerCommand(RadSharpPackage package, IVsMonitorSelection selection)
        {
            _package = package;
            _selection = selection;
        }

        private static bool IsViewDesigner(Guid group, uint id) =>
            group == s_std97 && id == (uint)VSConstants.VSStd97CmdID.ViewForm;

        /// <summary>The form file for the current selection, or null.</summary>
        private string? SelectedForm()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return FormFiles.ExistingFormFileFor(SelectedFile());
        }

        /// <summary>The path of the single selected item, or null (nothing, several items, or not a file).</summary>
        private string? SelectedFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var hierPtr = IntPtr.Zero;
            var containerPtr = IntPtr.Zero;
            try
            {
                if (ErrorHandler.Failed(_selection.GetCurrentSelection(out hierPtr, out var itemId, out var multi, out containerPtr))
                    || multi != null || hierPtr == IntPtr.Zero || itemId == (uint)VSConstants.VSITEMID.Selection)
                    return null;
                if (Marshal.GetObjectForIUnknown(hierPtr) is IVsProject project
                    && ErrorHandler.Succeeded(project.GetMkDocument(itemId, out var path)))
                    return path;
                return null;
            }
            finally
            {
                if (hierPtr != IntPtr.Zero) Marshal.Release(hierPtr);
                if (containerPtr != IntPtr.Zero) Marshal.Release(containerPtr);
            }
        }

        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (prgCmds == null || cCmds != 1 || !IsViewDesigner(pguidCmdGroup, prgCmds[0].cmdID) || SelectedForm() == null)
                return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            return VSConstants.S_OK;
        }

        int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsViewDesigner(pguidCmdGroup, nCmdID) || !(SelectedForm() is { } form))
                return (int)Constants.OLECMDERR_E_NOTSUPPORTED;

            const uint VSCmdOptQueryParameterList = 1;
            if ((nCmdexecopt >> 16) != VSCmdOptQueryParameterList)
                CodeNavigator.OpenDesigner(_package, form);
            return VSConstants.S_OK;
        }
    }
}
