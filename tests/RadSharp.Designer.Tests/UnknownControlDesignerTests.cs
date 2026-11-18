using RadSharp.Design.Code;
using RadSharp.Designer;

namespace RadSharp.Tests;

/// <summary>
/// A control class the designer doesn't know (the app defines it in its own code) survives open → save in the real
/// designer, and its placeholder can be selected, moved and resized on the surface.
/// </summary>
[Collection("PlatformHost")]   // each test installs its own PlatformHost.Current (process-wide)
public class UnknownControlDesignerTests
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

    static string TempDir() => Directory.CreateTempSubdirectory("radsharp-unknown-").FullName;

    const string FormText = """
        {
          "format": "radform",
          "version": "1.0",
          "generator": "RADSharp",
          "root": {
            "class": "Form",
            "name": "frmMain",
            "caption": "Keeps",
            "height": 320,
            "width": 480,
            "children": [
              {
                "class": "TallyCounter",
                "name": "cntTotal",
                "count": 3,
                "left": 24,
                "onCountChanged": "cntTotal_CountChanged",
                "top": 24,
                "width": 160,
                "children": [
                  {
                    "class": "Button",
                    "name": "btnReset",
                    "caption": "Reset"
                  }
                ]
              },
              {
                "class": "Button",
                "name": "btnOk",
                "caption": "OK",
                "left": 24,
                "top": 200
              }
            ]
          }
        }

        """;

    [Fact]
    public void A_form_with_an_unknown_control_survives_open_and_save_and_its_placeholder_can_be_moved_and_resized()
    {
        var project = RadProject.Create(TempDir(), "Keeps");
        var formFile = project.FormFiles.Single();
        File.WriteAllText(formFile, FormText);

        Shortcut.CommandModifier = KeyModifiers.Ctrl;
        PlatformHost.Current = new FakeHost();
        var d = new DesignerForm(project.ProjectFile, Path.Combine(TempDir(), "designer.json"), new DesignerOptions { ProjectControls = false });
        d.SetBounds(0, 0, 1220, 780);
        d.PerformLayout();

        var open = d.ActiveForm!;
        var doc = open.Document;
        var placeholder = Assert.IsType<UnknownControl>(doc.Form.FindControl("cntTotal"));
        Assert.Equal("TallyCounter", placeholder.ClassName);
        Assert.Contains(d.OutputLines, l => l.Contains("\"TallyCounter\"") && l.Contains("kept as a placeholder"));
        Assert.False(doc.Modified);

        // Open → save writes the file back byte for byte.
        d.Save(open);
        Assert.Equal(FormText, File.ReadAllText(formFile));

        // Select it, move it and resize it with the mouse.
        var surface = open.Surface;
        (int X, int Y) At(int x, int y)
        {
            var (sx, sy) = surface.DesignToScreen(x, y);
            return (surface.AbsoluteLeft + sx, surface.AbsoluteTop + sy);
        }
        void Drag((int X, int Y) from, (int X, int Y) to)
        {
            var a = At(from.X, from.Y);
            var b = At(to.X, to.Y);
            d.Simulate.Press(a.X, a.Y);
            for (int i = 1; i <= 4; i++) d.ProcessMouseMove(a.X + (b.X - a.X) * i / 4, a.Y + (b.Y - a.Y) * i / 4, KeyModifiers.None);
            d.Simulate.Release(b.X, b.Y);
        }
        var click = At(60, 40);
        d.Simulate.Click(click.X, click.Y);
        Assert.Same(placeholder, doc.Primary);

        Drag((60, 40), (76, 48));
        Assert.Equal(new Rect(40, 32, 160, UnknownControl.DefaultHeight), placeholder.Bounds);
        var corner = (placeholder.Left + placeholder.Width, placeholder.Top + placeholder.Height);
        Drag(corner, (corner.Item1 + 16, corner.Item2 + 24));
        Assert.Equal(new Rect(40, 32, 176, UnknownControl.DefaultHeight + 24), placeholder.Bounds);
        Assert.True(doc.Modified);

        d.Save(open);
        var expected = FormText
            .Replace("\"left\": 24,\n        \"onCountChanged\"", "\"left\": 40,\n        \"onCountChanged\"")
            .Replace("\"top\": 24,\n        \"width\": 160,", "\"top\": 32,\n        \"width\": 176,")
            .Replace("\"count\": 3,", $"\"count\": 3,\n        \"height\": {UnknownControl.DefaultHeight + 24},");
        Assert.Equal(expected, File.ReadAllText(formFile));

        // Undo goes back to the file as it was, placeholder included.
        Assert.True(doc.Undo());
        Assert.True(doc.Undo());
        d.Save(open);
        Assert.Equal(FormText, File.ReadAllText(formFile));
        Assert.IsType<UnknownControl>(doc.Form.FindControl("cntTotal"));

        // Names inside the placeholder's children stay taken.
        Assert.False(doc.Rename(doc.Form.FindControl("btnOk")!, "btnReset"));
        Assert.Equal("btnReset1", doc.UniqueName("btnReset"));
    }
}
