using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace RadSharp.Design.Code;

/// <summary>What a new file is made from.</summary>
public enum FileTemplate { Class, Interface, Form }

/// <summary>A file that moved or was renamed (the IDE retargets an open tab from <see cref="OldPath"/> to <see cref="NewPath"/>).</summary>
public sealed record FileChange(string OldPath, string NewPath);

/// <summary>
/// An operation failed half-way and could not undo what it had done: <see cref="Completed"/> are the changes that did
/// happen (the IDE must still retarget open tabs for them).
/// </summary>
public sealed class PartialOperationException(string message, IReadOnlyList<FileChange> completed, Exception? inner = null) : IOException(message, inner)
{
    public IReadOnlyList<FileChange> Completed { get; } = completed;
}

/// <summary>
/// File operations on a <see cref="RadProject"/> that keep the code consistent: new files from templates (with the
/// namespace the folder implies), folders, rename, move and delete. A form's <c>.radform.json</c> and its code-behind
/// always travel together. Everything here is textual (class names are renamed with
/// <see cref="CodeIntegration.RenameIdentifier"/> across the project's sources, namespaces with a regex on the
/// declaration); there is no semantic refactoring, so a <c>using</c> in another file that names a moved namespace
/// is not rewritten.
/// </summary>
public sealed class ProjectFileOps(RadProject project)
{
    public RadProject Project { get; } = project;

    /// <summary>Called with the paths of source files the operation rewrote (so the IDE can refresh open buffers).</summary>
    public Action<string>? SourceRewritten { get; set; }

    // ---- Names ----

    /// <summary>The namespace the folder implies: the root namespace plus the sanitised folder names below the project folder.</summary>
    public string NamespaceFor(string directory)
    {
        var rel = Path.GetRelativePath(Project.Directory, Path.GetFullPath(directory));
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return Project.RootNamespace;
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Select(Sanitize);
        return Project.RootNamespace + "." + string.Join('.', parts);
    }

