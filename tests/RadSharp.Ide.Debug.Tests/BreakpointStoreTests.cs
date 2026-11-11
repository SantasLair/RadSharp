using RadSharp.Ide.Debug.Protocol;

namespace RadSharp.Ide.Debug.Tests;

public class BreakpointStoreTests
{
    private static readonly string F = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bp-tests", "A.cs"));
    private static readonly string G = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bp-tests", "B.cs"));

    [Fact]
    public void Add_is_idempotent_per_line_and_normalises_blank_conditions()
    {
        var s = new BreakpointStore();
        var a = s.Add(F, 3, condition: "  ", hitCondition: "");
        var again = s.Add(F, 3, condition: "x");
        Assert.Same(a, again);
        Assert.Null(a.Condition);
        Assert.Null(a.HitCondition);
        Assert.Single(s.All);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Add(F, 0));
    }

    [Fact]
    public void Toggle_adds_then_removes()
    {
        var s = new BreakpointStore();
        Assert.True(s.Toggle(F, 5));
        Assert.NotNull(s.Find(F, 5));
        Assert.False(s.Toggle(F, 5));
        Assert.Empty(s.All);
    }

    [Fact]
    public void Enabled_filtering_conditions_and_ordering()
    {
        var s = new BreakpointStore();
        var b2 = s.Add(F, 20);
        var b1 = s.Add(F, 10);
        s.Add(G, 1);
        s.SetEnabled(b2.Id, false);
        s.SetConditions(b1.Id, "i == 3", ">=2");

        Assert.Equal([10, 20], s.GetForFile(F).Select(b => b.Line));
        Assert.Equal([10], s.GetEnabled(F).Select(b => b.Line));
        Assert.Equal("i == 3", s.Get(b1.Id)!.Condition);
        Assert.Equal(">=2", s.Get(b1.Id)!.HitCondition);
        Assert.Equal(2, s.Files.Count);
    }

    [Fact]
    public void MoveTo_refuses_an_occupied_line()
    {
        var s = new BreakpointStore();
        var a = s.Add(F, 1);
        s.Add(F, 2);
        Assert.False(s.MoveTo(a.Id, 2));
        Assert.True(s.MoveTo(a.Id, 7));
        Assert.Equal(7, s.Get(a.Id)!.Line);
    }

    [Fact]
    public void Adapter_results_are_matched_by_index_and_cleared_by_reset_but_settings_survive()
    {
        var s = new BreakpointStore();
        var a = s.Add(F, 1, condition: "c");
        var b = s.Add(F, 2);
        s.ApplyAdapterResult(F, s.GetEnabled(F), [
            new DapBreakpoint { Id = 10, Verified = true, Line = 1 },
            new DapBreakpoint { Id = 11, Verified = false, Message = "nope" },
        ]);
        Assert.True(s.Get(a.Id)!.Verified);
        Assert.False(s.Get(b.Id)!.Verified);
        Assert.Equal("nope", s.Get(b.Id)!.Message);

        // the adapter returned fewer results than sent: the missing one is unverified
        s.ApplyAdapterResult(F, s.GetEnabled(F), [new DapBreakpoint { Id = 10, Verified = true }]);
        Assert.False(s.Get(b.Id)!.Verified);

        s.RecordHits([10]);
        s.ResetAdapterState();
        Assert.Null(s.Get(a.Id)!.Verified);
        Assert.Null(s.Get(a.Id)!.AdapterId);
        Assert.Equal("c", s.Get(a.Id)!.Condition);
        Assert.Equal(1, s.Get(a.Id)!.TimesHit); // hit counts survive sessions
        s.ResetHitCounts();
        Assert.Equal(0, s.Get(a.Id)!.TimesHit);
    }

    [Fact]
    public void Changed_event_reports_each_kind()
    {
        var s = new BreakpointStore();
        var kinds = new List<BreakpointChangeKind>();
        s.Changed += (_, e) => kinds.Add(e.Kind);
        var a = s.Add(F, 1);
        s.SetEnabled(a.Id, false);
        s.Remove(a.Id);
        s.Clear();
        Assert.Equal([BreakpointChangeKind.Added, BreakpointChangeKind.Modified, BreakpointChangeKind.Removed, BreakpointChangeKind.Cleared], kinds);
    }

    [Fact]
    public void Paths_are_normalised()
    {
        var s = new BreakpointStore();
        var dir = Path.GetDirectoryName(F)!;
        s.Add(Path.Combine(dir, "sub", "..", "A.cs"), 4);
        Assert.NotNull(s.Find(F, 4));
    }

    [Fact]
    public void Exception_filters_are_kept()
    {
        var s = new BreakpointStore();
        s.SetExceptionFilters(["all", "all", "user-unhandled"]);
        Assert.Equal(2, s.ExceptionFilters.Count);
    }
}
