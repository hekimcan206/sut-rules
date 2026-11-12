namespace SutRules.Language;

// Syntax tree of a rule file. Positions are carried for diagnostics only; structural comparisons (round-trip tests)
// go through Printer.Sexp, which ignores them.

public abstract record Expr(SourcePos Pos);

public enum LiteralKind
{
    Null,
    Bool,
    Integer,
    Decimal,
    String,
}

public sealed record LiteralExpr(LiteralKind Kind, object? Value, SourcePos Pos) : Expr(Pos);

public sealed record NameExpr(string Name, SourcePos Pos) : Expr(Pos);

public sealed record MemberExpr(Expr Target, string Member, SourcePos Pos) : Expr(Pos);

public sealed record CallExpr(string Function, IReadOnlyList<Expr> Arguments, SourcePos Pos) : Expr(Pos);

/// <summary><c>fn(x in source where filter : projection)</c>; filter and projection are optional.</summary>
public sealed record ComprehensionExpr(
    string Function,
    string Variable,
    Expr Source,
    Expr? Filter,
    Expr? Projection,
    SourcePos Pos) : Expr(Pos);

public sealed record UnaryExpr(string Operator, Expr Operand, SourcePos Pos) : Expr(Pos);

public sealed record BinaryExpr(string Operator, Expr Left, Expr Right, SourcePos Pos) : Expr(Pos);

public sealed record IfExpr(Expr Condition, Expr Then, Expr Else, SourcePos Pos) : Expr(Pos);

public sealed record ListExpr(IReadOnlyList<Expr> Items, SourcePos Pos) : Expr(Pos);

public abstract record Statement(SourcePos Pos);

public sealed record LetStatement(string Name, Expr Value, SourcePos Pos) : Statement(Pos);

/// <summary>Skips the current item when the condition is not true.</summary>
public sealed record RequireStatement(Expr Condition, SourcePos Pos) : Statement(Pos);

/// <summary>Reports a finding and stops the current item when the condition is true.</summary>
public sealed record WhenStatement(Expr Condition, Expr? At, Expr? Expected, Expr? Actual, SourcePos Pos) : Statement(Pos);

/// <summary>
/// <c>check invoice { ... }</c> runs once per invoice; <c>check each x in source { ... }</c> runs once per element.
/// </summary>
public sealed record CheckBlock(string? Variable, Expr? Source, IReadOnlyList<Statement> Statements, SourcePos Pos);

public sealed record ParamDecl(string Name, string TypeName, SourcePos Pos);

public sealed record ParamAssignment(string Name, Expr Value, SourcePos Pos);

/// <summary>
/// One amendment of a rule, effective from the named SUT version until the next block. Unset fields inherit from the
/// previous block.
/// </summary>
public sealed record VersionBlock(
    string SutVersion,
    string? Severity,
    bool Retired,
    IReadOnlyList<ParamAssignment> Assignments,
    SourcePos Pos);

public sealed record Bilingual(string Tr, string En);

public sealed record FixDecl(string Action, string? ValueSource, Bilingual Text, SourcePos Pos);

public sealed record RuleDecl(
    string Id,
    string? Category,
    Bilingual? Title,
    string? Basis,
    IReadOnlyList<ParamDecl> Params,
    IReadOnlyList<VersionBlock> Versions,
    IReadOnlyList<CheckBlock> Checks,
    Bilingual? Reason,
    FixDecl? Fix,
    SourcePos Pos);
