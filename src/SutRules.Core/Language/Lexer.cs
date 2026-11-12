using System.Globalization;
using System.Text;

namespace SutRules.Language;

public enum TokenKind
{
    Identifier,
    String,
    Integer,
    Decimal,
    LeftBrace,
    RightBrace,
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket,
    Comma,
    Colon,
    Dot,
    Assign,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Plus,
    Minus,
    Star,
    Slash,
    Coalesce,
    End,
}

/// <summary>A position in a rule file, 1-based, for diagnostics a rule author can jump to.</summary>
public readonly record struct SourcePos(int Line, int Column)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Line}:{Column}");
}

public sealed record Token(TokenKind Kind, string Text, object? Value, SourcePos Pos);

/// <summary>
/// Hand-written lexer. Comments start with <c>#</c>. Strings are double-quoted and single-line; only <c>\"</c> and
/// <c>\\</c> are escapes, every other backslash is literal, so regular expressions read the way they are written
/// (<c>"^\d{6}$"</c>) instead of doubling every backslash.
/// </summary>
public static class Lexer
{
    public static IReadOnlyList<Token> Tokenize(string source, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var tokens = new List<Token>();
        int i = 0, line = 1, lineStart = 0;

        while (i < source.Length)
        {
            var c = source[i];
            var pos = new SourcePos(line, i - lineStart + 1);

            if (c == '\n')
            {
                i++;
                line++;
                lineStart = i;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '#')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                var text = source[start..i];
                tokens.Add(new Token(TokenKind.Identifier, text, text, pos));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < source.Length && char.IsAsciiDigit(source[i]))
                {
                    i++;
                }

                if (i + 1 < source.Length && source[i] == '.' && char.IsAsciiDigit(source[i + 1]))
                {
                    i++;
                    while (i < source.Length && char.IsAsciiDigit(source[i]))
                    {
                        i++;
                    }

                    var text = source[start..i];
                    tokens.Add(new Token(TokenKind.Decimal, text, decimal.Parse(text, CultureInfo.InvariantCulture), pos));
                }
                else
                {
                    var text = source[start..i];
                    if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    {
                        tokens.Add(new Token(TokenKind.Integer, text, value, pos));
                    }
                    else
                    {
                        diagnostics.Error(pos, "SR001", $"Integer literal '{text}' is out of range.");
                        tokens.Add(new Token(TokenKind.Integer, text, 0, pos));
                    }
                }

                continue;
            }

            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                var closed = false;
                while (i < source.Length && source[i] != '\n')
                {
                    var ch = source[i];
                    if (ch == '"')
                    {
                        closed = true;
                        i++;
                        break;
                    }

                    if (ch == '\\' && i + 1 < source.Length && source[i + 1] is '"' or '\\')
                    {
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }

                    sb.Append(ch);
                    i++;
                }

                if (!closed)
                {
                    diagnostics.Error(pos, "SR002", "Unterminated string literal (strings cannot span lines).");
                }

                var value = sb.ToString();
                tokens.Add(new Token(TokenKind.String, value, value, pos));
                continue;
            }

            var (kind, length) = c switch
            {
                '{' => (TokenKind.LeftBrace, 1),
                '}' => (TokenKind.RightBrace, 1),
                '(' => (TokenKind.LeftParen, 1),
                ')' => (TokenKind.RightParen, 1),
                '[' => (TokenKind.LeftBracket, 1),
                ']' => (TokenKind.RightBracket, 1),
                ',' => (TokenKind.Comma, 1),
                ':' => (TokenKind.Colon, 1),
                '.' => (TokenKind.Dot, 1),
                '+' => (TokenKind.Plus, 1),
                '-' => (TokenKind.Minus, 1),
                '*' => (TokenKind.Star, 1),
                '/' => (TokenKind.Slash, 1),
                '=' when Peek(source, i) == '=' => (TokenKind.Equal, 2),
                '=' => (TokenKind.Assign, 1),
                '!' when Peek(source, i) == '=' => (TokenKind.NotEqual, 2),
                '<' when Peek(source, i) == '=' => (TokenKind.LessOrEqual, 2),
                '<' => (TokenKind.Less, 1),
                '>' when Peek(source, i) == '=' => (TokenKind.GreaterOrEqual, 2),
                '>' => (TokenKind.Greater, 1),
                '?' when Peek(source, i) == '?' => (TokenKind.Coalesce, 2),
                _ => (TokenKind.End, 0),
            };

            if (length == 0)
            {
                diagnostics.Error(pos, "SR003", $"Unexpected character '{c}'.");
                i++;
                continue;
            }

            tokens.Add(new Token(kind, source.Substring(i, length), null, pos));
            i += length;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, null, new SourcePos(line, i - lineStart + 1)));
        return tokens;
    }

    private static char Peek(string s, int i) => i + 1 < s.Length ? s[i + 1] : '\0';
}
