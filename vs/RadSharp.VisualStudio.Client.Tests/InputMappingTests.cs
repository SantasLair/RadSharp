using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Xunit;
using P = RadSharp.DesignHost.Protocol;

namespace RadSharp.VisualStudio.Client.Tests;

/// <summary>The client has no reference to RADSharp, so these tests read RADSharp's source to keep the name tables honest.</summary>
public class InputMappingTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RadSharp.slnx"))) return dir.FullName;
        throw new InvalidOperationException("RadSharp.slnx not found above " + AppDomain.CurrentDomain.BaseDirectory);
    }

    /// <summary>Member names of <c>public enum {name}</c> in src/RadSharp.Core/Input/*.cs.</summary>
    private static HashSet<string> EnumMembers(string name)
    {
        var dir = Path.Combine(RepoRoot(), "src", "RadSharp.Core", "Input");
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var m = Regex.Match(text, @"public\s+enum\s+" + name + @"\s*\{(?<body>[^}]*)\}");
            if (!m.Success) continue;
            var body = Regex.Replace(m.Groups["body"].Value, @"//[^\n]*", "");
            body = Regex.Replace(body, @"/\*.*?\*/", "", RegexOptions.Singleline);
            body = Regex.Replace(body, @"\[[^\]]*\]", "");
            return new HashSet<string>(body.Split(',')
                .Select(p => p.Split('=')[0].Trim())
                .Where(p => p.Length > 0));
        }
        throw new InvalidOperationException("enum " + name + " not found in " + dir);
    }

    [Fact]
    public void Every_key_name_exists_in_the_RadSharp_Key_enum()
    {
        var keys = EnumMembers("Key");
        Assert.Contains("F12", keys); // the parse found the real enum
        var missing = InputMapping.KeyNames.Values.Distinct().Where(n => !keys.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "Not in RADSharp's Key enum: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_RadSharp_key_except_Unknown_has_a_WPF_key()
    {
        var mapped = new HashSet<string>(InputMapping.KeyNames.Values);
        var unmapped = EnumMembers("Key").Where(k => k != "Unknown" && !mapped.Contains(k)).ToList();
        Assert.True(unmapped.Count == 0, "RADSharp keys with no WPF mapping: " + string.Join(", ", unmapped));
    }

    [Fact]
    public void Every_RadSharp_cursor_has_a_WPF_cursor_and_no_extra_names()
    {
        var cursors = EnumMembers("Cursor");
        Assert.Equal(cursors.OrderBy(c => c), InputMapping.CursorNames.Keys.OrderBy(c => c));
    }

    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.Z, "Z")]
    [InlineData(Key.D0, "D0")]
    [InlineData(Key.NumPad7, "D7")]
    [InlineData(Key.F10, "F10")]
    [InlineData(Key.Return, "Enter")]
    [InlineData(Key.Back, "Backspace")]
    [InlineData(Key.Prior, "PageUp")]
    [InlineData(Key.Next, "PageDown")]
    [InlineData(Key.OemPlus, "Equals")]
    [InlineData(Key.Oem3, "Grave")]
    [InlineData(Key.Oem5, "Backslash")]
    [InlineData(Key.Apps, "Menu")]
    [InlineData(Key.LeftAlt, "Alt")]
    [InlineData(Key.RightAlt, "Alt")]
    public void Maps_WPF_keys(Key key, string name) => Assert.Equal(name, InputMapping.MapKey(key));

    [Theory]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.LWin)]
    [InlineData(Key.CapsLock)]
    [InlineData(Key.System)]
    [InlineData(Key.None)]
    public void Drops_keys_RadSharp_does_not_have(Key key) => Assert.Null(InputMapping.MapKey(key));

    [Fact]
    public void Maps_modifiers_and_buttons()
    {
        Assert.Equal(P.Modifiers.None, InputMapping.MapModifiers(ModifierKeys.None));
        Assert.Equal(P.Modifiers.Shift | P.Modifiers.Ctrl | P.Modifiers.Alt | P.Modifiers.Meta,
            InputMapping.MapModifiers(ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows));
        Assert.Equal(P.MouseButton.Middle, InputMapping.MapButton(MouseButton.Middle));
        Assert.Null(InputMapping.MapButton(MouseButton.XButton1));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("é", true)]
    [InlineData("\b", false)]
    [InlineData("\r", false)]
    [InlineData("\u0001", false)] // Ctrl+A
    [InlineData("", false)]
    public void Only_printable_text_is_sent(string text, bool printable) => Assert.Equal(printable, InputMapping.IsPrintable(text));
}
