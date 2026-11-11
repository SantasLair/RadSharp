using System.Text;
using RadSharp.CodeEditing;

namespace RadSharp.CodeEditing.Tests;

public class TextBufferTests
{
    [Fact]
    public void Lines_and_offsets_round_trip()
    {
        var b = new TextBuffer("alpha\nbeta\n\ngamma");
        var s = b.Current;
        Assert.Equal(4, s.LineCount);
        Assert.Equal("beta", s.GetLineText(1));
        Assert.Equal("", s.GetLineText(2));
        Assert.Equal(6, s.GetLineStart(1));
        Assert.Equal(new TextPosition(3, 2), s.GetPosition(14));
        Assert.Equal(14, s.GetOffset(3, 2));
        Assert.Equal(1, s.GetLineFromOffset(6));
        Assert.Equal(0, s.GetLineFromOffset(5));
        Assert.Equal('g', s[12]);
    }

    [Fact]
    public void Crlf_is_detected_and_preserved()
    {
        var b = new TextBuffer("one\r\ntwo\r\nthree");
        Assert.Equal("\r\n", b.NewLine);
        var s = b.Current;
        Assert.Equal(3, s.LineCount);
        Assert.Equal("two", s.GetLineText(1));
        Assert.Equal(3, s.GetLineEnd(0));
        Assert.Equal(5, s.GetLineStart(1));
        b.Insert(s.GetLineEnd(1), b.NewLine + "inserted");
        Assert.Equal("one\r\ntwo\r\ninserted\r\nthree", b.GetText());

        var lf = new TextBuffer("a\nb");
        Assert.Equal("\n", lf.NewLine);
        var mixed = new TextBuffer("a\r\nb\nc");
        Assert.Equal("a\r\nb\nc", mixed.GetText());
        Assert.Equal("b", mixed.Current.GetLineText(1));
    }

    [Fact]
    public void Snapshots_are_immutable_and_offsets_translate_forward()
    {
        var b = new TextBuffer("hello world");
        var v0 = b.Current;
        b.Insert(0, ">> ");
        b.Replace(9, 5, "WORLD!");
        Assert.Equal("hello world", v0.GetText());
        Assert.Equal(">> hello WORLD!", b.GetText());
        Assert.Equal(2, b.Version);
        Assert.Equal(9, v0.TranslateOffset(6, b.Current, PointTrackingMode.Negative));   // "w" moved right by 3, then replaced: before the new text
        Assert.Equal(15, v0.TranslateOffset(6, b.Current));                              // ...or after it
        Assert.Equal(3, v0.TranslateOffset(0, b.Current, PointTrackingMode.Positive));
        Assert.Equal(0, v0.TranslateOffset(0, b.Current, PointTrackingMode.Negative));
        Assert.Equal(15, v0.TranslateOffset(11, b.Current));
    }

    [Fact]
    public void Multiple_edits_apply_against_the_same_snapshot()
    {
        var b = new TextBuffer("a b c");
        var args = b.Apply([new TextEdit(4, 1, "C"), new TextEdit(0, 1, "AA"), TextEdit.Insert(2, "_")])!;
        Assert.Equal("AA _b C", b.GetText());
        Assert.Equal(3, args.Changes.Count);
        Assert.Equal(new TextChange(0, "a", 0, "AA"), args.Changes[0]);
        Assert.Equal(new TextChange(4, "c", 6, "C"), args.Changes[2]);
        Assert.Throws<ArgumentException>(() => b.Apply([new TextEdit(0, 3, "x"), new TextEdit(2, 1, "y")]));
    }

    [Fact]
    public void Typing_coalesces_into_one_undo_step()
    {
        var b = new TextBuffer("");
        foreach (var c in "hello") b.Apply(TextEdit.Insert(b.Length, c.ToString()), UndoCoalescing.Typing);
        b.BreakUndoCoalescing();
        foreach (var c in " you") b.Apply(TextEdit.Insert(b.Length, c.ToString()), UndoCoalescing.Typing);
        Assert.Equal("hello you", b.GetText());
        Assert.True(b.Undo());
        Assert.Equal("hello", b.GetText());
        Assert.True(b.Undo());
        Assert.Equal("", b.GetText());
        Assert.False(b.Undo());
        Assert.True(b.Redo());
        Assert.True(b.Redo());
        Assert.Equal("hello you", b.GetText());
    }