    static string Sanitize(string name)
    {
        var s = new string([.. name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
        return s.Length == 0 || char.IsDigit(s[0]) ? "_" + s : s;
    }

    public static bool IsValidIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_')
        && !IsKeyword(name);   // 'class', 'int'... can't name a type

    static bool IsKeyword(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None;

    // ---- Guards ----

    /// <summary>Throws unless <paramref name="path"/> lies strictly inside the project folder (the root itself and anything outside are refused).</summary>
    void EnsureInsideProject(string path, bool allowRoot = false)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Project.Directory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full, root, cmp))
        {
            if (allowRoot) return;
            throw new IOException("The project folder itself can't be renamed, moved or deleted here.");
        }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, cmp))
            throw new IOException($"{Path.GetFileName(full)} is outside the project folder.");
    }

    static bool CaseOnlyDifference(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) && !string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Moves a file; a case-only rename goes through a temporary name (a case-insensitive file system sees source and target as one file).</summary>
    static void MoveFile(string from, string to)
    {
        if (CaseOnlyDifference(from, to))
        {
            var temp = from + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.Move(from, temp);
            try { File.Move(temp, to); }
            catch { File.Move(temp, from); throw; }
        }
        else File.Move(from, to);
    }

    static void MoveDirectory(string from, string to)
    {
        if (CaseOnlyDifference(from, to))
        {
            var temp = from + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            Directory.Move(from, temp);
            try { Directory.Move(temp, to); }
            catch { Directory.Move(temp, from); throw; }
        }
        else Directory.Move(from, to);
    }

    /// <summary>True if <paramref name="target"/> exists, other than as the case-only twin of the source being renamed.</summary>
    static bool Occupied(string target, string? exceptSource = null) =>
        (File.Exists(target) || Directory.Exists(target)) && !(exceptSource != null && CaseOnlyDifference(exceptSource, target));

    static readonly Regex NamespaceDeclaration = new(@"(?m)^(?<pre>[ \t]*namespace[ \t]+)(?<name>[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);

    /// <summary>The first namespace a source declares, or null.</summary>
    public static string? DeclaredNamespace(string source) =>
        NamespaceDeclaration.Match(source) is { Success: true } m ? m.Groups["name"].Value : null;

    /// <summary>Rewrites the first namespace declaration (file-scoped or block); the source unchanged if there is none.</summary>
    public static string RewriteNamespace(string source, string newNamespace) =>
        NamespaceDeclaration.Replace(source, m => m.Groups["pre"].Value + newNamespace, 1);

    // ---- Creating ----

    /// <summary>Creates a folder (and returns its full path).</summary>
    public string CreateFolder(string parentDirectory, string name)
    {
        CheckName(name, allowDots: true);
        var path = Path.Combine(parentDirectory, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException($"{name} already exists.");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Creates a file from a template in <paramref name="directory"/>, with the namespace the folder implies, and
    /// returns its path: the <c>.cs</c> for a class or interface, the <c>.radform.json</c> for a form (its
    /// code-behind is created too).
    /// </summary>
    public string NewFile(string directory, string name, FileTemplate template)
    {
        if (!IsValidIdentifier(name))
            throw new IOException(IsKeyword(name) ? $"'{name}' is a C# keyword and can't be used as a type name." : $"'{name}' is not a valid C# type name.");
        EnsureInsideProject(directory, allowRoot: true);
        Directory.CreateDirectory(directory);
        var ns = NamespaceFor(directory);
        if (template == FileTemplate.Form)
        {
            var formFile = Path.Combine(directory, name + ".radform.json");
            if (File.Exists(formFile) || File.Exists(RadProject.CodeFileFor(formFile))) throw new IOException($"{name} already exists.");
            var form = new Form { Name = "frm" + (name.EndsWith("Form", StringComparison.Ordinal) && name.Length > 4 ? name[..^4] : name), Caption = name };
            form.SetBounds(0, 0, 480, 320);
            form.DesignWidth = 480;
            form.DesignHeight = 320;
            File.WriteAllText(formFile, FormWriter.Save(form));
            File.WriteAllText(RadProject.CodeFileFor(formFile), $$"""
                using RadSharp;

                namespace {{ns}};

                public partial class {{name}} : Form
                {
                    public {{name}}() => InitializeComponent();
                }

                """);
            return formFile;
        }
        var file = Path.Combine(directory, name + ".cs");
        if (File.Exists(file)) throw new IOException($"{name}.cs already exists.");
        var keyword = template == FileTemplate.Interface ? "interface" : "class";
        File.WriteAllText(file, $$"""
            namespace {{ns}};

            public {{keyword}} {{name}}
            {
            }

            """);
        return file;
    }

    static void CheckName(string name, bool allowDots)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".."
            || (!allowDots && name.Contains('.')))
            throw new IOException($"'{name}' is not a valid name.");
    }

    // ---- Form pairs ----

    /// <summary>For a form file or its code-behind: the (form, code) pair. Null for any other file.</summary>
    public (string Form, string Code)? PairOf(string file)
    {
        if (file.EndsWith(".radform.json", StringComparison.OrdinalIgnoreCase)) return (file, RadProject.CodeFileFor(file));
        if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var form = Path.ChangeExtension(file, ".radform.json");
            if (File.Exists(form)) return (form, file);
        }
        return null;
    }

    // ---- Rename ----

    /// <summary>
    /// Renames a file, a form (both its files, and the class in every source) or a folder. For a file or form,
    /// <paramref name="newName"/> is the new name without extension. Returns every file that moved.
    /// </summary>
    public IReadOnlyList<FileChange> Rename(string path, string newName)
    {
        path = Path.GetFullPath(path);
        EnsureInsideProject(path);
        if (Directory.Exists(path)) return RenameFolder(path, newName);
        CheckName(newName, allowDots: PairOf(path) == null);
        var changes = new List<FileChange>();
        if (PairOf(path) is { } pair)
        {
            var (formFile, codeFile) = pair;
            if (!IsValidIdentifier(newName)) throw new IOException($"'{newName}' is not a valid class name.");
            var dir = Path.GetDirectoryName(formFile)!;
            var newForm = Path.Combine(dir, newName + ".radform.json");
            var newCode = RadProject.CodeFileFor(newForm);
            if (Occupied(newForm, formFile) || Occupied(newCode, codeFile)) throw new IOException($"{newName} already exists.");
            var oldName = RadProject.FormClassName(formFile);
            MoveFile(formFile, newForm);
            changes.Add(new(formFile, newForm));
            if (File.Exists(codeFile))
            {
                try { MoveFile(codeFile, newCode); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Transactional: put the form file back; if that fails too, say what did move.
                    try { MoveFile(newForm, formFile); }
                    catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                    {
                        throw new PartialOperationException($"Renaming {Path.GetFileName(codeFile)} failed ({e.Message}) and {Path.GetFileName(newForm)} could not be moved back.", [.. changes], e);
                    }
                    throw;
                }
                changes.Add(new(codeFile, newCode));
            }
            RenameIdentifiersOrReport(oldName, newName, changes);
            return changes;
        }
        var ext = Path.GetExtension(path);
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName + ext);
        if (Occupied(target, path)) throw new IOException($"{Path.GetFileName(target)} already exists.");
        var typeName = Path.GetFileNameWithoutExtension(path);
        bool declaresType = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) && IsValidIdentifier(newName)
            && Regex.IsMatch(TextFileContent.Read(path).Text, $@"\b(class|interface|struct|record|enum)\s+{Regex.Escape(typeName)}\b");
        MoveFile(path, target);
        changes.Add(new(path, target));
        if (declaresType) RenameIdentifiersOrReport(typeName, newName, changes);
        return changes;
    }

    IReadOnlyList<FileChange> RenameFolder(string folder, string newName)
    {
        CheckName(newName, allowDots: true);
        var target = Path.Combine(Path.GetDirectoryName(folder)!, newName);
        if (Occupied(target, folder)) throw new IOException($"{newName} already exists.");
        var before = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        MoveDirectory(folder, target);
        return [.. before.Select(f => new FileChange(f, Path.Combine(target, Path.GetRelativePath(folder, f))))];
    }

    /// <summary>Renames a class in the sources after files moved; a failure then can't be undone, so it is reported with what did move.</summary>
    void RenameIdentifiersOrReport(string oldName, string newName, IReadOnlyList<FileChange> changes)
    {
        try { RenameIdentifierInProject(oldName, newName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PartialOperationException($"The files were renamed, but the class name could not be updated everywhere: {e.Message}", [.. changes], e);
        }
    }

    void RenameIdentifierInProject(string oldName, string newName)
    {
        foreach (var file in Project.SourceFiles)
        {
            var content = TextFileContent.Read(file);
            if (content.IsLossy) continue;   // not UTF-8: rewriting it would corrupt it
            var updated = CodeIntegration.RenameIdentifier(content.Text, oldName, newName);
            if (updated == content.Text) continue;
            content.Write(file, updated);
            SourceRewritten?.Invoke(file);
        }
    }

    // ---- Move ----

    /// <summary>
    /// Moves a file, a form (with its code-behind) or a folder into <paramref name="targetDirectory"/>. With
    /// <paramref name="fixNamespace"/>, the namespace declaration of each moved <c>.cs</c> is rewritten to the
    /// one the new folder implies (a plain textual rewrite). Returns every file that moved.
    /// </summary>
    public IReadOnlyList<FileChange> Move(string path, string targetDirectory, bool fixNamespace)
    {
        path = Path.GetFullPath(path);
        targetDirectory = Path.GetFullPath(targetDirectory);
        EnsureInsideProject(path);
        EnsureInsideProject(targetDirectory, allowRoot: true);
        var changes = new List<FileChange>();
        if (Directory.Exists(path))
        {
            var target = Path.Combine(targetDirectory, Path.GetFileName(path));
            if (SameDir(target, path)) return changes;
            if (targetDirectory.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || SameDir(targetDirectory, path))
                throw new IOException("A folder can't be moved into itself.");
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"{Path.GetFileName(path)} already exists in the target folder.");
            var before = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            MoveDirectory(path, target);
            foreach (var f in before) changes.Add(new(f, Path.Combine(target, Path.GetRelativePath(path, f))));
        }
        else
        {
            var files = PairOf(path) is { } pair ? new[] { pair.Form, pair.Code }.Where(File.Exists).ToArray() : [path];
            foreach (var f in files)
            {
                var target = Path.Combine(targetDirectory, Path.GetFileName(f));
                if (SameDir(Path.GetDirectoryName(f)!, targetDirectory)) return changes;
                if (File.Exists(target)) throw new IOException($"{Path.GetFileName(f)} already exists in the target folder.");
            }
            foreach (var f in files)
            {
                var target = Path.Combine(targetDirectory, Path.GetFileName(f));
                try { File.Move(f, target); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Transactional: put back what already moved; if that fails, report what did move.
                    var stuck = new List<FileChange>();
                    for (int i = changes.Count - 1; i >= 0; i--)
                    {
                        try { File.Move(changes[i].NewPath, changes[i].OldPath); }
                        catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { stuck.Add(changes[i]); }
                    }
                    if (stuck.Count > 0) throw new PartialOperationException($"Moving {Path.GetFileName(f)} failed ({e.Message}) and the earlier move could not be undone.", stuck, e);
                    throw;
                }
                changes.Add(new(f, target));
            }
        }
        if (fixNamespace)
        {
            try { FixNamespaces(changes.Select(c => c.NewPath)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new PartialOperationException($"The files were moved, but a namespace could not be updated: {e.Message}", [.. changes], e);
            }
        }
        return changes;
    }

    static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if a <c>.cs</c> file declares a namespace other than the one its folder implies.</summary>
    public bool NamespaceMismatch(string csFile) =>
        csFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(csFile)
        && DeclaredNamespace(File.ReadAllText(csFile)) is { } declared && declared != NamespaceFor(Path.GetDirectoryName(csFile)!);

    /// <summary>True if moving <paramref name="path"/> (a file, a form's pair or a folder) into <paramref name="targetDirectory"/>
    /// would leave a <c>.cs</c> declaring a namespace other than the one the new folder implies (the "fix the namespace?" offer).</summary>
    public bool WouldChangeNamespace(string path, string targetDirectory)
    {
        path = Path.GetFullPath(path);
        var ns = NamespaceFor(Path.Combine(targetDirectory, Directory.Exists(path) ? Path.GetFileName(path) : ""));
        IEnumerable<string> files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
            : PairOf(path) is { } pair ? [pair.Code] : [path];
        foreach (var f in files)
        {
            if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(f)) continue;
            // A moved folder keeps its sub-folders: the namespace each file will get depends on its place below the folder.
            var sub = Directory.Exists(path) ? Path.GetRelativePath(path, Path.GetDirectoryName(f)!) : ".";
            var expected = sub == "." ? ns : NamespaceFor(Path.Combine(targetDirectory, Path.GetFileName(path), sub));
            if (DeclaredNamespace(File.ReadAllText(f)) is { } declared && declared != expected) return true;
        }
        return false;
    }

    /// <summary>Rewrites the namespace of each given <c>.cs</c> file to its folder's; returns the files changed.</summary>
    public IReadOnlyList<string> FixNamespaces(IEnumerable<string> files)
    {
        var changed = new List<string>();
        foreach (var f in files)
        {
            if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(f)) continue;
            var content = TextFileContent.Read(f);
            if (content.IsLossy) continue;   // not UTF-8: rewriting it would corrupt it
            var updated = RewriteNamespace(content.Text, NamespaceFor(Path.GetDirectoryName(f)!));
            if (updated == content.Text) continue;
            content.Write(f, updated);
            SourceRewritten?.Invoke(f);
            changed.Add(f);
        }
        return changed;
    }

    // ---- Delete ----

    /// <summary>Deletes a file (a form with its code-behind) or a folder; returns the files removed.</summary>
    public IReadOnlyList<string> Delete(string path)
    {
        path = Path.GetFullPath(path);
        EnsureInsideProject(path);
        if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            Directory.Delete(path, recursive: true);
            return files;
        }
        var targets = PairOf(path) is { } pair ? new[] { pair.Form, pair.Code } : [path];
        var removed = new List<string>();
        foreach (var f in targets)
            if (File.Exists(f)) { File.Delete(f); removed.Add(f); }
        return removed;
    }
}
