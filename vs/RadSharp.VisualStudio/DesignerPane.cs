using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;
using IPersistFileFormat = Microsoft.VisualStudio.Shell.Interop.IPersistFileFormat;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// One designer document tab: doc view and doc data in one object. Handles persistence (dirty asterisk, Save, Save
    /// As, Save All, close prompt), reload when the file changes on disk, Edit-command routing and navigation; the tab's
    /// content comes from an <see cref="IDesignerView"/>.
    /// </summary>
    /// <remarks>
    /// Commands: VS routes commands to the active pane's IOleCommandTarget before the global handlers. We claim only the
    /// Edit commands below and answer OLECMDERR_E_NOTSUPPORTED for the rest, so Save (Ctrl+S), Save All, Close, etc.
    /// continue to VS, which calls back into IVsPersistDocData / IPersistFileFormat.
    /// </remarks>
    internal sealed class DesignerPane : WindowPane, IVsPersistDocData, IPersistFileFormat, IOleCommandTarget,
        IVsFileChangeEvents, IVsDocDataFileChangeControl
    {
        private const uint FormatIndex = 0;
        private const string FormatList = "RadSharp form (*.radform.json)\n*.radform.json\n\n";

        private static readonly Guid s_std97 = VSConstants.GUID_VSStandardCommandSet97;

        private readonly RadSharpPackage _package;
        private readonly Border _host = new Border();
        private IDesignerView? _view;
        private string _path = "";
        private uint _docCookie;
        private uint _fileChangeCookie;
        private int _ignoreFileChanges;
        private bool _reloadPending;

        public DesignerPane(RadSharpPackage package)
            : base(null)
        {
            _package = package;
            Content = _host;
        }

        private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The tab's content provider, once the document is loaded.</summary>
        internal IDesignerView? View => _view;

        /// <summary>Claims the keys the designer surface needs before VS turns them into commands (see <see cref="KeyClaim"/>).</summary>
        protected override bool PreProcessMessage(ref System.Windows.Forms.Message m) =>
            KeyClaim.PreProcess(ref m, _host, KeyContext.Designer) || base.PreProcessMessage(ref m);

        /// <summary>Global VS services through the package (sited from the start, unlike this pane).</summary>
        private object? Service(Type serviceType) => ((System.IServiceProvider)_package).GetService(serviceType);

        // ---- lifetime ------------------------------------------------------------------------------------------------

        protected override void OnClose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CloseView();
            base.OnClose();
        }

        internal void OnActivated()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _view?.Activate();
        }

        private void OpenView(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CloseView();

            _path = path;
            var view = _package.ViewFactory.Create(path);
            view.DirtyChanged += OnViewDirtyChanged;
            view.NavigateToCode += OnViewNavigateToCode;
            view.Error += OnViewError;
            _view = view;
            _host.Child = view.Content;
            AdviseFileChanges();
        }

        private void CloseView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            UnadviseFileChanges();
            if (_view == null)
                return;
            var view = _view;
            _view = null;
            view.DirtyChanged -= OnViewDirtyChanged;
            view.NavigateToCode -= OnViewNavigateToCode;
            view.Error -= OnViewError;
            _host.Child = null;
            view.Dispose();
        }

        // ---- view events -----------------------------------------------------------------------------------------------

        private void OnViewDirtyChanged(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dirty = _view?.IsDirty == true;
            if (dirty)
                QueryEdit();
            if (_docCookie != 0 && Service(typeof(SVsUIShell)) is IVsUIShell shell)
                shell.UpdateDocDataIsDirtyFeedback(_docCookie, dirty ? 1 : 0);
        }

        private void OnViewNavigateToCode(object? sender, NavigateToCodeEventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(() => CodeNavigator.NavigateAsync(_package, e.File, e.Line))
                .FileAndForget("RadSharp/Designer/NavigateToCode");
        }

        private void OnViewError(object? sender, DesignerErrorEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package.Output.Error(e.Text);
        }

        /// <summary>
        /// First edit: let source control check the file out (or warn that it is read-only). The view has already
        /// changed, so a refusal can only be reported; the save prompt handles it later.
        /// </summary>
        private void QueryEdit()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Service(typeof(SVsQueryEditQuerySave)) is not IVsQueryEditQuerySave2 qeqs)
                return;
            var hr = qeqs.QueryEditFiles((uint)tagVSQueryEditFlags.QEF_AllowInMemoryEdits, 1, new[] { _path }, null, null,
                out var verdict, out _);
            if (ErrorHandler.Succeeded(hr) && verdict != (uint)tagVSQueryEditResult.QER_EditOK)
                _package.Output.WriteLine($"{Path.GetFileName(_path)} is not editable on disk (read-only or not checked out); you will be asked where to save.");
        }

        // ---- IVsPersistDocData -----------------------------------------------------------------------------------------

        int IVsPersistDocData.GetGuidEditorType(out Guid pClassID)
        {
            pClassID = DesignerEditorFactory.Guid;
            return VSConstants.S_OK;
        }

        int IVsPersistDocData.IsDocDataDirty(out int pfDirty)
        {
            pfDirty = _view?.IsDirty == true ? 1 : 0;
            return VSConstants.S_OK;
        }

        int IVsPersistDocData.SetUntitledDocPath(string pszDocDataPath) => VSConstants.S_OK;

        int IVsPersistDocData.LoadDocData(string pszMkDocument)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ((IPersistFileFormat)this).Load(pszMkDocument, 0, 0);
        }

        int IVsPersistDocData.SaveDocData(VSSAVEFLAGS dwSave, out string pbstrMkDocumentNew, out int pfSaveCanceled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pbstrMkDocumentNew = null!;
            pfSaveCanceled = 0;

            if (Service(typeof(SVsUIShell)) is not IVsUIShell shell)
                return VSConstants.E_UNEXPECTED;

            switch (dwSave)
            {
                case VSSAVEFLAGS.VSSAVE_Save:
                case VSSAVEFLAGS.VSSAVE_SilentSave:
                {
                    // Source control / read-only check, as the text editor does.
                    var result = (uint)tagVSQuerySaveResult.QSR_SaveOK;
                    if (Service(typeof(SVsQueryEditQuerySave)) is IVsQueryEditQuerySave2 qeqs)
                    {
                        var hr = qeqs.QuerySaveFile(_path, 0, null, out result);
                        if (ErrorHandler.Failed(hr))
                            return hr;
                    }

                    switch ((tagVSQuerySaveResult)result)
                    {
                        case tagVSQuerySaveResult.QSR_NoSave_Cancel:
                            pfSaveCanceled = ~0;
                            return VSConstants.S_OK;
                        case tagVSQuerySaveResult.QSR_NoSave_Continue:
                            return VSConstants.S_OK;
                        case tagVSQuerySaveResult.QSR_ForceSaveAs:
                            return shell.SaveDocDataToFile(VSSAVEFLAGS.VSSAVE_SaveAs, this, _path, out pbstrMkDocumentNew, out pfSaveCanceled);
                        default:
                            return shell.SaveDocDataToFile(dwSave, this, _path, out pbstrMkDocumentNew, out pfSaveCanceled);
                    }
                }

                case VSSAVEFLAGS.VSSAVE_SaveAs:
                case VSSAVEFLAGS.VSSAVE_SaveCopyAs:
                    // Shows the Save As dialog (filter from GetFormatList) and calls IPersistFileFormat.Save.
                    return shell.SaveDocDataToFile(dwSave, this, _path, out pbstrMkDocumentNew, out pfSaveCanceled);

                default:
                    return VSConstants.E_INVALIDARG;
            }
        }

        int IVsPersistDocData.Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CloseView();
            return VSConstants.S_OK;
        }

        int IVsPersistDocData.OnRegisterDocData(uint docCookie, IVsHierarchy pHierNew, uint itemidNew)
        {
            _docCookie = docCookie;
            return VSConstants.S_OK;
        }

        int IVsPersistDocData.RenameDocData(uint grfAttribs, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Renamed in Solution Explorer (or Save As, which already switched the view). The view learns the new path
            // on its next save, which passes it explicitly.
            if (!string.IsNullOrEmpty(pszMkDocumentNew) && !string.Equals(pszMkDocumentNew, _path, StringComparison.OrdinalIgnoreCase))
            {
                UnadviseFileChanges();
                _path = pszMkDocumentNew;
                AdviseFileChanges();
            }

            return VSConstants.S_OK;
        }

        int IVsPersistDocData.IsDocDataReloadable(out int pfReloadable)
        {
            pfReloadable = 1;
            return VSConstants.S_OK;
        }

        int IVsPersistDocData.ReloadDocData(uint grfFlags)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ReloadView();
            return VSConstants.S_OK;
        }

        // ---- IPersistFileFormat ----------------------------------------------------------------------------------------

        int IPersistFileFormat.GetClassID(out Guid pClassID)
        {
            pClassID = DesignerEditorFactory.Guid;
            return VSConstants.S_OK;
        }

        int IPersist.GetClassID(out Guid pClassID)
        {
            pClassID = DesignerEditorFactory.Guid;
            return VSConstants.S_OK;
        }

        int IPersistFileFormat.IsDirty(out int pfIsDirty)
        {
            pfIsDirty = _view?.IsDirty == true ? 1 : 0;
            return VSConstants.S_OK;
        }

        int IPersistFileFormat.InitNew(uint nFormatIndex) => VSConstants.S_OK;

        int IPersistFileFormat.Load(string pszFilename, uint grfMode, int fReadOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(pszFilename))
                return VSConstants.E_INVALIDARG;
            try
            {
                OpenView(pszFilename);
                return VSConstants.S_OK;
            }
            catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
            {
                _package.Output.Error($"Could not open {pszFilename}: {ex.Message}");
                return Marshal.GetHRForException(ex);
            }
        }

        int IPersistFileFormat.Save(string pszFilename, int fRemember, uint nFormatIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view == null)
                return VSConstants.E_UNEXPECTED;

            // A null file name means "save to the current file".
            var target = string.IsNullOrEmpty(pszFilename) ? _path : pszFilename;
            var sameFile = string.Equals(target, _path, StringComparison.OrdinalIgnoreCase);
            var remember = sameFile || fRemember != 0;
            var view = _view;

            // Our own writes are not external changes. Save Copy As may write the current file as well (see
            // ClientDesignerView.SaveAsync), so ignore both.
            var fileChange = Service(typeof(SVsFileChangeEx)) as IVsFileChangeEx;
            fileChange?.IgnoreFile(0, target, 1);
            if (!sameFile)
                fileChange?.IgnoreFile(0, _path, 1);
            try
            {
                // COM save is synchronous, but the host's answer arrives through the WPF dispatcher, which JTF.Run
                // doesn't pump (it would deadlock). Wait in a nested dispatcher frame instead.
                DispatcherWait.Wait(view.SaveAsync(target, remember, _package.DisposalToken), SaveTimeout);
            }
            catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
            {
                _package.Output.Error($"Could not save {target}: {ex.Message}");
                return Marshal.GetHRForException(ex) is var hr && ErrorHandler.Failed(hr) ? hr : VSConstants.E_FAIL;
            }
            finally
            {
                fileChange?.SyncFile(target);
                fileChange?.IgnoreFile(0, target, 0);
                if (!sameFile)
                {
                    fileChange?.SyncFile(_path);
                    fileChange?.IgnoreFile(0, _path, 0);
                }
            }

            if (remember && !sameFile)
            {
                UnadviseFileChanges();
                _path = target;
                AdviseFileChanges();
            }

            return VSConstants.S_OK;
        }

        int IPersistFileFormat.SaveCompleted(string pszFilename) => VSConstants.S_OK;

        int IPersistFileFormat.GetCurFile(out string ppszFilename, out uint pnFormatIndex)
        {
            ppszFilename = _path;
            pnFormatIndex = FormatIndex;
            return VSConstants.S_OK;
        }

        int IPersistFileFormat.GetFormatList(out string ppszFormatList)
        {
            ppszFormatList = FormatList;
            return VSConstants.S_OK;
        }

        // ---- reload on external change ---------------------------------------------------------------------------------

        private void AdviseFileChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_fileChangeCookie != 0 || string.IsNullOrEmpty(_path))
                return;
            if (Service(typeof(SVsFileChangeEx)) is IVsFileChangeEx fileChange)
            {
                fileChange.AdviseFileChange(_path,
                    (uint)(_VSFILECHANGEFLAGS.VSFILECHG_Time | _VSFILECHANGEFLAGS.VSFILECHG_Size | _VSFILECHANGEFLAGS.VSFILECHG_Del),
                    this, out _fileChangeCookie);
            }
        }

        private void UnadviseFileChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_fileChangeCookie == 0)
                return;
            if (Service(typeof(SVsFileChangeEx)) is IVsFileChangeEx fileChange)
                fileChange.UnadviseFileChange(_fileChangeCookie);
            _fileChangeCookie = 0;
        }

        int IVsFileChangeEvents.FilesChanged(uint cChanges, string[] rgpszFile, uint[] rggrfChange)
        {
            if (_ignoreFileChanges > 0 || _view == null || _reloadPending)
                return VSConstants.S_OK;

            var deleted = false;
            for (var i = 0; i < cChanges && i < rggrfChange.Length; i++)
                deleted |= (rggrfChange[i] & (uint)_VSFILECHANGEFLAGS.VSFILECHG_Del) != 0;

            // Don't show UI from inside the file-change callback; queue it.
            _reloadPending = true;
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
                _reloadPending = false;
                OnFileChangedOnDisk(deleted);
            }).FileAndForget("RadSharp/Designer/FileChanged");
            return VSConstants.S_OK;
        }

        int IVsFileChangeEvents.DirectoryChanged(string pszDirectory) => VSConstants.S_OK;

        private void OnFileChangedOnDisk(bool deleted)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view == null)
                return;

            var name = Path.GetFileName(_path);
            if (deleted || !File.Exists(_path))
            {
                _package.Output.WriteLine($"{name} was deleted or moved on disk. Save to write it again.");
                return;
            }

            if (_view.IsDirty)
            {
                var answer = VsShellUtilities.ShowMessageBox(_package,
                    $"{_path}\n\nThis file has been modified outside of Visual Studio and has unsaved changes in the designer.\n\nReload it and lose your changes?",
                    "RadSharp Form Designer", OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                if (answer != 6) // IDYES
                    return;
            }

            ReloadView();
            _package.Output.WriteLine($"Reloaded {name} (changed on disk).");
        }

        private void ReloadView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view == null)
                return;
            _view.Reload();
            if (Service(typeof(SVsFileChangeEx)) is IVsFileChangeEx fileChange)
                fileChange.SyncFile(_path);
        }

        int IVsDocDataFileChangeControl.IgnoreFileChanges(int fIgnore)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // VS brackets its own writes to the file (e.g. source-control operations) with this.
            if (fIgnore != 0)
            {
                _ignoreFileChanges++;
            }
            else if (_ignoreFileChanges > 0 && --_ignoreFileChanges == 0
                     && Service(typeof(SVsFileChangeEx)) is IVsFileChangeEx fileChange)
            {
                fileChange.SyncFile(_path);
            }

            return VSConstants.S_OK;
        }

        // ---- commands --------------------------------------------------------------------------------------------------

        private static string? MapCommand(Guid group, uint id)
        {
            if (group != s_std97)
                return null;
            return (VSConstants.VSStd97CmdID)id switch
            {
                VSConstants.VSStd97CmdID.Undo or VSConstants.VSStd97CmdID.MultiLevelUndo => DesignerCommands.Undo,
                VSConstants.VSStd97CmdID.Redo or VSConstants.VSStd97CmdID.MultiLevelRedo => DesignerCommands.Redo,
                VSConstants.VSStd97CmdID.Cut => DesignerCommands.Cut,
                VSConstants.VSStd97CmdID.Copy => DesignerCommands.Copy,
                VSConstants.VSStd97CmdID.Paste => DesignerCommands.Paste,
                VSConstants.VSStd97CmdID.Delete => DesignerCommands.Delete,
                VSConstants.VSStd97CmdID.SelectAll => DesignerCommands.SelectAll,
                _ => null,
            };
        }

        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view == null || prgCmds == null)
                return (int)Constants.OLECMDERR_E_NOTSUPPORTED;

            for (var i = 0; i < cCmds && i < prgCmds.Length; i++)
            {
                var name = MapCommand(pguidCmdGroup, prgCmds[i].cmdID);
                if (name == null)
                    return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
                prgCmds[i].cmdf = (uint)OLECMDF.OLECMDF_SUPPORTED | (_view.CanExecute(name) ? (uint)OLECMDF.OLECMDF_ENABLED : 0);
            }

            return VSConstants.S_OK;
        }

        int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var name = MapCommand(pguidCmdGroup, nCmdID);
            if (_view == null || name == null)
                return (int)Constants.OLECMDERR_E_NOTSUPPORTED;

            // Help queries on the command ("show help for this command") are not executions.
            const uint VSCmdOptQueryParameterList = 1;
            if ((nCmdexecopt >> 16) == VSCmdOptQueryParameterList)
                return VSConstants.S_OK;

            if (!_view.CanExecute(name))
                return (int)Constants.OLECMDERR_E_DISABLED;
            _view.ExecuteCommand(name);
            return VSConstants.S_OK;
        }
    }
}
