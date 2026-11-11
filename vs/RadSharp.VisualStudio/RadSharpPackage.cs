using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// The RADSharp Form Designer package. VS loads it (in the background) the first time it needs the editor factory,
    /// i.e. when a *.json file is opened; there is no auto-load.
    /// </summary>
    /// <remarks>
    /// Editor registration, and why these values:
    /// <list type="bullet">
    /// <item><b>ProvideEditorExtension(".json", 0x64)</b>. VS chooses editor factories by the file's last extension, so
    /// a factory can't register ".radform.json". For each opened file VS tries the factories registered for the
    /// extension in descending priority and moves on to the next when CreateEditorInstance returns
    /// VS_E_UNSUPPORTEDFORMAT. VS's own JSON editors register "json" at 0x32 (JsonEditorFactory
    /// {90a6b3a7-...}) and 0x31 (JSON Editor with Encoding) in Microsoft.WebTools.Languages.Json.VS.pkgdef, and the core
    /// text editor is the "*" fallback. 0x64 puts us first, which makes the designer the default for
    /// *.radform.json; every other .json file gets VS_E_UNSUPPORTEDFORMAT and falls through to the JSON editor
    /// unchanged. (A per-user "Open With → Set as Default" choice for .json overrides priorities, as for any editor.)</item>
    /// <item><b>ProvideEditorFactory</b> with display name #110 lists "RADSharp Form Designer" in Open With.
    /// We don't touch the other registrations, so "Source Code (Text) Editor" and the JSON editor stay in Open With
    /// for the raw JSON.</item>
    /// <item><b>ProvideEditorLogicalView(Designer)</b>. MapLogicalView accepts only Primary and Designer. Anything that
    /// asks for a Code/TextView logical view (Find Results, Go To, "View Code") gets E_NOTIMPL and falls through to the
    /// text editor, so those still show the JSON text.</item>
    /// </list>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [InstalledProductRegistration("#111", "#112", "0.1.0")]
    [ProvideEditorFactory(typeof(DesignerEditorFactory), 110, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorExtension(typeof(DesignerEditorFactory), ".json", 0x64, NameResourceID = 110)]
    [ProvideEditorLogicalView(typeof(DesignerEditorFactory), LogicalViewID.Designer)]
    // RadSharp.VisualStudio.Client and RadSharp.DesignHost.Protocol load from the extension folder.
    [ProvideBindingPath]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Tabbed with VS's own Toolbox (left) and Properties window (right), as for the WinForms designer.
    [ProvideToolWindow(typeof(ToolboxToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.Toolbox, Orientation = ToolWindowOrientation.Left)]
    [ProvideToolWindow(typeof(PropertiesToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.PropertiesWindow, Orientation = ToolWindowOrientation.Right)]
    public sealed class RadSharpPackage : AsyncPackage, IVsSelectionEvents
    {
        public const string PackageGuidString = "5d0adadb-2d8e-4660-b721-fa0512f83271";
        public const string CommandSetGuidString = "b0754257-40bf-4afa-9a12-4dbee8f9ecb4";
        public const int CmdIdShowToolbox = 0x0100;
        public const int CmdIdShowProperties = 0x0101;

        private uint _selectionCookie;
        private DesignerOutput? _output;
        private DesignHostService? _hostService;
        private bool _toolWindowsShown;

        /// <summary>Creates the content of each designer tab (the real designer, or a placeholder explaining why not).</summary>
        public IDesignerViewFactory ViewFactory { get; set; } = null!;

        /// <summary>The shared design host. Created in <see cref="InitializeAsync"/>; the host process starts on first use.</summary>
        internal DesignHostService HostService => _hostService ?? throw new InvalidOperationException("The package is not initialized.");

        /// <summary>Shows the RADSharp Toolbox and Properties windows the first time a designer opens in this VS session.</summary>
        internal void ShowToolWindowsOnce()
        {
            if (_toolWindowsShown)
                return;
            _toolWindowsShown = true;
            JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowToolWindowAsync(typeof(ToolboxToolWindow), 0, true, DisposalToken);
                await ShowToolWindowAsync(typeof(PropertiesToolWindow), 0, true, DisposalToken);
            }).FileAndForget("RadSharp/Designer/ShowToolWindows");
        }

        /// <summary>Status bar and the "RADSharp Designer" Output pane. UI thread only.</summary>
        internal DesignerOutput Output
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _output ??= new DesignerOutput(this);
            }
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _hostService = new DesignHostService(this);
            ViewFactory ??= new ClientDesignerViewFactory(this);
            RegisterEditorFactory(new DesignerEditorFactory(this));

            // View > Other Windows > RADSharp Toolbox / RADSharp Properties.
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                var set = new Guid(CommandSetGuidString);
                commands.AddCommand(new MenuCommand((s, e) => ShowToolWindow(typeof(ToolboxToolWindow)), new CommandID(set, CmdIdShowToolbox)));
                commands.AddCommand(new MenuCommand((s, e) => ShowToolWindow(typeof(PropertiesToolWindow)), new CommandID(set, CmdIdShowProperties)));
            }

            // Tell the designer tab when it becomes the active document, so the inspector and palette follow it.
            if (await GetServiceAsync(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monitor)
                monitor.AdviseSelectionEvents(this, out _selectionCookie);
        }

        private void ShowToolWindow(Type type) =>
            JoinableTaskFactory.RunAsync(() => ShowToolWindowAsync(type, 0, true, DisposalToken)).FileAndForget("RadSharp/Designer/ShowToolWindow");

        protected override void Dispose(bool disposing)
        {
            // Packages are disposed on the UI thread when VS shuts down; if not, the process is going away anyway.
            // The analyzer doesn't treat CheckAccess() as a guard; this must not throw (base.Dispose has to run), so it
            // checks rather than asserts.
#pragma warning disable VSTHRD010
            if (disposing && _selectionCookie != 0 && ThreadHelper.CheckAccess())
                UnadviseSelectionEvents();
#pragma warning restore VSTHRD010
            if (disposing)
            {
                _hostService?.Dispose();
                _hostService = null;
            }

            base.Dispose(disposing);
        }

        private void UnadviseSelectionEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monitor)
                monitor.UnadviseSelectionEvents(_selectionCookie);
            _selectionCookie = 0;
        }

        int IVsSelectionEvents.OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid != (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame)
                return VSConstants.S_OK;

            // The active document changed (focus moving into a tool window doesn't change it).
            DesignerPane? pane = null;
            if (varValueNew is IVsWindowFrame frame
                && ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView)))
            {
                pane = docView as DesignerPane;
            }

            _hostService?.SetActiveDesigner(pane?.View as ClientDesignerView);
            pane?.OnActivated();
            return VSConstants.S_OK;
        }

        int IVsSelectionEvents.OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
            IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

        int IVsSelectionEvents.OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;
    }
}
