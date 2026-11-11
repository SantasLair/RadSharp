using RadSharp.CodeEditing;

namespace RadSharp.Designer;

/// <summary>
/// Colours JSON (a <c>.radform.json</c> opened as text): property names, strings, numbers, <c>true</c>/<c>false</c>/<c>null</c>
/// and punctuation. Line by line and stateless, which JSON allows (strings can't span lines).
/// </summary>
public sealed class JsonTokenSource : ITokenSource
{
    public event EventHandler<SourceChangedEventArgs>? Changed { add { } remove { } }

    public IReadOnlyList<TokenSpan>? GetTokens(TextSnapshot snapshot, int line) => Tokenize(snapshot.GetLineText(line));

    public static IReadOnlyList<TokenSpan> Tokenize(string text)
    {
        var spans = new List<TokenSpan>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"')
            {
                int start = i++;
                while (i < text.Length && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, text.Length);
                int after = i;
                while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                bool key = after < text.Length && text[after] == ':';
                spans.Add(new TokenSpan(start, i - start, key ? Classifications.PropertyName : Classifications.String));
            }
            else if (c == '-' || char.IsDigit(c))
            {
                int start = i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                spans.Add(new TokenSpan(start, i - start, Classifications.Number));
            }
            else if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && char.IsLetter(text[i])) i++;
                var word = text[start..i];
                if (word is "true" or "false" or "null") spans.Add(new TokenSpan(start, i - start, Classifications.Keyword));
            }
            else
            {
                if (c is '{' or '}' or '[' or ']' or ':' or ',') spans.Add(new TokenSpan(i, 1, Classifications.Punctuation));
                i++;
            }
        }
        return spans;
    }
}
