using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace RadSharp.VisualStudio
{
    /// <summary>Opens a source file in VS's code editor with the caret on a line (the designer's jump-to-handler).</summary>
    internal static class CodeNavigator
    {
        /// <param name="line">1-based; clamped to the file.</param>
        public static async Task NavigateAsync(RadSharpPackage package, string file, int line)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (!File.Exists(file))
            {
                package.Output.Error($"Cannot navigate to code: {file} does not exist.");
                return;
            }

            VsShellUtilities.OpenDocument(package, file, VSConstants.LOGVIEWID.Code_guid,
                out _, out _, out IVsWindowFrame? frame, out IVsTextView? view);
            if (frame == null)
            {
                package.Output.Error($"Cannot navigate to code: VS could not open {file}.");
                return;
            }

            frame.Show();
            view ??= VsShellUtilities.GetTextView(frame);
            if (view == null)
                return;

            var target = Math.Max(0, line - 1);
            var column = 0;
            if (ErrorHandler.Succeeded(view.GetBuffer(out var buffer)) && buffer != null
                && ErrorHandler.Succeeded(buffer.GetLineCount(out var count)) && count > 0)
            {
                target = Math.Min(target, count - 1);
                // After the indentation, so typing starts inside the handler body.
                if (ErrorHandler.Succeeded(buffer.GetLengthOfLine(target, out var length))
                    && ErrorHandler.Succeeded(buffer.GetLineText(target, 0, target, length, out var text)) && text != null)
                {
                    while (column < text.Length && char.IsWhiteSpace(text[column]))
                        column++;
                }
            }

            view.SetCaretPos(target, column);
            view.CenterLines(target, 1);
            view.SendExplicitFocus();
        }
    }
}
