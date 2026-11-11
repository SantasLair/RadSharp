using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>The main toolbar: its buttons are laid out side by side, none drawn over another.</summary>
[Collection("Designer")]   // sets PlatformHost.Current
public class ToolbarTests
{
    sealed class FakeHost : IPlatformServices
    {
        public void ShowForm(Form form) => form.PerformLayout();
        public void RunModal(Form form) { form.PerformLayout(); if (form.IsModal) form.Close(); }
        public void CloseForm(Form form) { }
        public void ProcessEventsUntil(Func<bool> done) { }
        public void ShowFileDialog(FileDialogKind kind, Form? owner, FileDialogOptions options, Action<FileResult[]?> done) => done(null);
        public void PostToUiThread(Action action) => action();
        public void ShowModal(Form form, Action onClosed) { RunModal(form); onClosed(); }
    }

    [Fact]
    public void No_two_visible_toolbar_buttons_overlap()
    {
        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = new FakeHost();
        var d = new DesignerForm(null, Path.Combine(Directory.CreateTempSubdirectory("radsharp-tb-").FullName, "designer.json"));
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();
        var bar = d.FindControl("tbMain")!;
        var items = bar.Children.Where(c => c.Visible && c.Align == Align.None).ToList();
        Assert.Contains(items, c => c.Name == "btnBack");
        Assert.Contains(items, c => c.Name == "btnForward");
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
            {
                var a = items[i]; var b = items[j];
                bool overlap = a.Left < b.Left + b.Width && b.Left < a.Left + a.Width && a.Top < b.Top + b.Height && b.Top < a.Top + a.Height;
                Assert.False(overlap, $"{a.Name} ({a.Left},{a.Top},{a.Width}x{a.Height}) overlaps {b.Name} ({b.Left},{b.Top},{b.Width}x{b.Height})");
            }
    }
}
