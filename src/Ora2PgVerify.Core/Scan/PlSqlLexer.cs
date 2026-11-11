using System.Text;

namespace Ora2PgVerify.Scan;

public enum TokenKind
{
    Word,
    QuotedIdentifier,
    Literal,
    Number,
    Symbol,
    Hint,
}

/// <summary>A PL/SQL token. Words are upper-cased; <see cref="Text"/> of a string is its content.</summary>
public readonly record struct Token(TokenKind Kind, string Text, int Line);

/// <summary>
/// A small PL/SQL lexer: enough to keep comments and string literals from producing false findings (a
/// "CONNECT BY" inside a comment or a message text is not a construct), and to keep optimizer hints visible.
/// Handles <c>--</c> and <c>/* */</c> comments, <c>/*+ hints */</c>, doubled quotes, q-quoted literals
/// (<c>q'[...]'</c>) and quoted identifiers.
/// </summary>
public static class PlSqlLexer
{
    public static IReadOnlyList<Token> Tokenize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = new List<Token>();
        var line = 1;
        var i = 0;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && Peek(source, i + 1) == '-')
            {
                while (i < n && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && Peek(source, i + 1) == '*')
            {
                var start = line;
                var hint = Peek(source, i + 2) == '+';
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end;
                var body = source[(i + 2)..end];
                line += Count(body, '\n');
                if (hint)
                {
                    tokens.Add(new Token(TokenKind.Hint, body[1..].Trim(), start));
                }

                i = Math.Min(n, end + 2);
                continue;
            }

            if (c == '\'')
            {
                var (text, next, lines) = ReadQuoted(source, i);
                tokens.Add(new Token(TokenKind.Literal, text, line));
                line += lines;
                i = next;
                continue;
            }

            if (c == '"')
            {
                var end = source.IndexOf('"', i + 1);
                end = end < 0 ? n : end;
                tokens.Add(new Token(TokenKind.QuotedIdentifier, source[(i + 1)..end], line));
                i = Math.Min(n, end + 1);
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < n && (char.IsAsciiDigit(source[i]) || (source[i] == '.' && Peek(source, i + 1) != '.')))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, source[start..i], line));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '$' or '#'))
                {
                    i++;
                }

                var word = source[start..i].ToUpperInvariant();
                if (word is "Q" or "NQ" && Peek(source, i) == '\'' && i + 1 < n)
                {
                    var (text, next, lines) = ReadQQuoted(source, i);
                    tokens.Add(new Token(TokenKind.Literal, text, line));
                    line += lines;
                    i = next;
                    continue;
                }

                if (word == "N" && Peek(source, i) == '\'')
                {
                    continue; // national string prefix: the literal follows
                }

                tokens.Add(new Token(TokenKind.Word, word, line));
                continue;
            }

            var two = i + 1 < n ? source.Substring(i, 2) : string.Empty;
            if (two is ":=" or "=>" or "||" or "<>" or "!=" or ".." or "<=" or ">=")
            {
                tokens.Add(new Token(TokenKind.Symbol, two, line));
                i += 2;
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line));
            i++;
        }

        return tokens;
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    private static int Count(string s, char c) => s.Count(x => x == c);

    private static (string Text, int Next, int Lines) ReadQuoted(string s, int start)
    {
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == '\'')
            {
                if (Peek(s, i + 1) == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            sb.Append(s[i]);
            i++;
        }

        var text = sb.ToString();
        return (text, i, Count(text, '\n'));
    }

    private static (string Text, int Next, int Lines) ReadQQuoted(string s, int quoteIndex)
    {
        var open = s[quoteIndex + 1];
        var close = open switch
        {
            '[' => ']',
            '{' => '}',
            '(' => ')',
            '<' => '>',
            _ => open,
        };
        var end = s.IndexOf(close + "'", quoteIndex + 2, StringComparison.Ordinal);
        end = end < 0 ? s.Length : end;
        var text = s[(quoteIndex + 2)..end];
        return (text, Math.Min(s.Length, end + 2), Count(text, '\n'));
    }
}
