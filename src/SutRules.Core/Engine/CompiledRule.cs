namespace SutRules.Engine;

/// <summary>
/// The state of one rule in one SUT version: which version block applies, its severity and its parameter values.
/// </summary>
public sealed record RuleVersionState(
    string Block,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    Severity Severity,
    object?[] Parameters,
    IReadOnlyDictionary<string, object?> ParametersByName);

/// <summary>A version block as written, for listings and the API (resolved state lives in <see cref="RuleVersionState"/>).</summary>
public sealed record RuleVersionInfo(string SutVersion, DateOnly EffectiveFrom, bool Retired, Severity? Severity, IReadOnlyDictionary<string, object?> Assignments);

internal enum StatementKind
{
    Let,
    Require,
    When,
}

internal sealed record CompiledStatement(StatementKind Kind, int Slot, Eval Condition, Eval? At, Eval? Expected, Eval? Actual);

internal sealed record CompiledCheck(Eval? Source, int Slot, bool AttachItem, CompiledStatement[] Statements);

/// <summary>A rule after parsing, type checking and compilation; immutable and safe to share across threads.</summary>
public sealed class CompiledRule
{
    internal CompiledRule(
        string id,
        RuleCategory category,
        Language.Bilingual title,
        string basis,
        Language.Bilingual reason,
        FixAction fixAction,
        string? fixValueSource,
        Language.Bilingual fixText,
        IReadOnlyList<RuleVersionInfo> versions,
        RuleVersionState?[] byCatalogOrdinal,
        CompiledCheck[] checks,
        int slotCount,
        string file,
        string source)
    {
        Id = id;
        Category = category;
        Title = title;
        Basis = basis;
        Reason = reason;
        FixAction = fixAction;
        FixValueSource = fixValueSource;
        FixText = fixText;
        Versions = versions;
        ByCatalogOrdinal = byCatalogOrdinal;
        Checks = checks;
        SlotCount = slotCount;
        File = file;
        Source = source;
    }

    public string Id { get; }

    public RuleCategory Category { get; }

    public Language.Bilingual Title { get; }

    public string Basis { get; }

    public Language.Bilingual Reason { get; }

    public FixAction FixAction { get; }

    /// <summary>"expected", "actual" or null: which reported value becomes the machine-readable fix value.</summary>
    public string? FixValueSource { get; }

    public Language.Bilingual FixText { get; }

    public IReadOnlyList<RuleVersionInfo> Versions { get; }

    public string File { get; }

    public string Source { get; }

    internal RuleVersionState?[] ByCatalogOrdinal { get; }

    internal CompiledCheck[] Checks { get; }

    internal int SlotCount { get; }

    /// <summary>The rule's state in the SUT version with the given ordinal; null when the rule is not in force.</summary>
    public RuleVersionState? StateFor(Catalog.SutVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Ordinal < ByCatalogOrdinal.Length ? ByCatalogOrdinal[version.Ordinal] : null;
    }

    internal Finding CreateFinding(Severity severity, int? lineNo, string? code, string? expected, string? actual)
    {
        var fixValue = FixValueSource switch
        {
            "expected" => expected,
            "actual" => actual,
            _ => null,
        };

        return new Finding(
            Id,
            Category,
            severity,
            lineNo,
            code,
            expected,
            actual,
            new LocalizedText(Format(Reason.Tr, lineNo, code, expected, actual), Format(Reason.En, lineNo, code, expected, actual)),
            new LocalizedText(Format(FixText.Tr, lineNo, code, expected, actual), Format(FixText.En, lineNo, code, expected, actual)),
            new SuggestedFix(FixAction, lineNo, fixValue));
    }

    /// <summary>The four placeholders a template may use; anything else is rejected when the rule compiles.</summary>
    public static readonly IReadOnlyList<string> Placeholders = ["line", "code", "expected", "actual"];

    private static string Format(string template, int? line, string? code, string? expected, string? actual) =>
        template
            .Replace("{line}", line is { } l ? Language.Values.Display(l) : "-", StringComparison.Ordinal)
            .Replace("{code}", code ?? "-", StringComparison.Ordinal)
            .Replace("{expected}", expected ?? "-", StringComparison.Ordinal)
            .Replace("{actual}", actual ?? "-", StringComparison.Ordinal);
}
