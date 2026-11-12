using System;
using System.IO;

namespace RadSharp.VisualStudio
{
    /// <summary>Finds RadSharpDesignHost.exe: the RADSHARP_DESIGNHOST override, else host\ inside the extension folder.</summary>
    public static class DesignHostLocator
    {
        public const string EnvironmentVariable = "RADSHARP_DESIGNHOST";
        public const string HostFileName = "RadSharpDesignHost.exe";
        public const string HostFolder = "host";

        /// <summary>
        /// The host path to start. RADSHARP_DESIGNHOST wins when set (a path to the exe, or to a folder holding it), so a
        /// developer can point VS at a freshly built host without repackaging. The file may not exist; check before use.
        /// </summary>
        public static string GetHostPath()
        {
            var overridePath = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var p = Environment.ExpandEnvironmentVariables(overridePath!.Trim().Trim('"'));
                return Directory.Exists(p) ? Path.Combine(p, HostFileName) : p;
            }

            return Path.Combine(ExtensionDirectory, HostFolder, HostFileName);
        }

        /// <summary>True if the host exe exists; <paramref name="path"/> is the location that was checked.</summary>
        public static bool TryGetHostPath(out string path)
        {
            path = GetHostPath();
            return File.Exists(path);
        }

        /// <summary>The folder this extension is installed in (where RadSharp.VisualStudio.dll lives).</summary>
        public static string ExtensionDirectory =>
            Path.GetDirectoryName(typeof(DesignHostLocator).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
    }
}
