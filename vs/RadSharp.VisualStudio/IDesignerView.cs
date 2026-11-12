using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// The content of one designer document tab. The VSIX owns the VS side (document pane, persistence, commands,
    /// navigation, error reporting); an <see cref="IDesignerView"/> owns what is shown inside the tab.
    /// </summary>
    /// <remarks>
    /// Threading: every member is called on the VS UI thread, and every event must be raised on the UI thread.
    /// An implementation that receives host messages on another thread marshals them before raising events.
    /// </remarks>
    public interface IDesignerView : IDisposable
    {
        /// <summary>The WPF element shown in the document tab. Created once and kept for the view's lifetime.</summary>
        FrameworkElement Content { get; }

        /// <summary>True while the document has changes that are not saved.</summary>
        bool IsDirty { get; }

        /// <summary>Raised when <see cref="IsDirty"/> changes (drives the tab's asterisk).</summary>
        event EventHandler? DirtyChanged;

        /// <summary>
        /// Writes the document to <paramref name="path"/>. Same path as opened for Save; a different path for
        /// Save As (the view then works on the new path) and Save Copy As (see <paramref name="remember"/>).
        /// Throws on failure; the message is shown to the user and the save is reported as failed.
        /// </summary>
        /// <param name="remember">False for Save Copy As: write the file, but keep the current path and dirty state.</param>
        Task SaveAsync(string path, bool remember, CancellationToken cancellationToken);

        /// <summary>The file changed on disk and the user chose to reload it: discard in-memory state and re-read.</summary>
        void Reload();

        /// <summary>Whether an Edit command is available now. Names are those in <see cref="DesignerCommands"/>.</summary>
        bool CanExecute(string command);

        /// <summary>Runs an Edit command. Names are those in <see cref="DesignerCommands"/>.</summary>
        void ExecuteCommand(string command);

        /// <summary>The designer wants VS's code editor opened at a file and 1-based line (event handler jump).</summary>
        event EventHandler<NavigateToCodeEventArgs>? NavigateToCode;

        /// <summary>Something went wrong; shown in the VS status bar and the "RadSharp Designer" Output pane.</summary>
        event EventHandler<DesignerErrorEventArgs>? Error;

        /// <summary>The tab became the active document (the inspector and palette should follow it).</summary>
        void Activate();
    }

    /// <summary>Creates the view for a document. The pane calls this once per opened tab.</summary>
    public interface IDesignerViewFactory
    {
        /// <param name="path">Full path of the *.radform.json file being opened.</param>
        IDesignerView Create(string path);
    }

    /// <summary>Edit-command names passed to <see cref="IDesignerView"/>; they match the protocol's Command names.</summary>
    public static class DesignerCommands
    {
        public const string Undo = "Undo";
        public const string Redo = "Redo";
        public const string Cut = "Cut";
        public const string Copy = "Copy";
        public const string Paste = "Paste";
        public const string Delete = "Delete";
        public const string SelectAll = "SelectAll";
    }

    public sealed class NavigateToCodeEventArgs : EventArgs
    {
        public NavigateToCodeEventArgs(string file, int line)
        {
            File = file;
            Line = line;
        }

        /// <summary>Full path of the source file.</summary>
        public string File { get; }

        /// <summary>1-based line number.</summary>
        public int Line { get; }
    }

    public sealed class DesignerErrorEventArgs : EventArgs
    {
        public DesignerErrorEventArgs(string text) => Text = text;

        public string Text { get; }
    }
}
