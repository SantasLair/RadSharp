using System.Text.Json;

namespace RadSharp.Tests;

/// <summary>Runs every conformance vector in spec/vectors (format: brainstorm, Appendix D.0).</summary>
public class ConformanceVectorTests
{
    static readonly string VectorDir = Path.Combine(AppContext.BaseDirectory, "vectors");

    public static TheoryData<string, int, string> Vectors()
    {
        var data = new TheoryData<string, int, string>();
        foreach (var path in Directory.GetFiles(VectorDir, "*.json").Order(StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            var tests = TestObjects(path);
            for (int i = 0; i < tests.Count; i++)
                data.Add(file, i, tests[i].GetProperty("name").GetString()!);
        }
        return data;
    }

    static List<JsonElement> TestObjects(string path)
    {
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray()] : [root];
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Vector(string file, int index, string name)
    {
        var test = TestObjects(Path.Combine(VectorDir, file))[index];
        var (form, warnings) = FormReader.Load(test.GetProperty("form").GetRawText());
        Assert.Empty(warnings);

        int stepNo = 0;
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            stepNo++;
            if (step.TryGetProperty("do", out var action))
                Do(form, action.GetString()!, step);
            if (step.TryGetProperty("expect", out var expect))
                Expect(form, expect, $"{name}, step {stepNo}");
        }
    }

    static void Do(Form form, string action, JsonElement step)
    {
        switch (action)
        {
            case "resize":
                form.Resize(step.GetProperty("width").GetInt32(), step.GetProperty("height").GetInt32());
                break;
            case "set":
                var target = Find(form, step.GetProperty("control").GetString()!);
                Assert.True(target.ReadProperty(step.GetProperty("property").GetString()!, step.GetProperty("value")));
                break;
            case "dragSplitter":
                var splitter = Assert.IsType<Splitter>(Find(form, step.GetProperty("control").GetString()!));
                splitter.DragBy(step.GetProperty("delta").GetInt32());
                break;
            default:
                Assert.Fail($"Unknown action \"{action}\".");
                break;
        }
        form.PerformLayout();
    }

    static void Expect(Form form, JsonElement expect, string where)
    {
        foreach (var entry in expect.EnumerateObject())
        {
            var a = entry.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var expected = new Rect(a[0], a[1], a[2], a[3]);
            var actual = Find(form, entry.Name).Bounds;
            Assert.True(expected == actual, $"{where}: {entry.Name} expected ({expected}), got ({actual})");
        }
    }

    static Control Find(Form form, string name) =>
        form.FindControl(name) ?? throw new Xunit.Sdk.XunitException($"No control named \"{name}\".");
}
