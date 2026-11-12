using System.Text.RegularExpressions;
using SutRules.Catalog;
using SutRules.Engine;

namespace SutRules.Language;

/// <summary>
/// Turns a parsed rule into a <see cref="CompiledRule"/>: validates its structure, resolves its version blocks
/// against the SUT catalog timeline, and compiles its checks.
/// </summary>
public static partial class RuleCompiler
{
    private static readonly Dictionary<string, Severity> Severities = new Dictionary<string, Severity>(StringComparer.Ordinal)
    {
        ["info"] = Severity.Info,
        ["warning"] = Severity.Warning,
        ["error"] = Severity.Error,
    };

    /// <summary>Parses and compiles one rule file. Returns null when <paramref name="diagnostics"/> has errors.</summary>
    public static CompiledRule? Compile(string source, SutCatalog catalog, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var decl = Parser.ParseRule(source, diagnostics);
        return decl is null ? null : Compile(decl, source, catalog, diagnostics);
    }

    public static CompiledRule? Compile(RuleDecl rule, string source, SutCatalog catalog, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var before = diagnostics.Items.Count;

        if (!RuleIdPattern().IsMatch(rule.Id))
        {
            diagnostics.Error(rule.Pos, "SR200", $"Rule id '{rule.Id}' must look like ABC-123.");
        }

        var category = RuleCategory.Structural;
        if (rule.Category is null)
        {
            diagnostics.Error(rule.Pos, "SR200", "The rule has no 'category'.");
        }
        else if (!TryEnum(rule.Category, out category))
        {
            diagnostics.Error(rule.Pos, "SR201", $"Unknown category '{rule.Category}'. Known: {Names<RuleCategory>()}.");
        }

        Require(rule.Title, "title", rule, diagnostics);
        Require(rule.Basis, "basis", rule, diagnostics);
        Require(rule.Reason, "reason", rule, diagnostics);
        Require(rule.Fix, "fix", rule, diagnostics);

        var fixAction = FixAction.CorrectField;
        if (rule.Fix is not null && !Enum.TryParse(rule.Fix.Action, ignoreCase: false, out fixAction))
        {
            diagnostics.Error(rule.Fix.Pos, "SR203", $"Unknown fix action '{rule.Fix.Action}'. Known: {Names<FixAction>()}.");
        }

        foreach (var (text, pos) in Templates(rule))
        {
            foreach (Match m in PlaceholderPattern().Matches(text))
            {
                if (!CompiledRule.Placeholders.Contains(m.Groups[1].Value, StringComparer.Ordinal))
                {
                    diagnostics.Error(pos, "SR210", $"Unknown placeholder '{m.Value}' in a text; use {{line}}, {{code}}, {{expected}} or {{actual}}.");
                }
            }
        }

        // Parameters: declared once with a type; their values come from version blocks.
        var parameters = new Dictionary<string, (int Index, DslType Type)>(StringComparer.Ordinal);
        foreach (var p in rule.Params)
        {
            if (Schema.ParamType(p.TypeName) is not { } type)
            {
                diagnostics.Error(p.Pos, "SR209", $"Unknown parameter type '{p.TypeName}'; use int, decimal, string, bool or date.");
                continue;
            }

            if (Parser.Reserved.Contains(p.Name) || Schema.Roots.ContainsKey(p.Name) || !parameters.TryAdd(p.Name, (parameters.Count, type)))
            {
                diagnostics.Error(p.Pos, "SR208", $"Parameter '{p.Name}' is declared twice or clashes with a built-in name.");
            }
        }

        var (versionInfos, states) = ResolveVersions(rule, catalog, parameters, diagnostics);

        // Checks.
        if (rule.Checks.Count == 0)
        {
            diagnostics.Error(rule.Pos, "SR200", "The rule has no 'check' block, so it can never report anything.");
        }

        var compiler = new ExpressionCompiler(diagnostics, parameters);
        var checks = new List<CompiledCheck>();
        foreach (var check in rule.Checks)
        {
            checks.Add(CompileCheck(check, compiler, diagnostics));
        }

        if (diagnostics.Items.Count > before)
        {
            return null;
        }

        return new CompiledRule(
            rule.Id,
            category,
            rule.Title!,
            rule.Basis!,
            rule.Reason!,
            fixAction,
            rule.Fix!.ValueSource,
            rule.Fix.Text,
            versionInfos,
            states,
            [.. checks],
            Math.Max(1, compiler.SlotCount),
            diagnostics.File,
            source);
    }

