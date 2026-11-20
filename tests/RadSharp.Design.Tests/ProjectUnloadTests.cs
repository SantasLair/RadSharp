using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using RadSharp.Design;
using RadSharp.Drawing;

namespace RadSharp.Tests;

/// <summary>
/// A rebuilt project's old load context must be collectable once its forms have moved to the new one: nothing in a
/// <see cref="DesignDocument"/> (the live form, the selection, the undo snapshots, a painted frame, the error box of a
/// control that threw while painting) may keep the old assembly alive. The helpers that touch the old context are
/// not inlined: in Debug the JIT would keep their locals alive in the test's own frame.
/// </summary>
[Collection("ProjectAssembly")]
public sealed class ProjectUnloadTests
{
    static string Form(params string[] children) => $$"""
        { "format": "radform", "version": "1.0", "generator": "RADSharp",
          "root": { "class": "Form", "name": "frmTest", "height": 300, "width": 400, "children": [{{string.Join(",", children)}}] } }
        """;

    /// <summary>Loads the project, designs a form with it, and moves the form to a second load of the project.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (DesignDocument Doc, WeakReference Old) Reload(string path, bool paint, bool select)
    {
        var first = ProjectAssembly.Load(path);
        Assert.Null(first.Error);
        var old = new WeakReference(AssemblyLoadContext.GetLoadContext(first.Assembly!));
        var doc = new DesignDocument { ProjectClasses = first.ClassSet.Find };
        doc.Load(Form(
            """{ "class": "Gauge", "name": "gauge1", "left": 8, "top": 8 }""",
            """{ "class": "TitleBar", "name": "tb", "left": 8, "top": 80 }""",
            """{ "class": "BadPainter", "name": "bad", "left": 8, "top": 150 }"""));   // BadPainter throws while it paints
        if (paint) using (FormRenderer.RenderToBitmap(doc.Form)) { }
        if (select) doc.Select(doc.Form.FindControl("gauge1"));
        doc.Snapshot();   // an undo snapshot
        doc.Form.FindControl("gauge1")!.SetBounds(50, 60, 120, 40);
        doc.NotifyChanged();
        var json = doc.Save();

        var second = ProjectAssembly.Load(path);
        doc.ReloadWith(json, second.ClassSet.Find);
        first.Dispose();
        GC.KeepAlive(second);   // the new load is still in use
        return (doc, old);
    }

    /// <summary>Collects until <paramref name="reference"/> is gone (bounded: an unload also waits on finalizers).</summary>
    internal static bool CollectUntilGone(WeakReference reference)
    {
        for (int i = 0; i < 40 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (reference.IsAlive) Thread.Sleep(25);
        }
        return !reference.IsAlive;
    }

    [Theory]
    [InlineData(false, false)]   // loaded and replaced
    [InlineData(true, true)]     // painted (a throwing painter included), with a selection
    public void The_old_load_context_is_collected_after_the_form_moves_to_a_new_load(bool paint, bool select)
    {
        var path = ProjectAssemblyTests.CopyOfSampleControls();
        var (doc, old) = Reload(path, paint, select);
        Assert.True(CollectUntilGone(old), "the old project's load context is still alive");
        Assert.Equal("SampleControls.Gauge", doc.Form.FindControl("gauge1")!.GetType().FullName);
        GC.KeepAlive(doc);
    }

    [Fact]
    public void Undoing_after_a_reload_does_not_bring_the_old_code_back()
    {
        var path = ProjectAssemblyTests.CopyOfSampleControls();
        var (doc, old) = Reload(path, paint: true, select: true);
        Assert.True(doc.Undo());
        Assert.True(CollectUntilGone(old), "undo kept the old project's load context alive");
        GC.KeepAlive(doc);
    }
}
