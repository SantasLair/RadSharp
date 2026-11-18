using RadSharp.Design;
using RadSharp.Design.Code;
using RadSharp.Designer;
using RadSharp.Drawing;

namespace RadSharp.RefApp;

/// <summary>
/// Drives the real <see cref="DesignerForm"/> the way a person does, for the reference app's recipes
/// (docs/reference-app-scope.md §4.2). Every verb goes through the designer's own input path: shortcuts and menu
/// clicks, typing into its fields, clicks and drags on the palette and the design surface, the Object Inspector's rows,
/// and the collection editors, whose modal dialogs are answered through <see cref="DriverHost"/>.
/// </summary>
/// <remarks>
/// <para><b>Verbs and the path each one drives.</b></para>
/// <list type="table">
/// <item><term>NewApplication</term><description>Mod+Shift+N (File › New Application); types name, location and package
/// folder into the dialog and clicks Create.</description></item>
/// <item><term>OpenProject</term><description>Mod+O (File › Open Project); the file dialog returns the path.</description></item>
/// <item><term>NewForm</term><description>Mod+N (File › New Form); types the class name and clicks OK.</description></item>
/// <item><term>RenameForm</term><description>mouse: menu bar Project › Rename Form…; types the name, OK.</description></item>
/// <item><term>Activate</term><description>clicks the form's tab above the design surfaces.</description></item>
/// <item><term>Place</term><description>types the class into the palette filter, clicks its palette row, then presses,
/// drags and releases on the surface at the parent's point (holding Alt when the point is off the 8 px grid); a plain
/// click when no size is given.</description></item>
/// <item><term>Select</term><description>clicks a point of the control on the surface; if no point of it is
/// uncovered, clicks its row on the inspector's Structure tab.</description></item>
/// <item><term>SelectForm</term><description>clicks the empty canvas outside the form.</description></item>
/// <item><term>ShowPage</term><description>clicks the TabSheet's tab on the surface.</description></item>
/// <item><term>Name</term><description>types into the Name box, Enter (the designer renames the code too).</description></item>
/// <item><term>Set</term><description>types the property into the inspector filter, clicks the row, then: Enter, type,
/// Enter (text); Enter and a click on the list item (enums); a click on the check box (booleans).</description></item>
/// <item><term>Anchors</term><description>clicks the anchors row's arrow, then the flag rows' check boxes.</description></item>
/// <item><term>Handler</term><description>Events tab, event filter, double-click on the event row (writes the stub into
/// the .cs and saves). With a name (a handler shared by several controls): click the row, Enter, type the name, Enter,
/// then double-click the row, because Enter alone writes no stub (a designer bug; the double-click is the interim).</description></item>
/// <item><term>Strings</term><description>a string-list row (a combo box's items): Enter opens the memo; type the lines with
/// Enter between them; a click on the row's name closes the memo and keeps the text.</description></item>
/// <item><term>Columns / Nodes / Menu</term><description>the inspector's columns/nodes row + Enter, or Mod+M for the
/// menu editor; then the editor's own buttons, fields and tree, by mouse and keyboard. Columns clicks each entry of the
/// column list before filling it (a grid's first Add turns its untitled columns into entries) and deletes leftovers. A
/// menu item's handler is typed into onClick (the stubs are written when the editor closes) or, with
/// <see cref="Item.Click"/>, made by double-clicking the item.</description></item>
/// <item><term>Save, Run</term><description>Mod+S, F9.</description></item>
/// </list>
/// <para><b>Bypasses</b> (where this differs from a person at a real window), each a gap the hand check (§4.4) covers:</para>
/// <list type="number">
/// <item>No real window or OS events: input is synthesized with <see cref="Form.Simulate"/>, which runs the same routines
/// as real input, but there is no hover dwell, no OS drag threshold and no IME.</item>
/// <item>Modal dialogs are answered synchronously inside <see cref="IPlatformServices.RunModal"/> (<see cref="DriverHost"/>);
/// file dialogs return a queued path instead of showing a native dialog.</item>
/// <item>Locating things on screen uses the controls' own geometry (<c>RowRect</c>, <c>ItemRect</c>, <c>TabRects</c>,
/// <c>ItemRects</c>, <see cref="DesignSurface.DesignToScreen"/>, <see cref="DesignSurface.ControlAt"/>); a person uses
/// their eyes. Rows that are out of view are scrolled to with the mouse wheel.</item>
/// <item>The designer window is 1920 × 1200 at 100 % so whole forms fit on the surface without scrolling.</item>
/// <item>The "external editor" the designer launches after creating a handler is a command that doesn't exist, so
/// nothing is started (the designer reports "Could not start …" in its output, which is ignored).</item>
/// <item>Designer settings live in a temporary file, not the user's.</item>
/// <item>Posted actions (<see cref="Application.Queue"/>) run when the driver pumps, after each verb.</item>
/// <item>The app tests (<see cref="LiveTally"/>) stand in for a 150 % monitor by setting <see cref="Form.DefaultScale"/>
/// before F9 and while a dialog opens; a real host takes it from the display.</item>
/// </list>
/// </remarks>
public sealed class DesignerDriver
{
    public DesignerForm Designer { get; }
    public DriverHost Host { get; }