    private static CompiledCheck CompileCheck(CheckBlock check, ExpressionCompiler compiler, DiagnosticBag diagnostics)
    {
        compiler.PushScope();
        try
        {
            Eval? source = null;
            var slot = -1;
            var attachItem = false;
            if (check.Variable is not null)
            {
                var typed = compiler.Compile(check.Source!);
                if (typed.Type is ListType list)
                {
                    source = typed.Eval;
                    attachItem = list.Element == Schema.Line;
                    slot = compiler.Declare(check.Variable, list.Element, check.Pos);
                }
                else
                {
                    if (typed.Type != ExpressionCompiler.ErrorType)
                    {
                        diagnostics.Error(check.Source!.Pos, "SR107", $"'check each' needs a list, but this is {typed.Type}.");
                    }

                    slot = compiler.Declare(check.Variable, ExpressionCompiler.ErrorType, check.Pos);
                }
            }

            var statements = new List<CompiledStatement>();
            var reports = 0;
            foreach (var statement in check.Statements)
            {
                switch (statement)
                {
                    case LetStatement letStmt:
                        var value = compiler.Compile(letStmt.Value);
                        var letSlot = compiler.Declare(letStmt.Name, value.Type, letStmt.Pos);
                        statements.Add(new CompiledStatement(StatementKind.Let, letSlot, value.Eval, null, null, null));
                        break;
                    case RequireStatement require:
                        statements.Add(new CompiledStatement(StatementKind.Require, -1, compiler.CompileCondition(require.Condition, "'require' condition").Eval, null, null, null));
                        break;
                    case WhenStatement whenStmt:
                        reports++;
                        var condition = compiler.CompileCondition(whenStmt.Condition, "'when' condition");
                        Eval? at = null;
                        if (whenStmt.At is not null)
                        {
                            var typedAt = compiler.Compile(whenStmt.At);
                            if (typedAt.Type != Schema.Line && typedAt.Type != ExpressionCompiler.ErrorType && typedAt.Type != DslType.Null)
                            {
                                diagnostics.Error(whenStmt.At.Pos, "SR109", $"'report at' needs a line, but this is {typedAt.Type}.");
                            }

                            at = typedAt.Eval;
                        }

                        var expected = whenStmt.Expected is null ? null : compiler.Compile(whenStmt.Expected).Eval;
                        var actual = whenStmt.Actual is null ? null : compiler.Compile(whenStmt.Actual).Eval;
                        statements.Add(new CompiledStatement(StatementKind.When, -1, condition.Eval, at, expected, actual));
                        break;
                }
            }

            if (reports == 0)
            {
                diagnostics.Error(check.Pos, "SR200", "This check has no 'when ... report', so it can never report anything.");
            }

            return new CompiledCheck(source, slot, attachItem, [.. statements]);
        }
        finally
        {
            compiler.PopScope();
        }
    }

    /// <summary>
    /// Version blocks must name catalog versions in ascending order. Block i applies from its SUT version up to (not
    /// including) block i+1; unset fields inherit. The result is one state per catalog version, so run-time lookup is
    /// an array index.
    /// </summary>
    private static (IReadOnlyList<RuleVersionInfo>, RuleVersionState?[]) ResolveVersions(
        RuleDecl rule,
        SutCatalog catalog,
        Dictionary<string, (int Index, DslType Type)> parameters,
        DiagnosticBag diagnostics)
    {
        var states = new RuleVersionState?[catalog.Versions.Count];
        var infos = new List<RuleVersionInfo>();
        if (rule.Versions.Count == 0)
        {
            diagnostics.Error(rule.Pos, "SR200", "The rule has no 'version' block, so it is never in force.");
            return (infos, states);
        }

        Severity? severity = null;
        var values = new object?[parameters.Count];
        var assigned = new bool[parameters.Count];
        var previousOrdinal = -1;
        var blocks = new List<(SutVersion Version, VersionBlock Block, Severity? Severity, object?[] Values)>();

        for (var i = 0; i < rule.Versions.Count; i++)
        {
            var block = rule.Versions[i];
            var version = catalog.Find(block.SutVersion);
            if (version is null)
            {
                diagnostics.Error(block.Pos, "SR204", $"Unknown SUT version '{block.SutVersion}'. The catalog has: {string.Join(", ", catalog.Versions.Select(v => v.Id))}.");
                continue;
            }

            if (version.Ordinal <= previousOrdinal)
            {
                diagnostics.Error(block.Pos, "SR205", $"Version blocks must be in ascending SUT order; '{block.SutVersion}' comes after a later or equal version.");
                continue;
            }

            previousOrdinal = version.Ordinal;
            if (i == 0 && block.Retired)
            {
                diagnostics.Error(block.Pos, "SR213", "The first version block cannot retire the rule; start the rule at the version it comes into force.");
            }

            if (block.Severity is not null)
            {
                if (Severities.TryGetValue(block.Severity, out var s))
                {
                    severity = s;
                }
                else
                {
                    diagnostics.Error(block.Pos, "SR202", $"Unknown severity '{block.Severity}'; use info, warning or error.");
                }
            }
            else if (i == 0 && !block.Retired)
            {
                diagnostics.Error(block.Pos, "SR206", "The first version block must set a severity.");
            }

            var assignments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var a in block.Assignments)
            {
                if (!parameters.TryGetValue(a.Name, out var p))
                {
                    diagnostics.Error(a.Pos, "SR208", $"'{a.Name}' is not a declared parameter; declare it with 'param {a.Name} <type>'.");
                    continue;
                }

                if (!TryConstant(a.Value, p.Type, out var constant))
                {
                    diagnostics.Error(a.Pos, "SR214", $"Parameter '{a.Name}' needs a constant {p.Type} value.");
                    continue;
                }

                values[p.Index] = constant;
                assigned[p.Index] = true;
                assignments[a.Name] = constant;
            }

            if (i == 0)
            {
                foreach (var (name, p) in parameters)
                {
                    if (!assigned[p.Index])
                    {
                        diagnostics.Error(block.Pos, "SR207", $"Parameter '{name}' has no value in the first version block.");
                    }
                }
            }

            infos.Add(new RuleVersionInfo(version.Id, version.EffectiveFrom, block.Retired, block.Severity is null ? null : severity, assignments));
            blocks.Add((version, block, block.Retired ? null : severity, (object?[])values.Clone()));
        }

