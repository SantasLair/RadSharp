using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// Editor factory registered for ".json" (see the remarks on <see cref="RadSharpPackage"/>). It only accepts
    /// *.radform.json; for every other file it returns VS_E_UNSUPPORTEDFORMAT so VS moves on to the normal JSON editor.
    /// </summary>
    [Guid(GuidString)]
    internal sealed class DesignerEditorFactory : IVsEditorFactory
    {
        public const string GuidString = "68869388-96cd-4be2-a244-4569f414be61";
        public static readonly Guid Guid = new Guid(GuidString);

        public const string FormExtension = ".radform.json";

        private readonly RadSharpPackage _package;

        public DesignerEditorFactory(RadSharpPackage package) => _package = package;

        public static bool IsFormFile(string? path) =>
            path != null && path.EndsWith(FormExtension, StringComparison.OrdinalIgnoreCase);

        public int SetSite(IOleServiceProvider psp) => VSConstants.S_OK;

        public int Close() => VSConstants.S_OK;

        public int MapLogicalView(ref Guid rguidLogicalView, out string pbstrPhysicalView)
        {
            // One physical view (the primary one, named by null). Code / TextView / Debugging requests are refused so
            // VS opens those in the text editor instead.
            pbstrPhysicalView = null!;
            return rguidLogicalView == VSConstants.LOGVIEWID.Primary_guid || rguidLogicalView == VSConstants.LOGVIEWID.Designer_guid
                ? VSConstants.S_OK
                : VSConstants.E_NOTIMPL;
        }

        public int CreateEditorInstance(uint grfCreateDoc, string pszMkDocument, string pszPhysicalView, IVsHierarchy pvHier, uint itemid,
            IntPtr punkDocDataExisting, out IntPtr ppunkDocView, out IntPtr ppunkDocData, out string pbstrEditorCaption,
            out Guid pguidCmdUI, out int pgrfCDW)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pbstrEditorCaption = null!;
            pguidCmdUI = Guid;
            pgrfCDW = 0;

            if ((grfCreateDoc & (VSConstants.CEF_OPENFILE | VSConstants.CEF_SILENT)) == 0)
                return VSConstants.E_INVALIDARG;

            if (!IsFormFile(pszMkDocument))
                return VSConstants.VS_E_UNSUPPORTEDFORMAT;

            // The document is already open in another editor (e.g. the text editor): its doc data is a text buffer we
            // can't share. VS then offers to close the other editor.
            if (punkDocDataExisting != IntPtr.Zero)
                return VSConstants.VS_E_INCOMPATIBLEDOCDATA;

            // Doc data and doc view are the same object. VS calls IVsPersistDocData.LoadDocData next.
            var pane = new DesignerPane(_package);
            ppunkDocView = Marshal.GetIUnknownForObject(pane);
            ppunkDocData = Marshal.GetIUnknownForObject(pane);
            pbstrEditorCaption = " [Design]";
            return VSConstants.S_OK;
        }
    }
}