    DesignerDriver(DesignerForm designer, DriverHost host)
    {
        Designer = designer;
        Host = host;
    }

    /// <summary>Starts the designer with no project (the welcome page), settings kept in <paramref name="settingsFolder"/>.</summary>
    public static DesignerDriver Start(string settingsFolder)
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        Form.DefaultScale = 1;
        var host = new DriverHost();
        PlatformHost.Current = host;
        var designer = new DesignerForm(null, Path.Combine(settingsFolder, "designer.json"));
        designer.Settings.Editor = EditorKind.Custom;
        designer.Settings.CustomEditorCommand = "\"radsharp-refapp-no-editor\" \"{file}\" {line}";
        designer.SetBounds(0, 0, 1920, 1200);
        designer.PerformLayout();
        return new DesignerDriver(designer, host);
    }

    Form D => Designer;
    InputSimulator Input => Designer.Simulate;

    /// <summary>The active form's document.</summary>
    public DesignDocument Doc => Designer.Document ?? throw new DriverException("No form is open.");
    DesignSurface Surface => Designer.ActiveForm?.Surface ?? throw new DriverException("No form is open.");
    T Find<T>(string name) where T : Control => D.FindControl(name) as T ?? throw new DriverException($"The designer has no {typeof(T).Name} {name}.");

    /// <summary>A control on the active form by name (the form itself by its own name).</summary>
    public Control Control(string name)
    {
        var form = Doc.Form;
        if (name == form.Name) return form;
        return form.FindControl(name) ?? throw new DriverException($"{Designer.ActiveForm!.ClassName} has no control {name}.");
    }

    void Done()
    {
        Host.Pump();
        D.PerformLayout();
    }

    // ---- Projects and forms ----

    /// <summary>File › New Application (Mod+Shift+N): the project goes in <c>location/name</c>.</summary>
    public RadProject NewApplication(string name, string location, string? packages)
    {
        Host.Expect("New Application", f =>
        {
            TypeInto(f, f.FindControl("edtName"), name);
            TypeInto(f, f.FindControl("edtLocation"), location);
            TypeInto(f, f.FindControl("edtPackages"), packages ?? "");
            return "btnOk";
        });
        Input.Key("Mod+Shift+N");
        Done();
        ExpectAnswered();
        return Designer.Project ?? throw new DriverException("New Application didn't open a project: " + string.Join(" / ", Designer.OutputLines));
    }

    /// <summary>File › Open Project (Mod+O).</summary>
    public RadProject OpenProject(string projectFile)
    {
        Host.NextFiles.Enqueue(projectFile);
        Input.Key("Mod+O");
        Done();
        return Designer.Project ?? throw new DriverException($"Could not open {projectFile}: " + string.Join(" / ", Designer.OutputLines));
    }

    /// <summary>File › New Form (Mod+N), answering the class name.</summary>
    public void NewForm(string className)
    {
        Host.Expect("New Form", f => { TypeInto(f, f.FindControl("edtFormName"), className); return "btnOk"; });
        Input.Key("Mod+N");
        Done();
        ExpectAnswered();
        if (Designer.ActiveForm?.ClassName != className) throw new DriverException($"New Form didn't create {className}.");
    }

    /// <summary>Project › Rename Form… on the active form.</summary>
    public void RenameForm(string newName)
    {
        Host.Expect("Rename Form", f => { TypeInto(f, f.FindControl("edtFormName"), newName); return "btnOk"; });
        ClickMenu(D, Find<MainMenu>("mnuMain"), "Project", "Rename Form…");
        Done();
        ExpectAnswered();
        if (Designer.ActiveForm?.ClassName != newName) throw new DriverException($"Rename Form didn't rename to {newName}.");
    }

    /// <summary>Switches to a form's tab.</summary>
    public void Activate(string className)
    {
        var pg = Find<PageControl>("pgForms");
        int i = pg.Pages.ToList().FindIndex(p => p.Caption.TrimEnd(' ', '*') == className);
        if (i < 0) throw new DriverException($"No tab for {className}.");
        ClickTab(D, pg, i);
        Done();
    }

    /// <summary>Mod+S.</summary>
    public void Save()
    {
        Input.Key("Mod+S");
        Done();
    }

    /// <summary>F9: returns the live form, shown by the host.</summary>
    public Form Run()
    {
        int before = Host.Shown.Count;
        Input.Key(Key.F9);
        Done();
        if (Host.Shown.Count <= before)
            throw new DriverException("F9 didn't start the application:\n" + string.Join("\n", Designer.OutputLines));
        return Host.Shown[^1];
    }

    // ---- Placing and selecting ----

    /// <summary>
    /// Places a <paramref name="className"/> from the palette into <paramref name="parent"/> at a parent-relative
    /// rectangle by dragging it out on the surface; with no size given, a click places it at its default size.
    /// Returns the new control (selected).
    /// </summary>
    public Control Place(string className, string parent, int x, int y, int width = 0, int height = 0)
    {
        var target = Control(parent);
        ArmPalette(className);
        var (ox, oy) = ParentOrigin(target);
        int x1 = ox + x, y1 = oy + y, x2 = x1 + width, y2 = y1 + height;
        bool free = x1 % 8 != 0 || y1 % 8 != 0 || x2 % 8 != 0 || y2 % 8 != 0;
        var mods = free ? KeyModifiers.Alt : KeyModifiers.None;
        var (sx1, sy1) = ScreenPoint(x1, y1);
        int before = Doc.Form.Descendants().Count();
        Input.Press(sx1, sy1, MouseButton.Left, mods);
        if (width > 0 || height > 0)
        {
            var (sx2, sy2) = ScreenPoint(x2, y2);
            for (int i = 1; i <= 4; i++) Input.MoveTo(sx1 + (sx2 - sx1) * i / 4, sy1 + (sy2 - sy1) * i / 4, mods);
            Input.Release(sx2, sy2, MouseButton.Left, mods);
        }
        else Input.Release(sx1, sy1, MouseButton.Left, mods);
        Done();
        var created = Doc.Primary;
        if (Doc.Form.Descendants().Count() <= before || ComponentRegistry.Of(created).Name != className)
            throw new DriverException($"Placing {className} in {parent} at ({x}, {y}) created nothing.");
        var expectedParent = created is Component ? Doc.Form : target;
        if (created.Parent != expectedParent)
            throw new DriverException($"{className} landed in {created.Parent?.Name} instead of {expectedParent.Name}: the press point is covered.");
        return created;
    }

    /// <summary>Types the class into the palette filter and clicks its row, arming the surface.</summary>
    void ArmPalette(string className)
    {
        TypeInto(D, Find<Edit>("edtPaletteFilter"), className);
        var tv = Find<TreeView>("tvPalette");
        var leaf = tv.Nodes.SelectMany(n => n.Nodes).FirstOrDefault(n => n.Text == className)
            ?? throw new DriverException($"{className} isn't on the palette.");
        ClickNode(D, tv, leaf);
        if (Surface.PendingClass?.Name != className) throw new DriverException($"Clicking {className} on the palette didn't arm it.");
    }

    static (int X, int Y) ParentOrigin(Control parent)
    {
        if (parent is Form) return (0, 0);
        var (cx, cy) = parent.ContentOffset;
        return (parent.AbsoluteLeft + cx, parent.AbsoluteTop + cy);
    }

    /// <summary>A design point as designer-window coordinates, wheeling the surface until it is in view.</summary>
    (int X, int Y) ScreenPoint(int designX, int designY)
    {
        var surface = Surface;
        for (int i = 0; i < 100; i++)
        {
            var (x, y) = surface.DesignToScreen(designX, designY);
            int dy = y < 8 ? 1 : y > surface.Height - 8 ? -1 : 0;
            int dx = x < 8 ? 1 : x > surface.Width - 8 ? -1 : 0;
            if (dx == 0 && dy == 0) return (surface.AbsoluteLeft + x, surface.AbsoluteTop + y);
            if (dy != 0) Input.Wheel(surface.AbsoluteLeft + surface.Width / 2, surface.AbsoluteTop + surface.Height / 2, dy);
            else
            {
                D.ProcessMouseWheel(surface.AbsoluteLeft + surface.Width / 2, surface.AbsoluteTop + surface.Height / 2, 0, dx, KeyModifiers.Shift);
            }
        }
        throw new DriverException($"Design point ({designX}, {designY}) can't be scrolled into view.");
    }

    /// <summary>Selects a control: a click on an uncovered point of it, or its row on the Structure tab.</summary>
    public void Select(string name)
    {
        var c = Control(name);
        if (c is Form) { SelectForm(); return; }
        if (FindSurfacePoint(c) is { } p)
        {
            var (sx, sy) = ScreenPoint(p.X, p.Y);
            Input.Click(sx, sy);
        }
        else
        {
            var pg = Find<PageControl>("pgInspector");
            ClickTab(D, pg, pg.Pages.ToList().FindIndex(t => t.Name == "tsStructure"));
            var tv = Find<TreeView>("tvStructure");
            var node = AllNodes(tv.Nodes).FirstOrDefault(n => n.Tag == c) ?? throw new DriverException($"{name} isn't in the structure tree.");
            ClickNode(D, tv, node);
        }
        Done();
        if (Doc.Primary != c || Doc.Selection.Count != 1) throw new DriverException($"Couldn't select {name}.");
    }

    /// <summary>A design point where the surface hits <paramref name="c"/>, avoiding a page control's tabs.</summary>
    Point? FindSurfacePoint(Control c)
    {
        var surface = Surface;
        var r = DesignSurface.DesignRect(c);
        IEnumerable<(int X, int Y)> Candidates()
        {
            if (c is PageControl pc)
            {
                var tabs = pc.TabRects();
                int right = tabs.Count > 0 ? tabs[^1].X + tabs[^1].Width : 0;
                yield return (r.X + Math.Max(right + 8, (right + r.Width) / 2), r.Y + 8);
                yield break;
            }
            yield return (r.X + r.Width / 2, r.Y + r.Height / 2);
            for (int y = r.Y + 2; y < r.Y + r.Height; y += 6)
                for (int x = r.X + 2; x < r.X + r.Width; x += 6) yield return (x, y);
        }
        foreach (var (x, y) in Candidates())
            if (x < r.X + r.Width && surface.ControlAt(x, y) == c) return new Point(x, y);
        return null;
    }

    /// <summary>Selects the form itself: a click on the empty canvas above and left of it.</summary>
    public void SelectForm()
    {
        var (sx, sy) = ScreenPoint(-8, -8);
        Input.Click(sx, sy);
        Done();
        if (Doc.Selection.Count != 0) throw new DriverException("Couldn't select the form.");
    }

    /// <summary>Shows a TabSheet in the designer by clicking its tab (this also selects its PageControl).</summary>
    public void ShowPage(string tabSheet)
    {
        var sheet = Control(tabSheet) as TabSheet ?? throw new DriverException($"{tabSheet} isn't a TabSheet.");
        var pc = sheet.Parent as PageControl ?? throw new DriverException($"{tabSheet} isn't on a PageControl.");
        int i = pc.Pages.ToList().IndexOf(sheet);
        var tab = pc.TabRects()[i];
        var (sx, sy) = ScreenPoint(pc.AbsoluteLeft + tab.X + tab.Width / 2, pc.AbsoluteTop + tab.Height / 2);
        Input.Click(sx, sy);
        Done();
        if (pc.ActivePage != sheet) throw new DriverException($"Clicking the tab didn't show {tabSheet}.");
    }

    // ---- Name box and Object Inspector ----

    /// <summary>Renames the selected control through the Name box.</summary>
    public void Name(string name)
    {
        var target = Doc.Primary;
        TypeInto(D, Find<Edit>("edtName"), name);
        Input.Key(Key.Enter);
        Done();
        if (target.Name != name) throw new DriverException($"The Name box didn't rename {ComponentRegistry.Of(target).Name} to {name}.");
    }

    PropertyGrid ShowInspector(bool events)
    {
        var pg = Find<PageControl>("pgInspector");
        int i = pg.Pages.ToList().FindIndex(t => t.Name == (events ? "tsEvents" : "tsProperties"));
        if (pg.ActivePageIndex != i) ClickTab(D, pg, i);
        return Find<PropertyGrid>(events ? "gridEvents" : "gridProps");
    }

    /// <summary>
    /// Filters the inspector's Properties tab to <paramref name="name"/>; returns its row, after clicking the row's name
    /// to select it (unless <paramref name="click"/> is false: on the anchors row that click toggles the flag rows).
    /// </summary>
    int InspectorRow(string name, bool click = true)
    {
        var grid = ShowInspector(events: false);
        TypeInto(D, Find<Edit>("edtPropsFilter"), name);
        int row = grid.IndexOf(name);
        if (row < 0) throw new DriverException($"The inspector has no property {name} for {Doc.Primary.Name}.");
        var r = grid.RowRect(row);
        if (click) Input.Click(grid.AbsoluteLeft + r.X + 12, grid.AbsoluteTop + r.Y + r.Height / 2);
        return row;
    }

    /// <summary>Sets a property of the selected control(s) in the Object Inspector.</summary>
    public void Set(string property, string value)
    {
        var grid = ShowInspector(events: false);
        int row = InspectorRow(property);
        var target = grid.Targets[0];
        var meta = ComponentRegistry.Of(target).FindProperty(property) ?? throw new DriverException($"No property {property}.");
        if (meta.Type == PropertyType.Bool)
        {
            if (grid.RowValue(row) != value)
            {
                var v = grid.ValueRect(row);
                Input.Click(grid.AbsoluteLeft + v.X + 12, grid.AbsoluteTop + v.Y + v.Height / 2);
            }
        }
        else if (meta.Values is { Count: > 0 } values)
        {
            int index = values.ToList().IndexOf(value);
            if (index < 0) throw new DriverException($"{property} has no value {value} ({string.Join(", ", values)}).");
            Input.Key(Key.Enter);
            var list = D.Popups.LastOrDefault() as ListBox ?? throw new DriverException($"Enter on {property} opened no list.");
            var item = list.ItemRect(index);
            Input.Click(list.Left + item.X + 10, list.Top + item.Y + item.Height / 2);
        }
        else if (meta.Type == PropertyType.Strings) throw new DriverException($"{property}: string lists aren't supported by the driver yet.");
        else
        {
            Input.Key(Key.Enter);
            if (!grid.IsEditing) throw new DriverException($"Enter on {property} opened no editor.");
            if (value.Length == 0) Input.Key(Key.Delete);   // the editor opens with the old text selected
            else Input.Type(value);
            Input.Key(Key.Enter);
        }
        Done();
        if (grid.RowValue(row) != value)
            throw new DriverException($"The inspector shows {property} = \"{grid.RowValue(row)}\" after entering \"{value}\".");
    }

    /// <summary>Sets the selected control's anchors through the inspector's anchors.left/top/right/bottom check boxes.</summary>
    public void Anchors(params string[] sides)
    {
        var grid = ShowInspector(events: false);
        int row = InspectorRow("anchors", click: false);
        var r = grid.RowRect(row);
        if (grid.IndexOf("anchors.left") < 0) Input.Click(grid.AbsoluteLeft + r.X + 12, grid.AbsoluteTop + r.Y + r.Height / 2);   // the arrow
        foreach (var side in new[] { "left", "top", "right", "bottom" })
        {
            int i = grid.IndexOf("anchors." + side);
            if (i < 0) throw new DriverException("The anchors row didn't expand.");
            bool want = sides.Contains(side);
            if ((grid.RowValue(i) == "true") == want) continue;
            var v = grid.ValueRect(i);
            Input.Click(grid.AbsoluteLeft + v.X + 12, grid.AbsoluteTop + v.Y + v.Height / 2);
        }
        Done();
    }

    /// <summary>Double-clicks an event on the Events tab: the designer names the handler and writes its stub.</summary>
    public string Handler(string eventName)
    {
        var target = Doc.Primary;
        var grid = ShowInspector(events: true);
        TypeInto(D, Find<Edit>("edtEventsFilter"), eventName);
        int row = grid.IndexOf(eventName);
        if (row < 0) throw new DriverException($"{target.Name} has no event {eventName}.");
        var r = grid.RowRect(row);
        Input.DoubleClick(grid.AbsoluteLeft + r.X + 12, grid.AbsoluteTop + r.Y + r.Height / 2);
        Done();
        return target.EventHandlers.GetValueOrDefault(eventName) ?? throw new DriverException($"Double-clicking {eventName} wrote no handler.");
    }

    /// <summary>
    /// Gives the selected control's event a handler by name (to share one method between several controls): Events tab,
    /// click the row, Enter, type the name, Enter. Then a double-click on the row, because Enter alone writes no stub into
    /// the code (bug, docs/reference-app-scope.md §5.1); the double-click keeps the typed name and writes the stub if the
    /// method doesn't exist yet.
    /// </summary>
    public void Handler(string eventName, string name)
    {
        var target = Doc.Primary;
        var grid = ShowInspector(events: true);
        TypeInto(D, Find<Edit>("edtEventsFilter"), eventName);
        int row = grid.IndexOf(eventName);
        if (row < 0) throw new DriverException($"{target.Name} has no event {eventName}.");
        var r = grid.RowRect(row);
        int x = grid.AbsoluteLeft + r.X + 12, y = grid.AbsoluteTop + r.Y + r.Height / 2;
        Input.Click(x, y);
        Input.Key(Key.Enter);
        if (!grid.IsEditing) throw new DriverException($"Enter on {eventName} opened no editor.");
        Input.Key("Mod+A");
        Input.Type(name);
        Input.Key(Key.Enter);
        Input.DoubleClick(x, y);
        Done();
        if (target.EventHandlers.GetValueOrDefault(eventName) != name)
            throw new DriverException($"{target.Name}.{eventName} is \"{target.EventHandlers.GetValueOrDefault(eventName)}\" after typing {name}.");
    }

    /// <summary>
    /// Sets a string-list property (a combo box's items) of the selected control: the row's Enter opens a memo; type one
    /// line per item (Enter between them), then click the row's name, which closes the memo and keeps the text.
    /// </summary>
    public void Strings(string property, params string[] lines)
    {
        var grid = ShowInspector(events: false);
        int row = InspectorRow(property);
        Input.Key(Key.Enter);
        if (!grid.IsEditing || D.Popups.LastOrDefault() is not Memo) throw new DriverException($"Enter on {property} opened no list editor.");
        Input.Key("Mod+A");
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) Input.Key(Key.Enter);
            Input.Type(lines[i]);
        }
        var r = grid.RowRect(row);
        Input.Click(grid.AbsoluteLeft + r.X + 12, grid.AbsoluteTop + r.Y + r.Height / 2);
        Done();
        if (grid.IsEditing) throw new DriverException($"Clicking beside the {property} editor didn't close it.");
        var expected = lines.Length == 1 ? "(1 items)" : $"({lines.Length} items)";
        if (grid.RowValue(row) != expected) throw new DriverException($"The inspector shows {property} = {grid.RowValue(row)} after entering {lines.Length} lines.");
    }

    // ---- Collection editors ----

    /// <summary>A column for <see cref="Columns"/>: title, width (0: default) and alignment.</summary>
    public sealed record Column(string Title, int Width = 0, TextAlign Alignment = TextAlign.Left, int MinWidth = 0);

    /// <summary>Adds columns to the selected grid or list view with the Columns editor (the inspector's columns row).</summary>
    public void Columns(params Column[] columns)
    {
        InspectorRow("columns");
        Host.Expect("Columns", f =>
        {
            // A list view starts with no columns; Add makes the first. A grid's first Add also turns its existing untitled
            // columns into entries (the editor's hint says so), so the entries are filled in order, adding more as needed
            // and deleting any left over.
            var list = (ListBox)f.FindControl("lstColumns")!;
            void ClickEntry(int i)
            {
                f.PerformLayout();
                var r = list.ItemRect(i);
                f.Simulate.Click(list.AbsoluteLeft + r.X + 10, list.AbsoluteTop + r.Y + r.Height / 2);
                if (list.ItemIndex != i) throw new DriverException($"Clicking column entry {i} didn't select it.");
            }
            if (list.Items.Length == 0) f.Simulate.Click(f.FindControl("btnAdd")!);
            for (int i = 0; i < columns.Length; i++)
            {
                var c = columns[i];
                if (i >= list.Items.Length)
                {
                    ClickEntry(list.Items.Length - 1);
                    f.Simulate.Click(f.FindControl("btnAdd")!);
                }
                ClickEntry(i);
                TypeInto(f, f.FindControl("edtTitle"), c.Title);
                if (c.Width > 0) { TypeInto(f, f.FindControl("spnWidth"), c.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)); f.Simulate.Key(Key.Tab); }
                if (c.MinWidth > 0) { TypeInto(f, f.FindControl("spnMinWidth"), c.MinWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)); f.Simulate.Key(Key.Tab); }
                if (c.Alignment != TextAlign.Left) PickCombo(f, (ComboBox)f.FindControl("cboAlignment")!, (int)c.Alignment);
            }
            while (list.Items.Length > columns.Length)
            {
                ClickEntry(list.Items.Length - 1);
                f.Simulate.Click(f.FindControl("btnDelete")!);
            }
            return "btnClose";
        });
        Input.Key(Key.Enter);
        Done();
        ExpectAnswered();
    }

    /// <summary>A tree node for <see cref="Nodes"/>.</summary>
    public sealed record Node(string Text, bool Expanded = false, params Node[] Children);

    /// <summary>Fills the selected (empty) TreeView with the Nodes editor (the inspector's nodes row).</summary>
    public void Nodes(params Node[] nodes)
    {
        InspectorRow("nodes");
        Host.Expect("Nodes", f =>
        {
            var tv = (TreeView)f.FindControl("tvNodes")!;
            TreeNode? Selected() => tv.Selected?.Tag as TreeNode;
            void SelectNode(TreeNode target) => ClickNode(f, tv, AllNodes(tv.Nodes).First(n => n.Tag == target));
            void Build(Node[] list, TreeNode? parent)
            {
                TreeNode? previous = null;
                foreach (var spec in list)
                {
                    if (previous != null) { SelectNode(previous); f.Simulate.Click(f.FindControl("btnAdd")!); }
                    else if (parent != null) { SelectNode(parent); f.Simulate.Click(f.FindControl("btnAddChild")!); }
                    else f.Simulate.Click(f.FindControl("btnAdd")!);
                    var created = Selected() ?? throw new DriverException("The Nodes editor selected nothing after adding.");
                    TypeInto(f, f.FindControl("edtText"), spec.Text);
                    var chk = (CheckBox)f.FindControl("chkExpanded")!;
                    if (chk.Checked != spec.Expanded) f.Simulate.Click(chk);
                    Build(spec.Children, created);
                    previous = created;
                }
            }
            Build(nodes, null);
            return "btnClose";
        });
        Input.Key(Key.Enter);
        Done();
        ExpectAnswered();
    }

    /// <summary>
    /// A menu item for <see cref="Menu"/>; <see cref="Separator"/> is a separator line. <paramref name="Handler"/> is typed
    /// into the onClick field (the editor writes the stub when it closes); <see cref="Click"/> instead double-clicks the
    /// item, which names the handler <c>&lt;name&gt;_Click</c> and writes the stub at once.
    /// </summary>
    public sealed record Item(string Caption, string Name = "", string Shortcut = "", string Handler = "", string AltShortcut = "",
        bool Enabled = true, bool Checked = false, params Item[] Items)
    {
        public static readonly Item Separator = new("-");

        /// <summary>Put in <see cref="Handler"/>: double-click the item for its default handler instead of typing one.</summary>
        public const string Click = "(double-click)";
    }

    /// <summary>Fills the selected (empty) MainMenu or PopupMenu with the menu editor (Component › Menu Editor, Mod+M).</summary>
    public void Menu(params Item[] items)
    {
        if (Doc.Primary is not IMenuOwner) throw new DriverException("Select a menu first.");
        Host.Expect("Menu", f =>
        {
            var tv = (TreeView)f.FindControl("tvItems")!;
            MenuItem? Selected() => tv.Selected?.Tag as MenuItem;
            void SelectItem(MenuItem target) => ClickNode(f, tv, AllNodes(tv.Nodes).First(n => n.Tag == target));
            void Build(Item[] list, MenuItem? parent)
            {
                MenuItem? previous = null;
                foreach (var spec in list)
                {
                    bool separator = spec.Caption == "-";
                    if (previous != null) { SelectItem(previous); f.Simulate.Click(f.FindControl(separator ? "btnSeparator" : "btnAdd")!); }
                    else if (parent != null) { SelectItem(parent); f.Simulate.Click(f.FindControl("btnAddSub")!); }
                    else f.Simulate.Click(f.FindControl("btnAdd")!);
                    var created = Selected() ?? throw new DriverException("The menu editor selected nothing after adding.");
                    if (!separator || previous == null) TypeInto(f, f.FindControl("edtCaption"), spec.Caption);
                    if (spec.Name.Length > 0) TypeInto(f, f.FindControl("edtName"), spec.Name);
                    if (spec.Shortcut.Length > 0) TypeInto(f, f.FindControl("edtShortcut"), spec.Shortcut);
                    if (spec.AltShortcut.Length > 0) TypeInto(f, f.FindControl("edtAltShortcut"), spec.AltShortcut);
                    if (spec.Handler == Item.Click)
                    {
                        DoubleClickNode(f, tv, AllNodes(tv.Nodes).First(n => n.Tag == created));
                        if (created.ClickHandler.Length == 0) throw new DriverException($"Double-clicking {spec.Caption} gave it no handler.");
                    }
                    else if (spec.Handler.Length > 0) TypeInto(f, f.FindControl("edtHandler"), spec.Handler);
                    var enabled = (CheckBox)f.FindControl("chkEnabled")!;
                    if (enabled.Checked != spec.Enabled) f.Simulate.Click(enabled);
                    var check = (CheckBox)f.FindControl("chkChecked")!;
                    if (check.Checked != spec.Checked) f.Simulate.Click(check);
                    Build(spec.Items, created);
                    previous = created;
                }
            }
            Build(items, null);
            return "btnClose";
        });
        Input.Key("Mod+M");
        Done();
        ExpectAnswered();
    }

    // ---- Input helpers (also used on dialogs and on the live form) ----

    /// <summary>Clicks into an edit, selects all (Mod+A) and types <paramref name="text"/> over it.</summary>
    public static void TypeInto(Form form, Control? edit, string text)
    {
        if (edit == null) throw new DriverException("No such field.");
        form.Simulate.Click(edit);
        form.Simulate.Key("Mod+A");
        if (text.Length == 0) form.Simulate.Key(Key.Delete);
        else form.Simulate.Type(text);
        var actual = edit switch { Edit e => e.Text, _ => text };
        if (actual != text) throw new DriverException($"{edit.Name} shows \"{actual}\" after typing \"{text}\".");
    }

    /// <summary>Clicks a tree node's row (wheeling it into view first).</summary>
    public static void ClickNode(Form form, TreeView tree, TreeNode node)
    {
        form.PerformLayout();
        for (int i = 0; i < 200; i++)
        {
            int row = tree.RowOf(node);
            if (row < 0) throw new DriverException($"Tree node {node.Text} isn't visible (collapsed parent?).");
            var r = tree.RowRect(row);
            var v = tree.Viewport;
            if (r.Y >= v.Y && r.Y + r.Height <= v.Y + v.Height)
            {
                form.Simulate.Click(tree.AbsoluteLeft + r.X + Math.Min(r.Width / 2, 120 + node.Level * 20), tree.AbsoluteTop + r.Y + r.Height / 2);
                if (tree.Selected != node) throw new DriverException($"Clicking {node.Text} didn't select it.");
                return;
            }
            form.Simulate.Wheel(tree.AbsoluteLeft + v.X + v.Width / 2, tree.AbsoluteTop + v.Y + v.Height / 2, r.Y < v.Y ? 1 : -1);
        }
        throw new DriverException($"Tree node {node.Text} can't be scrolled into view.");
    }

    /// <summary>Double-clicks a tree node's row: its first click is <see cref="ClickNode"/> (which scrolls it into view).</summary>
    public static void DoubleClickNode(Form form, TreeView tree, TreeNode node)
    {
        ClickNode(form, tree, node);
        var r = tree.RowRect(tree.RowOf(node));
        int x = tree.AbsoluteLeft + r.X + Math.Min(r.Width / 2, 120 + node.Level * 20), y = tree.AbsoluteTop + r.Y + r.Height / 2;
        form.Simulate.Press(x, y, clicks: 2);
        form.Simulate.Release(x, y);
    }

    /// <summary>Clicks a page control's tab.</summary>
    public static void ClickTab(Form form, PageControl pages, int index)
    {
        form.PerformLayout();
        var tab = pages.TabRects()[index];
        form.Simulate.Click(pages.AbsoluteLeft + tab.X + tab.Width / 2, pages.AbsoluteTop + tab.Height / 2);
        if (pages.ActivePageIndex != index) throw new DriverException($"Clicking tab {index} of {pages.Name} didn't switch to it.");
    }

    /// <summary>Clicks through a main menu with the mouse: the bar item, then each (sub)menu item by caption (without the &amp;).</summary>
    public static void ClickMenu(Form form, MainMenu menu, params string[] path)
    {
        form.PerformLayout();
        var top = menu.ItemRects().FindIndex(t => Plain(t.Item.Caption) == path[0]);
        if (top < 0) throw new DriverException($"No menu {path[0]}.");
        var rect = menu.ItemRects()[top].Rect;
        form.Simulate.Click(menu.AbsoluteLeft + rect.X + rect.Width / 2, menu.AbsoluteTop + rect.Height / 2);
        foreach (var caption in path.Skip(1))
        {
            var popup = form.Popups.LastOrDefault() as MenuPopup ?? throw new DriverException($"No menu opened before {caption}.");
            int i = popup.Items.ToList().FindIndex(m => Plain(m.Caption) == caption);
            if (i < 0) throw new DriverException($"No menu item {caption}: {string.Join(", ", popup.Items.Select(m => m.Caption))}");
            var r = popup.ItemRect(i);
            form.Simulate.Click(popup.Left + r.X + 10, popup.Top + r.Y + r.Height / 2);
        }
    }

    static string Plain(string caption) => caption.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");

    /// <summary>Picks a combo box item from its drop-down list with the mouse.</summary>
    public static void PickCombo(Form form, ComboBox combo, int index)
    {
        form.Simulate.Click(combo);
        if (form.Popups.LastOrDefault() is not ListBox list)
        {
            var r = combo.Bounds;
            form.Simulate.Click(combo.AbsoluteLeft + r.Width - 8, combo.AbsoluteTop + r.Height / 2);
            list = form.Popups.LastOrDefault() as ListBox ?? throw new DriverException($"{combo.Name} opened no list.");
        }
        var item = list.ItemRect(index);
        form.Simulate.Click(list.Left + item.X + 10, list.Top + item.Y + item.Height / 2);
        if (combo.ItemIndex != index) throw new DriverException($"{combo.Name} didn't select item {index}.");
    }

    public static IEnumerable<TreeNode> AllNodes(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Nodes)) yield return c;
        }
    }

    void ExpectAnswered()
    {
        if (Host.PendingModals > 0)
            throw new DriverException("An expected dialog never appeared. Output:\n" + string.Join("\n", Designer.OutputLines));
    }

    readonly record struct Point(int X, int Y);
}

/// <summary>The designer didn't do what a recipe step asked: either the recipe is wrong or the designer has a bug.</summary>
public sealed class DriverException(string message) : Exception(message);