        for (var b = 0; b < blocks.Count; b++)
        {
            var (version, block, blockSeverity, blockValues) = blocks[b];
            if (blockSeverity is null)
            {
                continue; // retired (or invalid): not in force until a later block revives it
            }

            var end = b + 1 < blocks.Count ? blocks[b + 1].Version.Ordinal : catalog.Versions.Count;
            var byName = parameters.ToDictionary(p => p.Key, p => blockValues[p.Value.Index], StringComparer.Ordinal);
            for (var ordinal = version.Ordinal; ordinal < end; ordinal++)
            {
                states[ordinal] = new RuleVersionState(
                    block.SutVersion,
                    version.EffectiveFrom,
                    end < catalog.Versions.Count ? catalog.Versions[end].EffectiveFrom : null,
                    blockSeverity.Value,
                    blockValues,
                    byName);
            }
        }

        return (infos, states);
    }

    /// <summary>Parameter values are constants: literals, negated numbers or date("...").</summary>
    private static bool TryConstant(Expr expr, DslType type, out object? value)
    {
        value = null;
        object? raw = expr switch
        {
            LiteralExpr { Kind: not LiteralKind.Null } l => l.Kind == LiteralKind.Integer ? Values.Int((int)l.Value!) : l.Value,
            UnaryExpr { Operator: "-", Operand: LiteralExpr { Kind: LiteralKind.Integer, Value: int i } } => Values.Int(-i),
            UnaryExpr { Operator: "-", Operand: LiteralExpr { Kind: LiteralKind.Decimal, Value: decimal d } } => -d,
            CallExpr { Function: "date", Arguments: [LiteralExpr { Kind: LiteralKind.String, Value: string s }] }
                when DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) => date,
            _ => null,
        };

        if (raw is null)
        {
            return false;
        }

        if (type == DslType.Decimal && raw is int asInt)
        {
            raw = (decimal)asInt;
        }

        var ok = (type == DslType.Int && raw is int) || (type == DslType.Decimal && raw is decimal) || (type == DslType.String && raw is string)
            || (type == DslType.Bool && raw is bool) || (type == DslType.Date && raw is DateOnly);
        value = ok ? raw : null;
        return ok;
    }

    private static IEnumerable<(string Text, SourcePos Pos)> Templates(RuleDecl rule)
    {
        if (rule.Reason is not null)
        {
            yield return (rule.Reason.Tr, rule.Pos);
            yield return (rule.Reason.En, rule.Pos);
        }

        if (rule.Fix is not null)
        {
            yield return (rule.Fix.Text.Tr, rule.Fix.Pos);
            yield return (rule.Fix.Text.En, rule.Fix.Pos);
        }
    }

    private static void Require(object? value, string item, RuleDecl rule, DiagnosticBag diagnostics)
    {
        if (value is null)
        {
            diagnostics.Error(rule.Pos, "SR200", $"The rule has no '{item}'.");
        }
    }

    private static bool TryEnum<T>(string lowerName, out T value)
        where T : struct, Enum
    {
        // Categories are written in lower case in rule files ("category pricing").
        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(lowerName, name.ToLowerInvariant(), StringComparison.Ordinal))
            {
                value = Enum.Parse<T>(name);
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Names<T>()
        where T : struct, Enum => string.Join(", ", Enum.GetNames<T>().Select(n => typeof(T) == typeof(RuleCategory) ? n.ToLowerInvariant() : n));

    [GeneratedRegex("^[A-Z]{3}-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
