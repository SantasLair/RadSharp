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
        public static string? Combine(string? projectDirectory, string? outputPath, string? fileName)
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
            return IsAssembly(path) ? path : null;
        }
    }
}
