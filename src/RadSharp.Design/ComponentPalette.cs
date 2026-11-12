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
    /// </summary>
    public static void Fill(TreeView tree, IEnumerable<ComponentClass> classes, string filter)
    {
        tree.Nodes.Clear();
        filter = filter.Trim();
        var groups = classes
            .Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => PaletteCategories.Of(c.Name))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
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
        tree.Invalidate();
    }
}
