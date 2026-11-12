namespace SutRules.Language;

/// <summary>
/// Recursive-descent parser for one rule file. Precedence, lowest first: <c>if</c>, <c>or</c>, <c>and</c>,
/// <c>not</c>, comparisons (non-associative), <c>??</c>, <c>+ -</c>, <c>* /</c>, unary minus, member access.
/// A syntax error stops the file at the first problem: later errors after a parse failure are mostly noise.
/// </summary>
public sealed class Parser
{
    /// <summary>Words that are operators or literals in expressions and therefore cannot name a variable.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "or", "not", "in", "if", "then", "else", "true", "false", "null", "where",
    };

    /// <summary>Functions that take the <c>x in source where filter : projection</c> form.</summary>
    public static readonly IReadOnlySet<string> Comprehensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "any", "all", "count", "sum", "min", "max", "first",
    };

    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _index;

    private Parser(IReadOnlyList<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    private Token Current => _tokens[_index];

    /// <summary>Parses a rule file; returns null and fills <paramref name="diagnostics"/> when it is malformed.</summary>
    public static RuleDecl? ParseRule(string source, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var tokens = Lexer.Tokenize(source, diagnostics);
        if (diagnostics.HasErrors)
        {
            return null;
        }

        var parser = new Parser(tokens, diagnostics);
        try
        {
            var rule = parser.Rule();
            parser.Expect(TokenKind.End, "end of file");
            return rule;
        }
        catch (SyntaxError e)
        {
            diagnostics.Error(e.Pos, "SR010", e.Message);
            return null;
        }
    }

    /// <summary>Parses a single expression (used by tests and the <c>explain</c> tooling).</summary>
    public static Expr? ParseExpression(string source, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var tokens = Lexer.Tokenize(source, diagnostics);
        if (diagnostics.HasErrors)
        {
            return null;
        }

        var parser = new Parser(tokens, diagnostics);
        try
        {
            var expr = parser.Expression();
            parser.Expect(TokenKind.End, "end of expression");
            return expr;
        }
        catch (SyntaxError e)
        {
            diagnostics.Error(e.Pos, "SR010", e.Message);
            return null;
        }
    }

    private RuleDecl Rule()
    {
        var pos = Current.Pos;
        Keyword("rule");
        var id = String("rule id");
        Expect(TokenKind.LeftBrace, "'{'");

        string? category = null, basis = null;
        Bilingual? title = null, reason = null;
        FixDecl? fix = null;
        var parameters = new List<ParamDecl>();
        var versions = new List<VersionBlock>();
        var checks = new List<CheckBlock>();

        while (Current.Kind != TokenKind.RightBrace)
        {
            var itemPos = Current.Pos;
            var word = Identifier("a rule item (category, title, basis, param, version, check, reason, fix)");
            switch (word)
            {
                case "category":
                    category = Once(category, Identifier("category name"), "category", itemPos);
                    break;
                case "title":
                    title = Once(title, BilingualText(), "title", itemPos);
                    break;
                case "basis":
                    basis = Once(basis, String("basis text"), "basis", itemPos);
                    break;
                case "param":
                    parameters.Add(new ParamDecl(Name("parameter name"), Identifier("parameter type"), itemPos));
                    break;
                case "version":
                    versions.Add(Version(itemPos));
                    break;
                case "check":
                    checks.Add(Check(itemPos));
                    break;
                case "reason":
                    reason = Once(reason, BilingualText(), "reason", itemPos);
                    break;
                case "fix":
                    fix = Once(fix, Fix(itemPos), "fix", itemPos);
                    break;
                default:
                    throw new SyntaxError(itemPos, $"Unknown rule item '{word}'.");
            }
        }

        Expect(TokenKind.RightBrace, "'}'");
        return new RuleDecl(id, category, title, basis, parameters, versions, checks, reason, fix, pos);
    }

    private static T Once<T>(T? existing, T value, string item, SourcePos pos) =>
        existing is null ? value : throw new SyntaxError(pos, $"'{item}' is declared twice.");

    private VersionBlock Version(SourcePos pos)
    {
        var sut = String("SUT version id");
        Expect(TokenKind.LeftBrace, "'{'");
        string? severity = null;
        var retired = false;
        var assignments = new List<ParamAssignment>();
        while (Current.Kind != TokenKind.RightBrace)
        {
            var itemPos = Current.Pos;
            var word = Identifier("severity, retired or a parameter assignment");
            if (word == "severity")
            {
                severity = Once(severity, Identifier("severity (info, warning, error)"), "severity", itemPos);
            }
            else if (word == "retired")
            {
                retired = true;
            }
            else
            {
                Expect(TokenKind.Assign, "'='");
                assignments.Add(new ParamAssignment(word, Expression(), itemPos));
            }
        }

        Expect(TokenKind.RightBrace, "'}'");
        return new VersionBlock(sut, severity, retired, assignments, pos);
    }

    private CheckBlock Check(SourcePos pos)
    {
        string? variable = null;
        Expr? source = null;
        var mode = Identifier("'invoice' or 'each'");
        if (mode == "each")
        {
            variable = Name("loop variable");
            Keyword("in");
            source = Expression();
        }
        else if (mode != "invoice")
        {
            throw new SyntaxError(pos, $"Expected 'check invoice' or 'check each <name> in <list>', found 'check {mode}'.");
        }

        Expect(TokenKind.LeftBrace, "'{'");
        var statements = new List<Statement>();
        while (Current.Kind != TokenKind.RightBrace)
        {
            statements.Add(Statement());
        }

        Expect(TokenKind.RightBrace, "'}'");
        return new CheckBlock(variable, source, statements, pos);
    }

    private Statement Statement()
    {
        var pos = Current.Pos;
        var word = Identifier("let, require or when");
        switch (word)
        {
            case "let":
                var name = Name("variable name");
                Expect(TokenKind.Assign, "'='");
                return new LetStatement(name, Expression(), pos);
            case "require":
                return new RequireStatement(Expression(), pos);
            case "when":
                var condition = Expression();
                Keyword("report");
                Expr? at = null, expected = null, actual = null;
                if (IsKeyword("at"))
                {
                    _index++;
                    at = Expression();
                }

                if (IsKeyword("expected"))
                {
                    _index++;
                    expected = Expression();
                }

                if (IsKeyword("actual"))
                {
                    _index++;
                    actual = Expression();
                }

                return new WhenStatement(condition, at, expected, actual, pos);
            default:
                throw new SyntaxError(pos, $"Expected let, require or when, found '{word}'.");
        }
    }

    private FixDecl Fix(SourcePos pos)
    {
        var action = Identifier("fix action");
        string? valueSource = null;
        if (IsKeyword("value"))
        {
            _index++;
            var valuePos = Current.Pos;
            valueSource = Identifier("'expected' or 'actual'");
            if (valueSource is not ("expected" or "actual"))
            {
                throw new SyntaxError(valuePos, "A fix value is either 'expected' or 'actual'.");
            }
        }

        return new FixDecl(action, valueSource, BilingualText(), pos);
    }

    private Bilingual BilingualText()
    {
        Keyword("tr");
        var tr = String("Turkish text");
        Keyword("en");
        var en = String("English text");
        return new Bilingual(tr, en);
    }

    // ---- expressions ------------------------------------------------------------------------------------------

    private Expr Expression()
    {
        if (IsKeyword("if"))
        {
            var pos = Current.Pos;
            _index++;
            var condition = Expression();
            Keyword("then");
            var then = Expression();
            Keyword("else");
            var otherwise = Expression();
            return new IfExpr(condition, then, otherwise, pos);
        }

        return Or();
    }

    private Expr Or()
    {
        var left = And();
        while (IsKeyword("or"))
        {
            var pos = Current.Pos;
            _index++;
            left = new BinaryExpr("or", left, And(), pos);
        }

        return left;
    }

    private Expr And()
    {
        var left = Not();
        while (IsKeyword("and"))
        {
            var pos = Current.Pos;
            _index++;
            left = new BinaryExpr("and", left, Not(), pos);
        }

        return left;
    }

    private Expr Not()
    {
        if (IsKeyword("not"))
        {
            var pos = Current.Pos;
            _index++;
            return new UnaryExpr("not", Not(), pos);
        }

        return Comparison();
    }

    private Expr Comparison()
    {
        var left = Coalesce();
        var pos = Current.Pos;
        string? op = Current.Kind switch
        {
            TokenKind.Equal => "==",
            TokenKind.NotEqual => "!=",
            TokenKind.Less => "<",
            TokenKind.LessOrEqual => "<=",
            TokenKind.Greater => ">",
            TokenKind.GreaterOrEqual => ">=",
            _ => null,
        };

        if (op is not null)
        {
            _index++;
        }
        else if (IsKeyword("in"))
        {
            _index++;
            op = "in";
        }
        else if (IsKeyword("not") && Next is { Kind: TokenKind.Identifier, Text: "in" })
        {
            _index += 2;
            op = "not in";
        }
        else
        {
            return left;
        }

        var right = Coalesce();
        if (IsComparisonStart())
        {
            throw new SyntaxError(Current.Pos, "Comparisons do not chain; use 'and' (a < b and b < c) or between().");
        }

        return new BinaryExpr(op, left, right, pos);
    }

    private bool IsComparisonStart() =>
        Current.Kind is TokenKind.Equal or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessOrEqual
            or TokenKind.Greater or TokenKind.GreaterOrEqual || IsKeyword("in");

    private Token? Next => _index + 1 < _tokens.Count ? _tokens[_index + 1] : null;

    private Expr Coalesce()
    {
        var left = Additive();
        while (Current.Kind == TokenKind.Coalesce)
        {
            var pos = Current.Pos;
            _index++;
            left = new BinaryExpr("??", left, Additive(), pos);
        }

        return left;
    }

    private Expr Additive()
    {
        var left = Multiplicative();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Current.Text;
            var pos = Current.Pos;
            _index++;
            left = new BinaryExpr(op, left, Multiplicative(), pos);
        }

        return left;
    }

    private Expr Multiplicative()
    {
        var left = Unary();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash)
        {
            var op = Current.Text;
            var pos = Current.Pos;
            _index++;
            left = new BinaryExpr(op, left, Unary(), pos);
        }

        return left;
    }

    private Expr Unary()
    {
        if (Current.Kind == TokenKind.Minus)
        {
            var pos = Current.Pos;
            _index++;
            return new UnaryExpr("-", Unary(), pos);
        }

        return Postfix();
    }

    private Expr Postfix()
    {
        var expr = Primary();
        while (Current.Kind == TokenKind.Dot)
        {
            var pos = Current.Pos;
            _index++;
            expr = new MemberExpr(expr, Name("member name"), pos);
        }

        return expr;
    }

    private Expr Primary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Integer:
                _index++;
                return new LiteralExpr(LiteralKind.Integer, token.Value, token.Pos);
            case TokenKind.Decimal:
                _index++;
                return new LiteralExpr(LiteralKind.Decimal, token.Value, token.Pos);
            case TokenKind.String:
                _index++;
                return new LiteralExpr(LiteralKind.String, token.Value, token.Pos);
            case TokenKind.LeftParen:
                _index++;
                var inner = Expression();
                Expect(TokenKind.RightParen, "')'");
                return inner;
            case TokenKind.LeftBracket:
                _index++;
                var items = new List<Expr>();
                if (Current.Kind != TokenKind.RightBracket)
                {
                    do
                    {
                        items.Add(Expression());
                    }
                    while (Accept(TokenKind.Comma));
                }

                Expect(TokenKind.RightBracket, "']'");
                return new ListExpr(items, token.Pos);
            case TokenKind.Identifier:
                return IdentifierExpression(token);
            default:
                throw new SyntaxError(token.Pos, $"Expected an expression, found {Describe(token)}.");
        }
    }

    private Expr IdentifierExpression(Token token)
    {
        switch (token.Text)
        {
            case "true":
            case "false":
                _index++;
                return new LiteralExpr(LiteralKind.Bool, token.Text == "true", token.Pos);
            case "null":
                _index++;
                return new LiteralExpr(LiteralKind.Null, null, token.Pos);
        }

        if (Reserved.Contains(token.Text))
        {
            throw new SyntaxError(token.Pos, $"Expected an expression, found keyword '{token.Text}'.");
        }

        _index++;
        if (!Accept(TokenKind.LeftParen))
        {
            return new NameExpr(token.Text, token.Pos);
        }

        if (Comprehensions.Contains(token.Text) && Current.Kind == TokenKind.Identifier
            && Next is { Kind: TokenKind.Identifier, Text: "in" } && !Reserved.Contains(Current.Text))
        {
            var variable = Current.Text;
            _index += 2;
            var source = Expression();
            Expr? filter = null, projection = null;
            if (IsKeyword("where"))
            {
                _index++;
                filter = Expression();
            }

            if (Accept(TokenKind.Colon))
            {
                projection = Expression();
            }

            Expect(TokenKind.RightParen, "')'");
            return new ComprehensionExpr(token.Text, variable, source, filter, projection, token.Pos);
        }

        var args = new List<Expr>();
        if (Current.Kind != TokenKind.RightParen)
        {
            do
            {
                args.Add(Expression());
            }
            while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.RightParen, "')'");
        return new CallExpr(token.Text, args, token.Pos);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private bool IsKeyword(string word) => Current.Kind == TokenKind.Identifier && Current.Text == word;

    private bool Accept(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        _index++;
        return true;
    }

    private void Expect(TokenKind kind, string what)
    {
        if (!Accept(kind))
        {
            throw new SyntaxError(Current.Pos, $"Expected {what}, found {Describe(Current)}.");
        }
    }

    private void Keyword(string word)
    {
        if (!IsKeyword(word))
        {
            throw new SyntaxError(Current.Pos, $"Expected '{word}', found {Describe(Current)}.");
        }

        _index++;
    }

    private string Identifier(string what)
    {
        if (Current.Kind != TokenKind.Identifier)
        {
            throw new SyntaxError(Current.Pos, $"Expected {what}, found {Describe(Current)}.");
        }

        return _tokens[_index++].Text;
    }

    private string Name(string what)
    {
        var pos = Current.Pos;
        var name = Identifier(what);
        return Reserved.Contains(name)
            ? throw new SyntaxError(pos, $"'{name}' is a keyword and cannot be used as a {what}.")
            : name;
    }

    private string String(string what)
    {
        if (Current.Kind != TokenKind.String)
        {
            throw new SyntaxError(Current.Pos, $"Expected {what} as a quoted string, found {Describe(Current)}.");
        }

        return _tokens[_index++].Text;
    }

    private static string Describe(Token token) => token.Kind switch
    {
        TokenKind.End => "end of file",
        TokenKind.String => $"string \"{token.Text}\"",
        _ => $"'{token.Text}'",
    };

    private sealed class SyntaxError(SourcePos pos, string message) : Exception(message)
    {
        public SourcePos Pos { get; } = pos;
    }
}
