using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using RadSharp.DesignHost.Protocol;
using RadSharp.VisualStudio.Client;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// A tool window showing one host pane (Palette or Inspector) of the active designer document, like the WinForms
    /// Toolbox and Properties windows. One <see cref="RemoteWindowView"/> re-attaches whenever the active designer (or its
    /// panes, after a host restart) changes. Keys go to the host, including Ctrl+C/V/X/A/Z and Delete, so text editing in
    /// the inspector works; only VS's global shortcuts stay with VS.
    /// </summary>
    public abstract class RadSharpToolWindow : ToolWindowPane
    {
        private readonly RemoteWindowView _view = new RemoteWindowView();
        private readonly TextBlock _message = new TextBlock
        {
            Text = "No RadSharp form is active.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(12),
        };
        private readonly Grid _root = new Grid();
        private DesignHostService? _service;

        protected RadSharpToolWindow(string caption)
            : base(null)
        {
            Caption = caption;
            _view.ShouldForwardKey = e =>
                KeyRouting.Classify(InputMapping.RealKey(e), Keyboard.Modifiers, KeyContext.ToolWindow) == KeyRoute.Host;
            _view.SetResourceReference(RemoteWindowView.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _message.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _root.Children.Add(_view);
            _root.Children.Add(_message);
            Content = _root;
            Refresh();
        }

        /// <summary>The host pane this window shows.</summary>
        protected abstract WindowRole Role { get; }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            if (Package is RadSharpPackage package)
            {
                _service = package.HostService;
                _service.ActiveDesignerChanged += OnActiveDesignerChanged;
            }

            Refresh();
        }

        protected override bool PreProcessMessage(ref System.Windows.Forms.Message m) =>
            KeyClaim.PreProcess(ref m, _root, KeyContext.ToolWindow) || base.PreProcessMessage(ref m);

        protected override void Dispose(bool disposing)
        {
            if (disposing && _service != null)
            {
                _service.ActiveDesignerChanged -= OnActiveDesignerChanged;
                _service = null;
                _view.Detach();
            }

            base.Dispose(disposing);
        }

        private void OnActiveDesignerChanged(object? sender, EventArgs e) => Refresh();

        private void Refresh()
        {
            var doc = _service?.ActiveDesigner?.Document;
            var window = doc != null && !doc.IsClosed && doc.Session.IsConnected ? doc.GetWindow(Role) : null;
            if (doc != null && window != null)
            {
                if (_view.WindowId != window.Id || !ReferenceEquals(_view.Session, doc.Session))
                    _view.Attach(doc.Session, window.Id);
                _view.Visibility = Visibility.Visible;
                _message.Visibility = Visibility.Collapsed;
                return;
            }

            _view.Detach();
            _view.Visibility = Visibility.Collapsed;
            _message.Visibility = Visibility.Visible;
            _message.Text = _service?.ActiveDesigner == null
                ? "No RadSharp form is active."
                : _service.DownReason != null ? "The RadSharp design host is not running." : "Loading…";
        }
    }

    /// <summary>View → Other Windows → RadSharp Toolbox: the component palette of the active form.</summary>
    [Guid(GuidString)]
    public sealed class ToolboxToolWindow : RadSharpToolWindow
    {
        public const string GuidString = "42d7b6d4-d391-4a96-829c-97a567cb223b";

        public ToolboxToolWindow()
            : base("RadSharp Toolbox")
        {
            BitmapImageMoniker = KnownMonikers.ToolBox;
        }

        protected override WindowRole Role => WindowRole.Palette;
    }

    /// <summary>View → Other Windows → RadSharp Properties: the Object Inspector of the active form.</summary>
    [Guid(GuidString)]
    public sealed class PropertiesToolWindow : RadSharpToolWindow
    {
        public const string GuidString = "d3a72c8a-8cf6-41fc-abc3-4d139b01e6bd";

        public PropertiesToolWindow()
            : base("RadSharp Properties")
        {
            BitmapImageMoniker = KnownMonikers.Property;
        }

        protected override WindowRole Role => WindowRole.Inspector;
    }
}
