using System.Security.Cryptography;
using System.Text;
using SutRules.Catalog;
using SutRules.Language;

namespace SutRules.Engine;

/// <summary>A rule file as loaded from disk or supplied by a test.</summary>
public sealed record RuleSource(string File, string Text);

/// <summary>
/// An immutable, all-or-nothing set of compiled rules plus the catalog they were compiled against. Every result can
/// name the bundle hash, so "which rules judged this invoice" has an exact answer even after files change.
/// </summary>
public sealed class RuleBundle
{
    public const string CatalogFile = "catalog/sut-catalog.json";

    private readonly Dictionary<string, CompiledRule> _byId;

    private RuleBundle(SutCatalog catalog, IReadOnlyList<CompiledRule> rules, string hash, DateTimeOffset loadedAt)
    {
        Catalog = catalog;
        Rules = rules;
        Hash = hash;
        LoadedAt = loadedAt;
        _byId = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public SutCatalog Catalog { get; }

    /// <summary>Rules ordered by id.</summary>
    public IReadOnlyList<CompiledRule> Rules { get; }

    /// <summary>SHA-256 over every rule file and the catalog (path and content), lower-case hex.</summary>
    public string Hash { get; }

    public DateTimeOffset LoadedAt { get; }

    public CompiledRule? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Loads <c>&lt;directory&gt;/*.rule</c> and <c>&lt;directory&gt;/catalog/sut-catalog.json</c>. Throws
    /// <see cref="RuleCompilationException"/> with every diagnostic when any file is wrong: a bundle with one broken
    /// rule is not loaded at all, because silently dropping a rule would pass invoices it should reject.
    /// </summary>
    public static RuleBundle Load(string directory, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var catalogPath = Path.Combine(directory, CatalogFile);
        if (!File.Exists(catalogPath))
        {
            throw new RuleCompilationException([new Diagnostic(CatalogFile, new SourcePos(1, 1), "SR220", $"Catalog not found at {catalogPath}.")]);
        }

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var sources = Directory.EnumerateFiles(directory, "*.rule", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(path => new RuleSource(Path.GetFileName(path), File.ReadAllText(path, Encoding.UTF8)))
            .ToList();
        return Compile(catalogBytes, sources, clock);
    }

    public static RuleBundle Compile(byte[] catalogJson, IEnumerable<RuleSource> sources, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalogJson);
        ArgumentNullException.ThrowIfNull(sources);

        SutCatalog catalog;
        try
        {
            using var stream = new MemoryStream(catalogJson, writable: false);
            catalog = SutCatalog.Load(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new RuleCompilationException([new Diagnostic(CatalogFile, new SourcePos(1, 1), "SR220", ex.Message)]);
        }

        var list = sources.OrderBy(s => s.File, StringComparer.Ordinal).ToList();
        var diagnostics = new List<Diagnostic>();
        var rules = new List<CompiledRule>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in list)
        {
            var bag = new DiagnosticBag(source.File);
            var rule = RuleCompiler.Compile(source.Text, catalog, bag);
            diagnostics.AddRange(bag.Items);
            if (rule is null)
            {
                continue;
            }

            // One rule per file, named after it: a reviewer looking for DEM-002 opens DEM-002.rule.
            if (!string.Equals(Path.GetFileNameWithoutExtension(source.File), rule.Id, StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic(source.File, new SourcePos(1, 1), "SR211", $"Rule '{rule.Id}' must live in '{rule.Id}.rule'."));
                continue;
            }

            if (!seen.TryAdd(rule.Id, source.File))
            {
                diagnostics.Add(new Diagnostic(source.File, new SourcePos(1, 1), "SR212", $"Rule '{rule.Id}' is also defined in {seen[rule.Id]}."));
                continue;
            }

            rules.Add(rule);
        }

        if (diagnostics.Count > 0)
        {
            throw new RuleCompilationException(diagnostics);
        }

        return new RuleBundle(catalog, rules.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(), HashOf(catalogJson, list),
            (clock ?? TimeProvider.System).GetUtcNow());
    }

    private static string HashOf(byte[] catalog, List<RuleSource> sources)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string name, byte[] content)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(name));
            sha.AppendData([0]);
            sha.AppendData(BitConverter.GetBytes(content.Length));
            sha.AppendData(content);
        }

        Add(CatalogFile, catalog);
        foreach (var s in sources)
        {
            // Line endings are normalised so a Windows checkout and a Linux container agree on the hash.
            Add(s.File, Encoding.UTF8.GetBytes(s.Text.Replace("\r\n", "\n", StringComparison.Ordinal)));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
