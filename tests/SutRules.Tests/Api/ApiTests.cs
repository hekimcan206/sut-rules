using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SutRules.Api.Features;
using SutRules.Domain;
using SutRules.Engine;
using SutRules.Reporting;

namespace SutRules.Tests.Api;

/// <summary>The HTTP surface over the shipped rules, booted in-process on a real Kestrel pipeline.</summary>
public sealed class ApiTests : IClassFixture<ApiTests.Factory>
{
    private readonly HttpClient _client;

    public ApiTests(Factory factory) => _client = factory.CreateClient();

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Rules:Directory", TestData.RulesDirectory);
            builder.UseSetting("Rules:PollInterval", "00:00:00");
            builder.UseSetting("Rules:MaxInvoicesPerRequest", "100");
            builder.UseSetting("OpenApi:Enabled", "true");
        }
    }

    private static InvoiceBatch Sample(int count) => new(TestData.ParityInvoices().Take(count).ToList());

    [Fact]
    public async Task Check_returns_the_same_results_as_the_engine_and_names_the_bundle()
    {
        var batch = Sample(50);
        using var response = await _client.PostAsJsonAsync("/api/v1/check", batch, BatchReport.JsonOptions, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(RuleEndpoints.BundleHeader).Single().Should().Be(TestData.Bundle.Hash);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["ruleset"]!.GetValue<string>().Should().Be(TestData.Bundle.Hash);
        body["engine"]!.GetValue<string>().Should().Be("sut-rules");

        var expected = JsonSerializer.SerializeToNode(new RuleEngine(TestData.Bundle).CheckAll(batch.Invoices), BatchReport.JsonOptions);
        JsonNode.DeepEquals(body["invoices"], expected).Should().BeTrue();
    }

    [Fact]
    public async Task Markdown_format_renders_the_report()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/check?format=markdown", Sample(20), BatchReport.JsonOptions, TestContext.Current.CancellationToken);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.Should().StartWith("# SUT pre-check report").And.Contain("| Invoices | Clean |");
    }

    [Theory]
    [InlineData("{\"invoices\":[]}", "", "invoices")]
    [InlineData("{}", "", "invoices")]
    [InlineData(null, "?format=pdf", "format")]
    public async Task Invalid_requests_get_problem_details(string? json, string query, string field)
    {
        HttpContent content = json is null
            ? JsonContent.Create(Sample(1), options: BatchReport.JsonOptions)
            : new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"/api/v1/check{query}", content, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(field);
    }

    [Fact]
    public async Task Batch_size_is_capped()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/check", Sample(101), BatchReport.JsonOptions, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rules_on_a_date_show_the_state_of_each_rule_in_that_sut_version()
    {
        var y2025 = await _client.GetFromJsonAsync<JsonNode>("/api/v1/rules?date=2025-03-01", TestContext.Current.CancellationToken);
        y2025!["sutVersion"]!.GetValue<string>().Should().Be("SUT-2025.01");
        Rule(y2025, "ADM-001")["state"].Should().BeNull();
        Rule(y2025, "PRV-001")["state"]!["severity"]!.GetValue<string>().Should().Be("Warning");

        var y2026 = await _client.GetFromJsonAsync<JsonNode>("/api/v1/rules?date=2026-03-01", TestContext.Current.CancellationToken);
        Rule(y2026!, "ADM-001")["state"]!["parameters"]!["max_lag_days"]!.GetValue<string>().Should().Be("45");
        Rule(y2026!, "PRV-001")["state"]!["severity"]!.GetValue<string>().Should().Be("Error");

        var before = await _client.GetFromJsonAsync<JsonNode>("/api/v1/rules?date=2024-01-01", TestContext.Current.CancellationToken);
        before!["sutVersion"].Should().BeNull();

        using var bad = await _client.GetAsync("/api/v1/rules?date=01.03.2026", TestContext.Current.CancellationToken);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static JsonNode Rule(JsonNode list, string id) => list["rules"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public async Task A_rule_exposes_its_versions_and_source()
    {
        var rule = await _client.GetFromJsonAsync<JsonNode>("/api/v1/rules/ADM-001", TestContext.Current.CancellationToken);
        rule!["versions"]!.AsArray().Select(v => v!["sutVersion"]!.GetValue<string>()).Should().Equal("SUT-2025.07", "SUT-2026.01");
        rule["source"]!.GetValue<string>().Should().Contain("param max_lag_days int");

        using var missing = await _client.GetAsync("/api/v1/rules/XYZ-999", TestContext.Current.CancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ruleset_health_and_openapi_are_served()
    {
        var ruleset = await _client.GetFromJsonAsync<JsonNode>("/api/v1/ruleset", TestContext.Current.CancellationToken);
        ruleset!["hash"]!.GetValue<string>().Should().Be(TestData.Bundle.Hash);
        ruleset["rules"]!.GetValue<int>().Should().Be(22);
        ruleset["sutVersions"]!.AsArray().Should().HaveCount(3);
        ruleset["lastReloadFailure"].Should().BeNull();

        (await _client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetStringAsync("/health/ready", TestContext.Current.CancellationToken)).Should().Contain("Healthy");
        (await _client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Should().Contain("CheckInvoices");
    }
}
