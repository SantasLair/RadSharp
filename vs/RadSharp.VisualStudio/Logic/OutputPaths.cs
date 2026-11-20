using System;
using System.IO;

namespace RadSharp.VisualStudio
{
    /// <summary>Making sense of what a project system says about its build output (no Visual Studio types: unit-tested).</summary>
    public static class OutputPaths
    {
        /// <summary>A full path to an assembly file (.dll or .exe), as MSBuild's <c>TargetPath</c> is.</summary>
        public static bool IsAssembly(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)
            && (path!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The assembly from a project folder, an output folder (relative to it, or full) and a file name; null when a
        /// part is missing.
        /// </summary>
        public static string? Combine(string? projectDirectory, string? outputPath, string? fileName, Func<string, bool>? exists = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(fileName))
                return null;
            if (!Path.IsPathRooted(outputPath))
            {
                if (string.IsNullOrWhiteSpace(projectDirectory))
                    return null;
                outputPath = Path.Combine(projectDirectory, outputPath);
            }
            var path = Path.GetFullPath(Path.Combine(outputPath, fileName));
            return IsAssembly(path) ? PreferManaged(path, exists) : null;
        }

        /// <summary>
        /// The assembly to give the host after a project lookup: a failed lookup (project system busy or threw) keeps
        /// <paramref name="previous"/>; only a definite "no project" (null, not failed) clears it.
        /// </summary>
        public static string? AfterLookup(string? previous, string? found, bool lookupFailed) => lookupFailed ? previous : found;

        /// <summary>
        /// An <c>.exe</c> is usually the native apphost; the managed assembly the designer can load is the <c>.dll</c> with
        /// the same name next to it. Returns that dll when it exists, otherwise <paramref name="path"/>.
        /// </summary>
        public static string PreferManaged(string path, Func<string, bool>? exists = null)
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return path;
            var dll = path.Substring(0, path.Length - 4) + ".dll";
            return (exists ?? File.Exists)(dll) ? dll : path;
        }
    }
}
