using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// Where the project a form file belongs to puts its built assembly (its <c>TargetPath</c> in the active
    /// configuration), so the design host can load the project's own controls. Null if the file is in no project, or the
    /// project system can't say.
    /// </summary>
    internal static class ProjectOutput
    {
        public static string? For(IServiceProvider services, string formFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(services.GetService(typeof(SVsUIShellOpenDocument)) is IVsUIShellOpenDocument open))
                    return null;
                if (ErrorHandler.Failed(open.IsDocumentInAProject(formFile, out var hierarchy, out _, out _, out int found))
                    || found == 0 || !(hierarchy is IVsHierarchy project))
                    return null;
                return FromBuildProperties(project) ?? FromAutomation(project);
            }
            catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
            {
                return null;
            }
        }

        /// <summary>MSBuild's own answer: <c>TargetPath</c> evaluated for the active configuration.</summary>
        private static string? FromBuildProperties(IVsHierarchy project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(project is IVsBuildPropertyStorage storage))
                return null;
            var config = ActiveConfiguration(project);
            foreach (var name in new[] { config, null })
            {
                if (ErrorHandler.Succeeded(storage.GetPropertyValue("TargetPath", name, (uint)_PersistStorageType.PST_PROJECT_FILE, out var value))
                    && OutputPaths.IsAssembly(value))
                    return value;
            }
            return null;
        }

        /// <summary>The DTE project's output folder and file name, for project systems without build properties.</summary>
        private static string? FromAutomation(IVsHierarchy project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Automation(project) is EnvDTE.Project dte))
                return null;
            var outputPath = dte.ConfigurationManager?.ActiveConfiguration?.Properties?.Item("OutputPath")?.Value as string;
            var fileName = dte.Properties?.Item("OutputFileName")?.Value as string;
            var dir = Path.GetDirectoryName(dte.FullName);
            return OutputPaths.Combine(dir, outputPath, fileName);
        }

        /// <summary>The active configuration as MSBuild names it ("Debug|AnyCPU"), or null.</summary>
        private static string? ActiveConfiguration(IVsHierarchy project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var config = (Automation(project) as EnvDTE.Project)?.ConfigurationManager?.ActiveConfiguration;
            if (config == null)
                return null;
            var platform = config.PlatformName == "Any CPU" ? "AnyCPU" : config.PlatformName;
            return config.ConfigurationName + "|" + platform;
        }

        private static object? Automation(IVsHierarchy project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ErrorHandler.Succeeded(project.GetProperty((uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_ExtObject, out var o)) ? o : null;
        }
    }
}
