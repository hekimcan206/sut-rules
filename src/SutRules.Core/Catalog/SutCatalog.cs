using System.Text.Json;
using System.Text.Json.Serialization;

namespace SutRules.Catalog;

/// <summary>
/// The SUT version timeline with its reference data. A version is written as a delta over the one it inherits from,
/// the way amendments (değişiklik tebliğleri) are published, and resolved here into full snapshots.
/// </summary>
public sealed class SutCatalog
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, SutVersion> _byId;

    private SutCatalog(IReadOnlyList<SutVersion> versions, IReadOnlyList<Branch> branches)
    {
        Versions = versions;
        Branches = branches;
        _byId = versions.ToDictionary(v => v.Id, StringComparer.Ordinal);
    }

    /// <summary>Versions ordered by effective date.</summary>
    public IReadOnlyList<SutVersion> Versions { get; }

    public IReadOnlyList<Branch> Branches { get; }

    /// <summary>The version valid on <paramref name="date"/>, or null when the date precedes every version.</summary>
    public SutVersion? ResolveFor(DateOnly date)
    {
        // A handful of versions per year: a reverse scan beats any index.
        for (var i = Versions.Count - 1; i >= 0; i--)
        {
            if (Versions[i].IsValidOn(date))
            {
                return Versions[i];
            }
        }

        return null;
    }

    public SutVersion? Find(string id) => _byId.GetValueOrDefault(id);

    public static SutCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static SutCatalog Load(Stream json)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty SUT catalog.");
        return Resolve(document);
    }

    private static SutCatalog Resolve(CatalogDocument document)
    {
        var ordered = (document.Versions ?? []).OrderBy(v => v.EffectiveFrom).ToList();
        if (ordered.Count == 0)
        {
            throw new InvalidDataException("The SUT catalog defines no version.");
        }

        if (ordered.Select(v => v.EffectiveFrom).Distinct().Count() != ordered.Count)
        {
            throw new InvalidDataException("Two SUT versions share an effective date; the timeline would be ambiguous.");
        }

        var resolved = new List<SutVersion>(ordered.Count);
        var byId = new Dictionary<string, SutVersion>(StringComparer.Ordinal);

        for (var i = 0; i < ordered.Count; i++)
        {
            var dto = ordered[i];
            SutVersion? parent = null;
            if (dto.Inherits is not null && !byId.TryGetValue(dto.Inherits, out parent))
            {
                throw new InvalidDataException($"Version {dto.Id} inherits unknown or later version {dto.Inherits}.");
            }

            var procedures = parent is null
                ? new Dictionary<string, ProcedureDefinition>(StringComparer.Ordinal)
                : new Dictionary<string, ProcedureDefinition>(parent.Procedures, StringComparer.Ordinal);
            foreach (var code in dto.RemovedProcedures ?? [])
            {
                procedures.Remove(code);
            }

            foreach (var p in dto.Procedures ?? [])
            {
                procedures[p.Code] = p.ToDefinition();
            }

            foreach (var (code, change) in dto.ProcedureChanges ?? [])
            {
                if (!procedures.TryGetValue(code, out var current))
                {
                    throw new InvalidDataException($"Version {dto.Id} changes unknown procedure {code}.");
                }

                procedures[code] = change.ApplyTo(current);
            }

            var pairs = new List<IncompatiblePair>(parent?.IncompatiblePairs ?? []);
            pairs.AddRange(dto.IncompatiblePairs ?? []);

            var version = new SutVersion
            {
                Id = dto.Id,
                EffectiveFrom = dto.EffectiveFrom,
                EffectiveTo = i + 1 < ordered.Count ? ordered[i + 1].EffectiveFrom : null,
                DescriptionEn = dto.DescriptionEn,
                Ordinal = i,
                Procedures = procedures,
                IncompatiblePairs = pairs,
                DiagnosisGender = dto.DiagnosisGender ?? parent?.DiagnosisGender ?? [],
            };

            if (!byId.TryAdd(version.Id, version))
            {
                throw new InvalidDataException($"Duplicate SUT version id {version.Id}.");
            }

            resolved.Add(version);
        }

        return new SutCatalog(resolved, document.Branches ?? []);
    }

    private sealed record CatalogDocument(IReadOnlyList<Branch>? Branches, IReadOnlyList<VersionDocument>? Versions);

    private sealed record VersionDocument(
        string Id,
        DateOnly EffectiveFrom,
        string? Inherits,
        string DescriptionEn,
        IReadOnlyList<ProcedureDocument>? Procedures,
        Dictionary<string, ProcedureChange>? ProcedureChanges,
        IReadOnlyList<string>? RemovedProcedures,
        IReadOnlyList<IncompatiblePair>? IncompatiblePairs,
        IReadOnlyList<DiagnosisGenderRange>? DiagnosisGender);

    private sealed record ProcedureDocument(
        string Code,
        string NameTr,
        string NameEn,
        ProcedureKind Kind,
        decimal Price,
        Domain.Gender? Gender,
        int? MinAge,
        int? MaxAge,
        int? MaxPerDay,
        FrequencyLimit? Frequency,
        IReadOnlyList<string>? RequiredIcdPrefixes,
        IReadOnlyList<string>? AllowedBranches,
        bool InpatientOnly,
        IReadOnlyList<string>? PackageContents)
    {
        public ProcedureDefinition ToDefinition() => new(
            Code, NameTr, NameEn, Kind, Price, Gender, MinAge, MaxAge, MaxPerDay, Frequency,
            Sorted(RequiredIcdPrefixes), Sorted(AllowedBranches), InpatientOnly, Sorted(PackageContents));
    }

    /// <summary>A partial amendment of an inherited procedure; only the fields that are present change.</summary>
    private sealed record ProcedureChange(decimal? Price, int? MinAge, int? MaxAge, int? MaxPerDay, FrequencyLimit? Frequency)
    {
        public ProcedureDefinition ApplyTo(ProcedureDefinition p) => p with
        {
            Price = Price ?? p.Price,
            MinAge = MinAge ?? p.MinAge,
            MaxAge = MaxAge ?? p.MaxAge,
            MaxPerDay = MaxPerDay ?? p.MaxPerDay,
            Frequency = Frequency ?? p.Frequency,
        };
    }

    // Ordinal order keeps joined lists in suggestion texts stable across runs and engines.
    private static string[] Sorted(IReadOnlyList<string>? values) =>
        values is null ? [] : [.. values.Order(StringComparer.Ordinal)];
}
