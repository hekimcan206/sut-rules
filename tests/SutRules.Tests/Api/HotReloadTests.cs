using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SutRules.Api.Rules;
using SutRules.Domain;
using SutRules.Reporting;

namespace SutRules.Tests.Api;

/// <summary>
/// Editing a rule file changes behaviour without a restart, and a broken edit never takes effect: the previous bundle
/// keeps serving, the failure is visible, and fixing the file recovers.
/// </summary>
public sealed class HotReloadTests : IDisposable
{
    private readonly DirectoryInfo _rules = Directory.CreateTempSubdirectory("sutrules-reload-");

    public HotReloadTests()
    {
        Directory.CreateDirectory(Path.Combine(_rules.FullName, "catalog"));
        File.Copy(Path.Combine(TestData.RulesDirectory, "catalog", "sut-catalog.json"), Path.Combine(_rules.FullName, "catalog", "sut-catalog.json"));
        foreach (var file in Directory.EnumerateFiles(TestData.RulesDirectory, "*.rule"))
        {
            File.Copy(file, Path.Combine(_rules.FullName, Path.GetFileName(file)));
        }
    }

    public void Dispose() => _rules.Delete(recursive: true);

    private WebApplicationFactory<Program> Start() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Rules:Directory", _rules.FullName);
        b.UseSetting("Rules:PollInterval", "00:00:00.100");
    });

    private string RulePath(string id) => Path.Combine(_rules.FullName, id + ".rule");

    private static async Task<JsonNode> Ruleset(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonNode>("/api/v1/ruleset", TestContext.Current.CancellationToken))!;

    private static async Task<string?> AdmFinding(HttpClient client)
    {
        // Invoice 52 days after its last service: over 45, under 60.
        var invoice = TestData.ParityInvoices()[0];
        var line = invoice.Lines[0] with { ServiceDate = new DateOnly(2026, 1, 5) };
        invoice = invoice with { InvoiceDate = new DateOnly(2026, 2, 26), Lines = [line] };
        using var response = await client.PostAsJsonAsync("/api/v1/check", new InvoiceBatch([invoice]), BatchReport.JsonOptions, TestContext.Current.CancellationToken);
        var report = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        return report["invoices"]![0]!["findings"]!.AsArray().FirstOrDefault(f => f!["ruleId"]!.GetValue<string>() == "ADM-001")?["expected"]?.GetValue<string>();
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The rule directory change was not picked up within 10 s.");
    }

    [Fact]
    public async Task Edits_take_effect_broken_edits_do_not_and_a_fix_recovers()
    {
        using var factory = Start();
        using var client = factory.CreateClient();
        var original = (await Ruleset(client))["hash"]!.GetValue<string>();
        (await AdmFinding(client)).Should().Be("45");

        // 1. An amendment: the 2026 limit goes back to 60, so the 52-day invoice is now on time.
        await File.WriteAllTextAsync(RulePath("ADM-001"), (await File.ReadAllTextAsync(RulePath("ADM-001"), TestContext.Current.CancellationToken))
            .Replace("max_lag_days = 45", "max_lag_days = 60", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await Eventually(async () => (await Ruleset(client))["generation"]!.GetValue<int>() == 2);
        var amended = (await Ruleset(client))["hash"]!.GetValue<string>();
        amended.Should().NotBe(original);
        (await AdmFinding(client)).Should().BeNull();

        // 2. A typo: rejected as a whole, the amended bundle keeps serving, readiness reports Degraded.
        var good = await File.ReadAllTextAsync(RulePath("PRC-001"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(RulePath("PRC-001"), good.Replace("p.price", "p.prcie", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await Eventually(async () => (await Ruleset(client))["lastReloadFailure"] is not null);
        var failed = await Ruleset(client);
        failed["hash"]!.GetValue<string>().Should().Be(amended);
        failed["lastReloadFailure"]!["diagnostics"]!.AsArray().Select(d => d!.GetValue<string>()).Should().Contain(d => d.Contains("PRC-001.rule") && d.Contains("SR101"));
        (await client.GetStringAsync("/health/ready", TestContext.Current.CancellationToken)).Should().Contain("Degraded");
        (await AdmFinding(client)).Should().BeNull("the last good bundle is still the one evaluating");

        // 3. The fix: loads, failure clears.
        await File.WriteAllTextAsync(RulePath("PRC-001"), good + "\n", TestContext.Current.CancellationToken);
        await Eventually(async () => (await Ruleset(client))["lastReloadFailure"] is null);
        (await client.GetStringAsync("/health/ready", TestContext.Current.CancellationToken)).Should().Contain("Healthy");
        factory.Services.GetRequiredService<RuleBundleProvider>().Generation.Should().Be(3);
    }

    [Fact]
    public void A_broken_bundle_at_startup_stops_the_service()
    {
        File.WriteAllText(RulePath("MDL-001"), "rule \"MDL-001\" { category nope }");
        using var factory = Start();
        var act = () => factory.CreateClient();
        act.Should().Throw<SutRules.Language.RuleCompilationException>();
    }
}
