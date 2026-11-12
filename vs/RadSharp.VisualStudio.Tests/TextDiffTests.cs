using System.IO;
using System.Text;
using Xunit;

namespace RadSharp.VisualStudio.Tests
{
    public class TextDiffTests
    {
        [Theory]
        [InlineData("", "")]
        [InlineData("abc", "abc")]
        [InlineData("", "abc")]
        [InlineData("abc", "")]
        [InlineData("abc", "abXc")]
        [InlineData("abc", "Xabc")]
        [InlineData("abc", "abcX")]
        [InlineData("aaaa", "aaaaaa")]
        [InlineData("abcabc", "abc")]
        [InlineData("line1\r\nline2\r\n", "line1\r\nnew\r\nline2\r\n")]
        [InlineData("a\r\nb", "a\nb")]
        [InlineData("a\nb", "a\r\nb")]
        [InlineData("x😀y", "x😁y")]
        public void Replacement_turns_old_into_new(string oldText, string newText)
        {
            var r = TextDiff.MinimalReplacement(oldText, newText);
            Assert.Equal(newText, r.ApplyTo(oldText));
            Assert.Equal(oldText == newText, r.IsEmpty);
        }

        [Fact]
        public void Inserted_handler_is_a_small_edit()
        {
            var oldText = "class Form1\r\n{\r\n    void A() { }\r\n}\r\n";
            var newText = "class Form1\r\n{\r\n    void A() { }\r\n\r\n    void Button1_Click(object sender, EventArgs e)\r\n    {\r\n    }\r\n}\r\n";
            var r = TextDiff.MinimalReplacement(oldText, newText);
            Assert.Equal(0, r.Length);
            Assert.Equal(newText.Length - oldText.Length, r.Text.Length);
            Assert.Equal(newText, r.ApplyTo(oldText));
        }

        [Fact]
        public void Never_splits_crlf()
        {
            // Common prefix "a\r", but the boundary may not fall between \r and \n.
            var r = TextDiff.MinimalReplacement("a\r\nb", "a\rXb");
            Assert.True(r.Start <= 1);
            Assert.Equal("a\rXb", r.ApplyTo("a\r\nb"));
        }

        [Fact]
        public void Never_splits_surrogate_pairs()
        {
            // Same high surrogate, different low surrogate: the replacement must include the whole pair.
            var r = TextDiff.MinimalReplacement("x😀y", "x😁y");
            Assert.Equal(1, r.Start);
            Assert.Equal(2, r.Length);
            Assert.Equal("😁", r.Text);
        }
    }
}
