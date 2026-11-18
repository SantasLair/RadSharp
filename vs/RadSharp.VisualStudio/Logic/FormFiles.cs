using System;
using System.IO;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// Pairs a form file with its code-behind, as the WinForms designer pairs Form1.cs with its designer: X.radform.json
    /// next to X.cs. F7 goes from the form to the code, Shift+F7 (View Designer) from the code to the form. No VS SDK
    /// dependency, so it is unit-tested.
    /// </summary>
    public static class FormFiles
    {
        public const string FormExtension = ".radform.json";

        public static bool IsFormFile(string? path) =>
            path != null && path.EndsWith(FormExtension, StringComparison.OrdinalIgnoreCase);

        /// <summary>X.radform.json → X.cs in the same folder (whether or not it exists); null for any other file.</summary>
        public static string? CodeFileFor(string? formFile) =>
            IsFormFile(formFile) ? formFile!.Substring(0, formFile!.Length - FormExtension.Length) + ".cs" : null;

        /// <summary>X.cs → X.radform.json in the same folder (whether or not it exists); null for any other file. X.Designer.cs
        /// and other dotted names map by their full name, so only the class's own file pairs with the form.</summary>
        public static string? FormFileFor(string? codeFile) =>
            codeFile != null && codeFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? codeFile.Substring(0, codeFile.Length - ".cs".Length) + FormExtension
                : null;

        /// <summary>The existing code-behind of a form file, or null.</summary>
        public static string? ExistingCodeFileFor(string? formFile, Func<string, bool>? exists = null) =>
            CodeFileFor(formFile) is { } code && (exists ?? File.Exists)(code) ? code : null;

        /// <summary>The existing form file of a code file, or null.</summary>
        public static string? ExistingFormFileFor(string? codeFile, Func<string, bool>? exists = null) =>
            FormFileFor(codeFile) is { } form && (exists ?? File.Exists)(form) ? form : null;
    }
}
