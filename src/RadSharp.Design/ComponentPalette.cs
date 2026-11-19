namespace RadSharp.Design;

/// <summary>
/// The component palette's contents, shared by the designer and the Visual Studio design host: which classes
/// can be placed on a form, and the palette tree grouped by <see cref="PaletteCategories"/>.
/// </summary>
public static class ComponentPalette
{
    /// <summary>Every registered class that can be placed on a form, by name.</summary>
    public static List<ComponentClass> Classes() =>
        [.. ComponentRegistry.All
            .Where(c => c.CanCreate && c.Type != typeof(Control) && !typeof(Form).IsAssignableFrom(c.Type) && c.Type != typeof(DesignSurface))
            .OrderBy(c => c.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Refills <paramref name="tree"/> with <paramref name="classes"/> whose name contains <paramref name="filter"/>,
    /// one expanded node per category; each leaf's <see cref="TreeNode.Tag"/> is its <see cref="ComponentClass"/>.
    /// <paramref name="categoryOf"/> gives a class's category (null: <see cref="PaletteCategories.Of"/>); RadSharp's
    /// own categories come first, in name order, then the others (a project's: <see cref="PaletteCategories.Project"/>
    /// first, then its libraries by name), as Delphi adds an installed package's tabs after the standard ones.
    /// </summary>
    public static void Fill(TreeView tree, IEnumerable<ComponentClass> classes, string filter, Func<ComponentClass, string>? categoryOf = null)
    {
        tree.Nodes.Clear();
        filter = filter.Trim();
        categoryOf ??= c => PaletteCategories.Of(c.Name);
        var groups = classes
            .Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(categoryOf)
            .OrderBy(g => PaletteCategories.IsBuiltIn(g.Key) ? 0 : g.Key == PaletteCategories.Project ? 1 : 2)
            .ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var category = new TreeNode(g.Key) { Expanded = true };
            foreach (var cls in g.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var node = category.Add(cls.Name);
                node.Tag = cls;
                node.Icon = PaletteIcons.For(cls);
            }
            tree.Nodes.Add(category);
        }
        // The tree rebuilds its row list lazily (at the next paint): do it now, so the old nodes, and through their tags
        // a replaced design session's classes, aren't kept until then.
        _ = tree.VisibleCount;
        tree.Invalidate();
    }
}