    [Fact]
    public void Backspaces_coalesce_and_multi_caret_typing_merges()
    {
        var b = new TextBuffer("abcdef");
        for (int i = 0; i < 3; i++) b.Apply(TextEdit.Delete(b.Length - 1, 1), UndoCoalescing.DeleteBackward);
        Assert.Equal("abc", b.GetText());
        b.Undo();
        Assert.Equal("abcdef", b.GetText());

        var m = new TextBuffer("x\ny");
        m.Apply([TextEdit.Insert(1, "1"), TextEdit.Insert(3, "1")], UndoCoalescing.Typing);
        m.Apply([TextEdit.Insert(2, "2"), TextEdit.Insert(5, "2")], UndoCoalescing.Typing);
        Assert.Equal("x12\ny12", m.GetText());
        Assert.True(m.Undo());
        Assert.Equal("x\ny", m.GetText());
    }

    [Fact]
    public void Modified_state_follows_undo_and_save()
    {
        var b = new TextBuffer("abc");
        Assert.False(b.IsModified);
        b.Insert(3, "d");
        Assert.True(b.IsModified);
        b.Undo();
        Assert.False(b.IsModified);
        b.Redo();
        b.MarkClean();
        Assert.False(b.IsModified);
        b.Undo();
        Assert.True(b.IsModified);
        b.Insert(0, "z");                     // the saved state is now unreachable
        Assert.True(b.IsModified);
    }

    [Fact]
    public void Transactions_group_edits()
    {
        var b = new TextBuffer("abc");
        using (b.BeginTransaction())
        {
            b.Insert(0, "1");
            b.Insert(4, "2");
        }
        Assert.Equal("1abc2", b.GetText());
        b.Undo();
        Assert.Equal("abc", b.GetText());
    }

    [Fact]
    public void Undo_redo_round_trips_1000_random_edits()
    {
        var rng = new Random(20261111);
        var initial = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i} with some text"));
        var b = new TextBuffer(initial);
        var model = new StringBuilder(initial);
        var history = new List<string> { initial };
        const string alphabet = "abcdefXYZ \n\t{}();\r";
        for (int i = 0; i < 1000; i++)
        {
            int len = model.Length;
            int start = rng.Next(len + 1);
            int del = rng.Next(4) == 0 ? 0 : rng.Next(Math.Min(20, len - start) + 1);
            int insLen = rng.Next(3) == 0 ? 0 : rng.Next(1, 12);
            var ins = new string(Enumerable.Range(0, insLen).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            var kind = (UndoCoalescing)rng.Next(4);
            if (rng.Next(3) == 0)
            {
                // A multi-edit change (several carets).
                int second = Math.Min(len, start + del + rng.Next(30));
                int del2 = rng.Next(Math.Min(5, len - second) + 1);
                if (second >= start + del)
                {
                    b.Apply([new TextEdit(start, del, ins), new TextEdit(second, del2, "Q")], kind);
                    model.Remove(second, del2).Insert(second, "Q");
                    model.Remove(start, del).Insert(start, ins);
                    history.Add(model.ToString());
                    Assert.Equal(model.ToString(), b.GetText());
                    continue;
                }
            }
            b.Apply(new TextEdit(start, del, ins), kind);
            model.Remove(start, del).Insert(start, ins);
            if (del > 0 || ins.Length > 0) history.Add(model.ToString());
            Assert.Equal(model.ToString(), b.GetText());
        }
        var final = b.GetText();
        // The line index agrees with a plain split after all that.
        var lines = final.Split('\n');
        Assert.Equal(lines.Length, b.LineCount);
        for (int i = 0; i < lines.Length; i += 7) Assert.Equal(lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i], b.Current.GetLineText(i));

        int undos = 0;
        while (b.Undo()) undos++;
        Assert.Equal(initial, b.GetText());
        Assert.True(undos > 0);
        int redos = 0;
        while (b.Redo()) redos++;
        Assert.Equal(undos, redos);
        Assert.Equal(final, b.GetText());
        Assert.Contains(final, history);
    }

    [Fact]
    public async Task Snapshots_read_safely_on_another_thread()
    {
        var b = new TextBuffer(string.Join("\n", Enumerable.Range(0, 5000).Select(i => "row " + i)));
        var snap = b.Current;
        var expected = snap.GetText();
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++) Assert.Equal(expected, snap.GetText());
        });
        for (int i = 0; i < 2000; i++) b.Insert(b.Length / 2, "x\n");
        await reader;
        Assert.Equal(expected, snap.GetText());
    }
}
