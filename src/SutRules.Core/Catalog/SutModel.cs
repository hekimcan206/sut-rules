using SutRules.Domain;

namespace SutRules.Catalog;

/// <summary>How a SUT code is billed. Packages follow the diagnosis-based list (EK-2/C) and absorb their contents.</summary>
public enum ProcedureKind
{
    Examination,
    Laboratory,
    Imaging,
    Procedure,
    Package,
    BedDay,
    Consumable,
}

/// <summary>At most <see cref="Count"/> units in any rolling window of <see cref="Days"/> days.</summary>
public sealed record FrequencyLimit(int Count, int Days);

/// <summary>One billable code in one SUT version, with every constraint the rules read.</summary>
public sealed record ProcedureDefinition(
    string Code,
    string NameTr,
    string NameEn,
    ProcedureKind Kind,
    decimal Price,
    Gender? Gender,
    int? MinAge,
    int? MaxAge,
    int? MaxPerDay,
    FrequencyLimit? Frequency,
    IReadOnlyList<string> RequiredIcdPrefixes,
    IReadOnlyList<string> AllowedBranches,
    bool InpatientOnly,
    IReadOnlyList<string> PackageContents);

/// <summary>Codes that may not be billed on the same day: the <see cref="Secondary"/> one is rejected.</summary>
public sealed record IncompatiblePair(string Primary, string Secondary);

/// <summary>ICD-10 three-character category range [From, To] that is valid for one sex only (e.g. O00-O9A).</summary>
public sealed record DiagnosisGenderRange(string From, string To, Gender Gender);

/// <summary>Specialty branch reference entry.</summary>
public sealed record Branch(string Code, string NameTr, string NameEn);

/// <summary>
/// A resolved, immutable SUT version: the reference data valid in [EffectiveFrom, EffectiveTo). Which rules apply
/// and with what severity is not stored here: each rule file says that for itself.
/// </summary>
public sealed class SutVersion
{
    public required string Id { get; init; }

    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Exclusive upper bound; null for the current version.</summary>
    public DateOnly? EffectiveTo { get; init; }

    public required string DescriptionEn { get; init; }

    /// <summary>Position in the catalog timeline (0 = oldest); rule version blocks are resolved by it.</summary>
    public required int Ordinal { get; init; }

    public required IReadOnlyDictionary<string, ProcedureDefinition> Procedures { get; init; }

    public required IReadOnlyList<IncompatiblePair> IncompatiblePairs { get; init; }

    public required IReadOnlyList<DiagnosisGenderRange> DiagnosisGender { get; init; }

    public bool IsValidOn(DateOnly date) => date >= EffectiveFrom && (EffectiveTo is null || date < EffectiveTo);
}
