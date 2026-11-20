using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.DesignHost.Tests;

/// <summary>
/// After a hot reload the host must not hold the old project's load context (surface, inspector, palette and a
/// selection all showed its controls). Helpers that touch the old context are not inlined: in Debug the JIT would keep
/// their locals alive in the test's own frame.
/// </summary>
[Collection("DesignHost")]
public class ProjectUnloadTests
{
    const string FormText = """
        { "format": "radform", "version": "1.0", "generator": "RADSharp",
          "root": { "class": "Form", "name": "frmProject", "height": 300, "width": 400, "children": [
            { "class": "Gauge", "name": "gauge1", "left": 8, "top": 8, "value": 7 },
            { "class": "TitleBar", "name": "tb", "left": 8, "top": 80 } ] } }
        """;

    static string BuiltDirectory => Path.GetDirectoryName(typeof(ProjectUnloadTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "SampleControls").Value!)!;

    /// <summary>An identity of the load context of the document's Gauge (a number: it does not keep the context alive).</summary>
    static int ContextId(HostClient client) => client.Ui(() =>
        RuntimeHelpers.GetHashCode(AssemblyLoadContext.GetLoadContext(client.Server!.Document(1)!.Doc.Form.FindControl("gauge1")!.GetType().Assembly)));

    /// <summary>Opens the form with the project and shows some of its panes; returns its load context (weakly).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Old, string Dll, int Id) Open(HostClient client, bool panes, bool select)
    {
        var dir = TestFiles.TempDir();
        foreach (var file in Directory.EnumerateFiles(BuiltDirectory)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        var form = Path.Combine(dir, "F.radform.json");
        File.WriteAllText(form, FormText);
        var dll = Path.Combine(dir, "SampleControls.dll");
        client.Ui(() =>
        {
            client.Server!.ProjectSettleTime = TimeSpan.FromMilliseconds(100);
            client.Server.ProjectCheckInterval = TimeSpan.FromMilliseconds(50);
        });
        client.Send(new P.SetProjectAssembly { DocId = 1, Path = dll });
        var open = client.Open(1, form);
        client.Show(open.Surface, 800, 600);
        if (panes)
        {
            client.Show(open.Inspector, 300, 600);
            client.Show(open.Palette, 240, 600);
        }
        client.Settle();
        if (select)
        {
            var (x, y) = client.ControlPoint(1, "gauge1");
            client.Click(open.Surface, x, y);
            client.Settle();
        }
        var old = client.Ui(() => new WeakReference(AssemblyLoadContext.GetLoadContext(client.Server!.Document(1)!.Doc.Form.FindControl("gauge1")!.GetType().Assembly)));
        return (old, dll, ContextId(client));
    }

    /// <summary>Rebuilds the project and waits for the host to have moved the form to the new code.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Rebuild(HostClient client, string dll, int oldId)
    {
        File.WriteAllBytes(dll, File.ReadAllBytes(dll));
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (ContextId(client) == oldId && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.NotEqual(oldId, ContextId(client));
        client.Settle();
    }

    [Theory]
    [InlineData(false, false)]   // the surface only
    [InlineData(true, false)]    // with the inspector and the palette
    [InlineData(true, true)]     // and a selected control
    public void The_old_load_context_is_collected_after_a_hot_reload(bool panes, bool select)
    {
        using var client = new HostClient();
        client.Send(new P.Hello { ProtocolVersion = P.ProtocolInfo.Version, ClientName = "tests" });
        client.Expect<P.HelloAck>();
        var (old, dll, id) = Open(client, panes, select);
        Rebuild(client, dll, id);
        Assert.True(CollectUntilGone(client, old), "the old project's load context is still alive after the hot reload");
    }

    /// <summary>Collects on the test thread and on the host's UI thread until the context is gone (bounded).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CollectUntilGone(HostClient client, WeakReference old)
    {
        for (int i = 0; i < 40 && old.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            client.Ui(() => { GC.Collect(); return 0; });
            if (old.IsAlive) Thread.Sleep(30);
        }
        return !old.IsAlive;
    }
}
