using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// Shown instead of the designer when it can't run (the design host exe is missing). Explains why and keeps the tab
    /// usable: never dirty, and saving only copies the file for Save As.
    /// </summary>
    internal sealed class PlaceholderDesignerView : IDesignerView
    {
        private readonly string _path;

        public PlaceholderDesignerView(string path, string reason)
        {
            _path = path;
            var title = new TextBlock
            {
                Text = "The RADSharp designer can't open " + Path.GetFileName(path),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            };
            var body = new TextBlock { Text = reason + "\n\nUse Open With… → Source Code (Text) Editor to edit the JSON.", TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(title);
            panel.Children.Add(body);
            title.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            body.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var root = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            Content = root;
        }

        public FrameworkElement Content { get; }

        public bool IsDirty => false;

        public event EventHandler? DirtyChanged { add { } remove { } }

        public event EventHandler<NavigateToCodeEventArgs>? NavigateToCode { add { } remove { } }

        public event EventHandler<DesignerErrorEventArgs>? Error { add { } remove { } }

        public Task SaveAsync(string path, bool remember, CancellationToken cancellationToken)
        {
            // Nothing changed in memory: the file on disk is the document.
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                File.Copy(_path, path, overwrite: true);
            return Task.CompletedTask;
        }

        public void Reload()
        {
        }

        public bool CanExecute(string command) => false;

        public void ExecuteCommand(string command)
        {
        }

        public void Activate()
        {
        }

        public void Dispose()
        {
        }
    }
}
