using System.Text;
using RadSharp.Drawing;

namespace RadSharp.Designer;

/// <summary>Help › Keyboard Shortcuts: a one-page reference generated from the <see cref="CommandRegistry"/>.</summary>
public static class ShortcutsReference
{
    /// <summary>Every command by category with its current binding (an empty binding is shown as "unassigned") and its id.</summary>
    public static string Generate(CommandRegistry registry)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RADSharp keyboard shortcuts");
        sb.AppendLine("Generated from the command list; rebind keys in Tools > Keyboard Shortcuts.");
        foreach (var group in registry.All.GroupBy(c => c.Category))
        {
            sb.AppendLine().AppendLine(group.Key).AppendLine(new string('-', group.Key.Length));
            foreach (var c in group)
            {
                var keys = c.Shortcut.Length > 0 ? DesignerForm.ShortcutText(c.Shortcut) : "unassigned";
                if (c.AltShortcut.Length > 0) keys += " / " + DesignerForm.ShortcutText(c.AltShortcut);
                sb.AppendLine($"{c.Title,-38}{keys,-26}{c.Id}");
            }
        }
        return sb.ToString();
    }
}

/// <summary>The reference as a read-only list, for when no project (and so no document area) is open.</summary>
public sealed class ShortcutsReferenceDialog : Form
{
    public ShortcutsReferenceDialog(string text, Form? owner)
    {
        DialogLayout.Setup(this, "Keyboard Shortcuts", 760, 560, owner);
        DialogLayout.Put(this, new ListBox { Name = "lstShortcuts", Items = text.Replace("\r\n", "\n").Split('\n'), ItemHeight = 20 }, 16, 16, 728, 454);
        DialogLayout.Put(this, new Button { Name = "btnClose", Caption = "Close", Default = true, Cancel = true, ModalResult = ModalResult.Cancel }, 654, 486, 90, 30);
        PerformLayout();
    }
}

public partial class DesignerForm
{
    void RegisterPolishCommands()
    {
        _commands.Register(new Command("Help.KeyboardShortcuts", "Keyboard Shortcuts Reference", "Help", () => ShowShortcutsReference()));
        _commands.Register(new Command("Code.ReloadLanguage", "Reload Language Service", "Code", ReloadAllLanguage, "", () => HasProject));
    }

    void AddHelpMenuItems(MenuItem help) => help.Items.Add(_commands.CreateMenuItem("Help.KeyboardShortcuts"));

    /// <summary>
    /// Help › Keyboard Shortcuts Reference: opens the generated reference as a read-only document beside the project's files
    /// (rewritten each time, so it shows the current bindings); with no project open, as a list window. Returns the document, if any.
    /// </summary>
    public CodeDocument? ShowShortcutsReference()
    {
        var text = ShortcutsReference.Generate(_commands);
        if (_project == null)
        {
            new ShortcutsReferenceDialog(text, this).ShowModal();
            return null;
        }
        var file = Path.Combine(_paths.Directory, "Keyboard Shortcuts.txt");
        try
        {
            Directory.CreateDirectory(_paths.Directory);
            if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
            File.WriteAllText(file, text);
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            NextStep("Keyboard Shortcuts", $"Could not write the reference to {file}:\n{e.Message}",
                ("Show it in a window", () => new ShortcutsReferenceDialog(text, this).ShowModal()));
            return null;
        }
        var doc = OpenCodeFile(file);
        doc?.Reload();
        return doc;
    }
}
