using RadSharp.Design;

namespace RadSharp.Tests;

/// <summary>
/// Roadmap M7, "done when": the tile-editor form is designed from scratch with designer operations
/// only (palette creation, the inspector, the menu editor, renaming, dragging components) and saves
/// byte-identically to the hand-made M6 version.
/// </summary>
public class RebuildTileEditorTests
{
    [Fact]
    public void Tile_editor_form_designed_from_scratch_saves_byte_identically()
    {
        _ = typeof(TileEditor.MapInfo);  // make sure the sample's components are registered
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TileEditor.radform.json"));
        var reference = FormReader.Load(text).Form;
        var expected = FormWriter.Save(reference);

        var doc = new DesignDocument();
        var inspector = new PropertyGrid();
        CopyProperties(doc, inspector, reference, doc.Form);
        doc.Rename(doc.Form, reference.Name);
        foreach (var child in reference.Children) Rebuild(doc, inspector, child, doc.Form);

        Assert.Equal(expected, doc.Save());
    }

    static void Rebuild(DesignDocument doc, PropertyGrid inspector, Control source, Control parent)
    {
        var cls = ComponentRegistry.Of(source);
        var control = doc.Create(cls, parent, 0, 0);          // the palette
        doc.Rename(control, source.Name);                      // the name field
        if (control is Component) control.SetBounds(source.Left, source.Top, 0, 0);   // dragging its icon
        CopyProperties(doc, inspector, source, control);
        if (source is IMenuOwner menu) RebuildMenu(doc, menu, (IMenuOwner)control);
        foreach (var child in source.Children) Rebuild(doc, inspector, child, control);
    }

    /// <summary>Types every non-default value into the inspector, as a user would (properties, then events).</summary>
    static void CopyProperties(DesignDocument doc, PropertyGrid inspector, Control source, Control target)
    {
        var cls = ComponentRegistry.Of(source);
        foreach (var mode in new[] { PropertyGridMode.Properties, PropertyGridMode.Events })
        {
            inspector.Mode = mode;
            inspector.Target = target;
            for (int i = 0; i < inspector.RowCount; i++)
            {
                var name = inspector.RowName(i);
                string? value = mode == PropertyGridMode.Events
                    ? source.EventHandlers.GetValueOrDefault(name)
                    : cls.FindProperty(name) is { } p && !p.HasDefaultValue(source, cls.Prototype) ? p.FormatValue(source) : null;
                if (value == null) continue;
                doc.Snapshot();
                Assert.True(inspector.Apply(i, value), $"{source.Name}.{name} = {value}");
                doc.NotifyChanged();
            }
        }
    }

    static void RebuildMenu(DesignDocument doc, IMenuOwner source, IMenuOwner target)
    {
        var editor = new MenuEditor(doc, target);
        Edit Field(string name) => (Edit)editor.Form.FindControl(name)!;
        void Add(List<MenuItem> items, bool sub)
        {
            bool first = true;
            foreach (var item in items)
            {
                var created = first && sub ? editor.AddSubItem(item.Caption) : editor.AddItem(item.Caption);
                first = false;
                editor.Select(created);
                Field("edtName").Text = item.Name;
                Field("edtShortcut").Text = item.Shortcut;
                Field("edtHandler").Text = item.ClickHandler;
                ((CheckBox)editor.Form.FindControl("chkEnabled")!).Checked = item.Enabled;
                ((CheckBox)editor.Form.FindControl("chkChecked")!).Checked = item.Checked;
                created.AltShortcut = item.AltShortcut;   // (no field for it yet)
                created.Separator = item.Separator;
                created.Visible = item.Visible;
                if (item.Items.Count > 0) Add(item.Items, sub: true);
                editor.Select(created);
            }
        }
        Add(source.Items, sub: false);
    }
}
