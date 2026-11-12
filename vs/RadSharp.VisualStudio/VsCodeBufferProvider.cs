using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using RadSharp.VisualStudio.Client;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// The host's code reads and edits, against Visual Studio's open editor buffers. A file open in VS is read from its
    /// buffer (unsaved edits included) and edited in place as one minimal edit (one undo unit, not saved); a file that
    /// isn't open is read by the host from disk and written here, keeping its encoding. Either way VS then shows the line.
    /// The session calls this on the UI thread.
    /// </summary>
    internal sealed class VsCodeBufferProvider : ICodeBufferProvider
    {
        private readonly RadSharpPackage _package;
        private readonly DesignSession _session;

        public VsCodeBufferProvider(RadSharpPackage package, DesignSession session)
        {
            _package = package;
            _session = session;
        }

        public bool TryGetText(string file, out string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            text = "";
            var buffer = FindOpenBuffer(file);
            if (buffer == null)
                return false;
            text = buffer.CurrentSnapshot.GetText();
            return true;
        }

        public void ApplyEdit(DesignDocument? document, string file, string text, int line, string baseHash)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = FindOpenBuffer(file);
            if (buffer != null)
            {
                var snapshot = buffer.CurrentSnapshot;
                var current = snapshot.GetText();
                if (!CodeHash.Matches(current, baseHash))
                    throw new CodeEditConflictException();
                var change = TextDiff.MinimalReplacement(current, text);
                if (!change.IsEmpty)
                {
                    using var edit = buffer.CreateEdit();
                    edit.Replace(new Span(change.Start, change.Length), change.Text);
                    edit.Apply();
                }
            }
            else
            {
                // Not open: hash the disk text the way the host read it (File.ReadAllText) before writing.
                var onDisk = File.Exists(file) ? File.ReadAllText(file) : "";
                if (!CodeHash.Matches(onDisk, baseHash))
                    throw new CodeEditConflictException();
                FileText.WritePreservingEncoding(file, text);
            }

            _session.RaiseNavigateToCode(document, file, line);
        }


        /// <summary>The editor buffer of <paramref name="file"/> if it is open in VS (in the running document table).</summary>
        private ITextBuffer? FindOpenBuffer(string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (((IServiceProvider)_package).GetService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable rdt)
                return null;

            var hr = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, file, out _, out _, out var docDataPtr, out _);
            if (ErrorHandler.Failed(hr) || docDataPtr == IntPtr.Zero)
                return null;

            object docData;
            try
            {
                docData = Marshal.GetObjectForIUnknown(docDataPtr);
            }
            finally
            {
                Marshal.Release(docDataPtr);
            }

            IVsTextBuffer? vsBuffer = docData as IVsTextBuffer;
            if (vsBuffer == null && docData is IVsTextBufferProvider provider && ErrorHandler.Succeeded(provider.GetTextBuffer(out var lines)))
                vsBuffer = lines;
            if (vsBuffer == null)
                return null;

            var componentModel = ((IServiceProvider)_package).GetService(typeof(SComponentModel)) as IComponentModel;
            var adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
            return adapters?.GetDocumentBuffer(vsBuffer);
        }
    }
}
