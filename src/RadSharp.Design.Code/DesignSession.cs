namespace RadSharp.Design.Code;

/// <summary>
/// The open project's own controls, and those of the libraries it uses (NuGet packages, project references), loaded
/// into the designer for its palette and design surface: Delphi's installed design-time package, built from the
/// project. The project is compiled like a live run (<see cref="LiveSession"/>) into a collectible load context of
/// its own, separate from F9's (closing the running form disposes that one), and replaced whenever the project
/// changes: <see cref="Dispose"/> unregisters the classes and unloads the context.
/// </summary>
/// <remarks>
/// <para><see cref="Compile"/> on any thread (it blocks: MSBuild may restore the project first); <see cref="Activate"/>
/// and everything after it on the UI thread, since that is where the project's code starts to run.</para>
/// <para>Design-time code: the designer creates the controls with <see cref="Control.DesignMode"/> set (from the
/// constructor on), and runs their constructors, published property setters, layout and painting; never their event
/// handlers, and their timers don't tick. A class whose constructor throws stays a placeholder.</para>
/// <para>The classes are collectible: a designed form holds instances of them only through a
/// <see cref="DesignDocument"/> whose <see cref="DesignDocument.ProjectClasses"/> is <see cref="Find"/>. Before
/// disposing a session, save each such document's text, and reload it (<see cref="DesignDocument.Reload"/>) with the
/// next session's classes, or none: the instances become placeholders that keep their text.</para>
/// </remarks>
public sealed class DesignSession : IDisposable
{
    LiveSession? _live;
    readonly Dictionary<string, ComponentClass> _byName = new(StringComparer.Ordinal);
    readonly Dictionary<ComponentClass, string> _categories = [];
    readonly List<ComponentClass> _classes = [];
    bool _activated;

    DesignSession(RadProject project, LiveSession live)
    {
        Project = project;
        _live = live;
        Messages = live.Messages;
    }

    public RadProject Project { get; }

    /// <summary>The compile's errors and notices (as a live run's: <see cref="LiveSession.Messages"/>).</summary>
    public IReadOnlyList<BuildMessage> Messages { get; }

    /// <summary>Whether the project compiled (without errors, there may be no classes).</summary>
    public bool Success => _live?.Success ?? false;

    /// <summary>What the project was compiled against (null: the designer's assemblies only).</summary>
    public ProjectDependencies? Dependencies => _live?.Dependencies;

    /// <summary>The usable classes, after <see cref="Activate"/>: the project's first, then its libraries', by name.</summary>
    public IReadOnlyList<ComponentClass> Classes => _classes;

    /// <summary>Compiles the project (blocking: MSBuild may restore it first). Any thread.</summary>
    public static DesignSession Compile(RadProject project, LiveRunOptions? options = null) =>
        new(project, LiveSession.Compile(project, null, options));

    /// <summary>
    /// On the UI thread: registers the project's and its libraries' control classes and creates one of each (its
    /// prototype, in design mode). A class that throws, there or in the project's registration code, isn't offered;
    /// what went wrong is returned, one message per problem, for the designer's Output list.
    /// </summary>
    public IReadOnlyList<string> Activate()
    {
        var live = _live ?? throw new ObjectDisposedException(nameof(DesignSession));
        if (_activated) throw new InvalidOperationException("The design session is already active.");
        _activated = true;
        var problems = new List<string>();
        IReadOnlyList<ComponentClass> found;
        try { found = live.GetComponentClasses(includeLibraries: true); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problems.Add($"The project's controls couldn't be registered ({Describe(e)}); they stay placeholders.");
            return problems;
        }
        var own = live.Assembly;
        foreach (var cls in found.OrderBy(c => c.Type.Assembly == own ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            if (_byName.ContainsKey(cls.Name))
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
                problems.Add($"{cls.Name} can't be created at design time ({Describe(e)}); it stays a placeholder.");
                continue;
            }
            _byName[cls.Name] = cls;
            _classes.Add(cls);
            _categories[cls] = cls.Type.Assembly == own ? PaletteCategories.Project : cls.Type.Assembly.GetName().Name ?? "Libraries";
        }
        return problems;
    }

    /// <summary>The usable class named <paramref name="name"/>, or null: for <see cref="DesignDocument.ProjectClasses"/>.</summary>
    public ComponentClass? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The palette category of one of <see cref="Classes"/>: <see cref="PaletteCategories.Project"/>, or the library's assembly name.</summary>
    public string CategoryOf(ComponentClass cls) => _categories.GetValueOrDefault(cls, "Libraries");

    static string Describe(Exception e)
    {
        var inner = e is System.Reflection.TargetInvocationException or TypeInitializationException && e.InnerException != null ? e.InnerException : e;
        return $"{inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>Unregisters the classes and unloads them (once nothing holds an instance: see the remarks).</summary>
    public void Dispose()
    {
        _byName.Clear();
        _categories.Clear();
        _classes.Clear();
        _live?.Dispose();
        _live = null;
    }
}
