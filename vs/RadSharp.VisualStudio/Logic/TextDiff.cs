using System;

namespace RadSharp.VisualStudio
{
    /// <summary>One replacement that turns an old text into a new one.</summary>
    public readonly struct TextReplacement
    {
        public TextReplacement(int start, int length, string text)
        {
            Start = start;
            Length = length;
            Text = text;
        }

        /// <summary>Offset in the old text.</summary>
        public int Start { get; }

        /// <summary>Characters of the old text replaced.</summary>
        public int Length { get; }

        /// <summary>The new characters.</summary>
        public string Text { get; }

        /// <summary>True when old and new text are equal.</summary>
        public bool IsEmpty => Length == 0 && Text.Length == 0;

        public string ApplyTo(string oldText) => oldText.Substring(0, Start) + Text + oldText.Substring(Start + Length);

        public override string ToString() => $"[{Start}, +{Length}) -> \"{Text}\"";
    }

    /// <summary>
    /// The smallest single replacement (common prefix / suffix removed) between two texts, so an editor applies a
    /// designer code change as one small edit: one undo unit, and the caret, markers and breakpoints elsewhere stay put.
    /// </summary>
    public static class TextDiff
    {
        public static TextReplacement MinimalReplacement(string oldText, string newText)
        {
            if (oldText == null) throw new ArgumentNullException(nameof(oldText));
            if (newText == null) throw new ArgumentNullException(nameof(newText));

            var max = Math.Min(oldText.Length, newText.Length);
            var prefix = 0;
            while (prefix < max && oldText[prefix] == newText[prefix])
                prefix++;

            var suffix = 0;
            while (suffix < max - prefix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
                suffix++;

            // Never split a CR LF pair or a surrogate pair at either boundary.
            while (prefix > 0 && (IsSplit(oldText, prefix) || IsSplit(newText, prefix)))
                prefix--;
            while (suffix > 0 && (IsSplit(oldText, oldText.Length - suffix) || IsSplit(newText, newText.Length - suffix)))
                suffix--;

            var start = prefix;
            var oldLength = oldText.Length - prefix - suffix;
            var replacement = newText.Substring(prefix, newText.Length - prefix - suffix);
            return new TextReplacement(start, oldLength, replacement);
        }

        /// <summary>True when a boundary at <paramref name="index"/> falls inside "\r\n" or a surrogate pair.</summary>
        private static bool IsSplit(string s, int index)
        {
            if (index <= 0 || index >= s.Length)
                return false;
            var before = s[index - 1];
            var after = s[index];
            return (before == '\r' && after == '\n') || (char.IsHighSurrogate(before) && char.IsLowSurrogate(after));
        }
    }
}
