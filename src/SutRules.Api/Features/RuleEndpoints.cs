using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using SutRules.Api.Infrastructure;
using SutRules.Api.Rules;
using SutRules.Domain;
using SutRules.Engine;
using SutRules.Language;
using SutRules.Reporting;

namespace SutRules.Api.Features;

public sealed record RuleStateDto(string Block, DateOnly EffectiveFrom, DateOnly? EffectiveTo, Severity Severity, IReadOnlyDictionary<string, string> Parameters);

public sealed record RuleSummaryDto(string Id, RuleCategory Category, string TitleTr, string TitleEn, RuleStateDto? State);

public sealed record RulesOnDateDto(DateOnly Date, string? SutVersion, string Bundle, IReadOnlyList<RuleSummaryDto> Rules);

public sealed record RuleDetailDto(
    string Id,
    RuleCategory Category,
    string TitleTr,
    string TitleEn,
    string Basis,
    FixAction FixAction,
    IReadOnlyList<RuleVersionDto> Versions,
    string Source);

public sealed record RuleVersionDto(string SutVersion, DateOnly EffectiveFrom, bool Retired, Severity? Severity, IReadOnlyDictionary<string, string> Assignments);

public sealed record SutVersionDto(string Id, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Description);

public sealed record RulesetDto(
    string Hash,
    DateTimeOffset LoadedAt,
    int Generation,
    int Rules,
    IReadOnlyList<SutVersionDto> SutVersions,
    ReloadFailureDto? LastReloadFailure);

public sealed record ReloadFailureDto(DateTimeOffset At, IReadOnlyList<string> Diagnostics);

internal static class RuleEndpoints
{
    public const string BundleHeader = "X-Rule-Bundle";

    public static RouteGroupBuilder MapRuleEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/check", Check)
            .WithName("CheckInvoices")
            .WithSummary("Check invoices against the SUT version valid on each invoice date")
            .WithDescription("Returns the rejection-reason report (JSON, or Markdown with ?format=markdown). The X-Rule-Bundle header names the rule bundle that produced it.")
            .Produces<BatchReport>()
            .ProducesValidationProblem();

        group.MapGet("/rules", ListRules)
            .WithName("ListRules")
            .WithSummary("Rules and their state (severity, version block, parameters) on a date; defaults to today");

        group.MapGet("/rules/{id}", GetRule)
            .WithName("GetRule")
            .WithSummary("One rule: metadata, version blocks as written, and its source file")
            .Produces<RuleDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/ruleset", GetRuleset)
            .WithName("GetRuleset")
            .WithSummary("The active rule bundle: hash, load time, SUT timeline and the last rejected reload, if any");

        return group;
    }

    private static IResult Check(
        InvoiceBatch? batch,
        string? format,
        HttpContext http,
        RuleBundleProvider provider,
        RulesMetrics metrics,
        IOptions<RulesOptions> options,
        TimeProvider clock)
    {
        var max = options.Value.MaxInvoicesPerRequest;
        if (batch?.Invoices is not { Count: > 0 } invoices)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["invoices"] = ["At least one invoice is required."] });
        }

        if (invoices.Count > max)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["invoices"] = [$"At most {max} invoices per request."] });
        }

        if (format is not (null or "json" or "markdown"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["Use json or markdown."] });
        }

        // One engine reference for the whole request: a reload mid-request cannot mix two bundles in one report.
        var engine = provider.Engine;
        using var activity = RulesMetrics.ActivitySource.StartActivity("sutrules.check");
        activity?.SetTag("sutrules.bundle", engine.Bundle.Hash);
        activity?.SetTag("sutrules.invoices", invoices.Count);

        var watch = Stopwatch.StartNew();
        var results = engine.CheckAll(invoices);
        watch.Stop();
        metrics.Checked(results, watch.Elapsed.TotalMilliseconds);

        var report = BatchReport.Create(engine.Bundle, results, clock.GetUtcNow());
        http.Response.Headers[BundleHeader] = engine.Bundle.Hash;
        return format == "markdown"
            ? Results.Text(report.ToMarkdown(), "text/markdown; charset=utf-8")
            : Results.Json(report, BatchReport.JsonOptions);
    }

    private static IResult ListRules(string? date, RuleBundleProvider provider, TimeProvider clock)
    {
        var on = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (date is not null && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out on))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["Use yyyy-MM-dd."] });
        }

        var bundle = provider.Bundle;
        var version = bundle.Catalog.ResolveFor(on);
        var rules = bundle.Rules.Select(r =>
        {
            var state = version is null ? null : r.StateFor(version);
            return new RuleSummaryDto(r.Id, r.Category, r.Title.Tr, r.Title.En, state is null ? null
                : new RuleStateDto(state.Block, state.EffectiveFrom, state.EffectiveTo, state.Severity, Text(state.ParametersByName)));
        }).ToList();
        return Results.Json(new RulesOnDateDto(on, version?.Id, bundle.Hash, rules), BatchReport.JsonOptions);
    }

    private static IResult GetRule(string id, RuleBundleProvider provider)
    {
        if (provider.Bundle.Find(id) is not { } r)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"Rule {id} is not in the active bundle.");
        }

        var versions = r.Versions.Select(v => new RuleVersionDto(v.SutVersion, v.EffectiveFrom, v.Retired, v.Severity, Text(v.Assignments))).ToList();
        return Results.Json(new RuleDetailDto(r.Id, r.Category, r.Title.Tr, r.Title.En, r.Basis, r.FixAction, versions, r.Source), BatchReport.JsonOptions);
    }

    private static IResult GetRuleset(RuleBundleProvider provider)
    {
        var bundle = provider.Bundle;
        var failure = provider.LastFailure;
        return Results.Json(new RulesetDto(
            bundle.Hash,
            bundle.LoadedAt,
            provider.Generation,
            bundle.Rules.Count,
            bundle.Catalog.Versions.Select(v => new SutVersionDto(v.Id, v.EffectiveFrom, v.EffectiveTo, v.DescriptionEn)).ToList(),
            failure is null ? null : new ReloadFailureDto(failure.At, failure.Diagnostics.Select(d => d.ToString()).ToList())), BatchReport.JsonOptions);
    }

    private static Dictionary<string, string> Text(IReadOnlyDictionary<string, object?> values) =>
        values.ToDictionary(p => p.Key, p => Values.Display(p.Value), StringComparer.Ordinal);
}
