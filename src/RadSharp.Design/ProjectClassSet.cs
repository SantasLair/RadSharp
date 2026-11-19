using System.Reflection;
using RadSharp.Drawing;

namespace RadSharp.Design;

/// <summary>
/// The control classes a project brings into a designer, its own and its component libraries': the ones a designed
/// form may create (<see cref="DesignDocument.ProjectClasses"/> is <see cref="Find"/>) and the palette lists. Both
/// designers build one: the standalone designer from the project it compiles itself (<c>DesignSession</c>), the Visual
/// Studio design host from the assembly Visual Studio built (<see cref="ProjectAssembly"/>), so the two offer the same
/// classes, under the same names and palette categories.
/// </summary>
public sealed class ProjectClassSet
{
    readonly Dictionary<string, ComponentClass> _byName = new(StringComparer.Ordinal);
    readonly Dictionary<ComponentClass, string> _categories = [];
    readonly List<ComponentClass> _classes = [];

    ProjectClassSet() { }

    /// <summary>No classes.</summary>
    public static ProjectClassSet Empty { get; } = new();

    /// <summary>The usable classes: the project's own first, then its libraries', by name.</summary>
    public IReadOnlyList<ComponentClass> Classes => _classes;

    /// <summary>The usable class named <paramref name="name"/>, or null: for <see cref="DesignDocument.ProjectClasses"/>.</summary>
    public ComponentClass? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The palette category of one of <see cref="Classes"/>: <see cref="PaletteCategories.Project"/>, or the library's assembly name.</summary>
    public string CategoryOf(ComponentClass cls) => _categories.GetValueOrDefault(cls, "Libraries");

    /// <summary>
    /// What a palette lists: <paramref name="builtIn"/> (RadSharp's own classes) without those a project class takes the
    /// name of, as in the running application, then <see cref="Classes"/>.
    /// </summary>
    public List<ComponentClass> PaletteClasses(IEnumerable<ComponentClass> builtIn) =>
        [.. builtIn.Where(c => Find(c.Name) == null), .. _classes];

    /// <summary>The palette category of a class <see cref="PaletteClasses"/> returned.</summary>
    public string PaletteCategory(ComponentClass cls) => Find(cls.Name) == cls ? CategoryOf(cls) : PaletteCategories.Of(cls.Name);

    /// <summary>
    /// The control classes <paramref name="assemblies"/> register (the metadata generator's module initializer, which
    /// this runs), that a form file can create, forms left out: a designer designs a project's forms from their files
    /// and never runs their constructors. By name.
    /// </summary>
    public static IReadOnlyList<ComponentClass> Registered(IEnumerable<Assembly> assemblies)
    {
        var set = assemblies.ToHashSet();
        foreach (var a in set) System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(a.ManifestModule.ModuleHandle);
        return [.. ComponentRegistry.All
            .Where(c => set.Contains(c.Type.Assembly) && c.CanCreate && !typeof(Form).IsAssignableFrom(c.Type))
            .OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// On the UI thread, once the classes are registered: runs <paramref name="own"/>'s
    /// <see cref="DesignTimeSetupAttribute"/> method, if it has one, then creates one of each class (its prototype, in
    /// design mode). A class that throws there, or that has the name of an earlier one (form files can't tell them
    /// apart), isn't offered; what went wrong goes to <paramref name="problems"/>, one message per problem.
    /// <see cref="Theme.Current"/> is kept: the project's code may set its own.
    /// </summary>
    public static ProjectClassSet Activate(Assembly own, IEnumerable<ComponentClass> found, List<string> problems, string? skewHint = null)
    {
        var set = new ProjectClassSet();
        var theme = Theme.Current;
        try
        {
            RunSetup(own, problems, skewHint);
            foreach (var cls in found.OrderBy(c => c.Type.Assembly == own ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                if (set._byName.ContainsKey(cls.Name))
                {
                    problems.Add($"{cls.Name} ({cls.Type.Assembly.GetName().Name}) has the name of another of the project's controls; " +
                        "form files can't tell them apart, so the designer uses the first.");
                    continue;
                }
                try
                {
                    using (Control.EnterDesignMode()) _ = cls.Prototype;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    problems.Add($"{cls.Name} can't be created at design time ({Describe(e, skewHint)}); it stays a placeholder.");
                    continue;
                }
                set._byName[cls.Name] = cls;
                set._classes.Add(cls);
                set._categories[cls] = cls.Type.Assembly == own ? PaletteCategories.Project : cls.Type.Assembly.GetName().Name ?? "Libraries";
            }
        }
        finally
        {
            Theme.Current = theme;
        }
        return set;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Design time only: designers aren't trimmed.")]
    static void RunSetup(Assembly own, List<string> problems, string? skewHint)
    {
        DesignTimeSetupAttribute? setup;
        try { setup = own.GetCustomAttribute<DesignTimeSetupAttribute>(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problems.Add($"DesignTimeSetup couldn't be read ({Describe(e, skewHint)}).");
            return;
        }
        if (setup == null) return;
        var method = setup.Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == setup.Method && m.GetParameters().All(p => p.IsOptional))
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault();
        if (method == null)
        {
            problems.Add($"DesignTimeSetup: {setup.Type.FullName} has no public static {setup.Method}() to call.");
            return;
        }
        try
        {
            using (Control.EnterDesignMode()) method.Invoke(null, [.. method.GetParameters().Select(_ => Type.Missing)]);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problems.Add($"DesignTimeSetup: {setup.Type.Name}.{setup.Method} threw {Describe(e, skewHint)}; the controls may not look right.");
        }
    }

    internal static string Describe(Exception e, string? skewHint = null)
    {
        var inner = e is TargetInvocationException or TypeInitializationException && e.InnerException != null ? e.InnerException : e;
        var text = $"{inner.GetType().Name}: {inner.Message}";
        return skewHint != null && IsMissingMember(inner) ? text + ". " + skewHint : text;
    }

    /// <summary>A member or type that isn't there: what a project built against another RadSharp.Core version runs into.</summary>
    internal static bool IsMissingMember(Exception e) => e is MissingMemberException or TypeLoadException;
}
