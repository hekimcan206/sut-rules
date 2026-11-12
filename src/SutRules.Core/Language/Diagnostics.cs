namespace SutRules.Language;

/// <summary>
/// A compile-time problem in a rule file. Codes are stable (SR0xx lexer/parser, SR1xx type checker, SR2xx rule
/// structure and versioning), so tooling and the golden diagnostics tests can match on them.
/// </summary>
public sealed record Diagnostic(string File, SourcePos Pos, string Code, string Message)
{
    public override string ToString() => $"{File}:{Pos}: error {Code}: {Message}";
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public DiagnosticBag(string file) => File = file;

    public string File { get; }

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Count > 0;

    public void Error(SourcePos pos, string code, string message) => _items.Add(new Diagnostic(File, pos, code, message));
}

/// <summary>Thrown when a rule bundle cannot be loaded; carries every diagnostic, not just the first.</summary>
public sealed class RuleCompilationException : Exception
{
    public RuleCompilationException(IReadOnlyList<Diagnostic> diagnostics)
        : base(Describe(diagnostics)) => Diagnostics = diagnostics;

    public RuleCompilationException()
        : this([])
    {
    }

    public RuleCompilationException(string message)
        : base(message) => Diagnostics = [];

    public RuleCompilationException(string message, Exception inner)
        : base(message, inner) => Diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics.Count == 0
            ? "Rule compilation failed."
            : $"{diagnostics.Count} rule compilation error(s):{Environment.NewLine}"
              + string.Join(Environment.NewLine, diagnostics.Select(d => "  " + d));
}
