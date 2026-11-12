using System.IO.Compression;
using System.Text.Json;
using SutRules.Domain;
using SutRules.Engine;
using SutRules.Reporting;

namespace SutRules.Tests;

/// <summary>Shared access to the repository's rule files and fixtures; bundles are compiled once per test run.</summary>
internal static class TestData
{
    private static readonly Lazy<RuleBundle> LazyBundle = new(() => RuleBundle.Load(RulesDirectory));

    public static string RepositoryRoot { get; } = FindRoot();

    public static string RulesDirectory => Path.Combine(RepositoryRoot, "rules");

    public static RuleBundle Bundle => LazyBundle.Value;

    public static byte[] CatalogBytes => File.ReadAllBytes(Path.Combine(RulesDirectory, RuleBundle.CatalogFile));

    public static IReadOnlyList<RuleSource> RuleSources() =>
        Directory.EnumerateFiles(RulesDirectory, "*.rule")
            .Order(StringComparer.Ordinal)
            .Select(p => new RuleSource(Path.GetFileName(p), File.ReadAllText(p)))
            .ToList();

    public static string ReadGzip(string relative)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, relative));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<Invoice> ParityInvoices() =>
        JsonSerializer.Deserialize<InvoiceBatch>(ReadGzip("Parity/invoices.json.gz"), BatchReport.JsonOptions)!.Invoices;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SutRules.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (SutRules.slnx) not found.");
    }
}
