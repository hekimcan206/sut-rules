using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using Serilog;
using SutRules.Api.Features;
using SutRules.Api.Infrastructure;
using SutRules.Api.Rules;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

// 5,000 synthetic invoices are about 7 MB of JSON; anything bigger is a batch job for the CLI, not a request.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 8 * 1024 * 1024);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<RulesOptions>(builder.Configuration.GetSection(RulesOptions.Section));
builder.Services.AddSingleton<RuleBundleProvider>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RuleBundleProvider>());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

builder.Services.AddHealthChecks()
    .AddCheck<RulesHealthCheck>("rules", tags: [HealthEndpoints.ReadyTag]);

// RFC 9457 problem details for every error, with the trace id so a reported error can be found in traces.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "sut-rules";
    document.Info.Version = "v1";
    document.Info.Description = "Versioned SUT rule files evaluated with the SUT version valid on the invoice date. Synthetic, illustrative rules.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// Resolve the provider eagerly: a broken rule bundle stops the process at startup instead of at the first request.
app.Services.GetRequiredService<RuleBundleProvider>();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("sut-rules"));
}

app.MapHealthEndpoints();
app.MapGroup("/api/v1").WithGroupName("v1").MapRuleEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so WebApplicationFactory can boot the app in tests.</summary>
public partial class Program;
