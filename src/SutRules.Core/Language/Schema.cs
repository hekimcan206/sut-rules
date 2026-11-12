using SutRules.Catalog;
using SutRules.Domain;

namespace SutRules.Language;

/// <summary>
/// What a rule can see: the root names, their record types and the fields of each. This is the contract between
/// rule authors and the engine; the reference in <c>docs/language.md</c> is generated from these descriptions.
/// Field names are snake_case so rule text reads like the SUT annex columns rather than C# properties.
/// </summary>
public static class Schema
{
    public static readonly EnumType Sex = new("sex", Enum.GetNames<Gender>());
    public static readonly EnumType Treatment = new("treatment", Enum.GetNames<TreatmentType>());
    public static readonly EnumType ProvisionKind = new("provision_type", Enum.GetNames<ProvisionType>());
    public static readonly EnumType Kind = new("procedure_kind", Enum.GetNames<ProcedureKind>());

    public static readonly IReadOnlyList<EnumType> Enums = [Sex, Treatment, ProvisionKind, Kind];

    // Enum values cross into the language as their member names; cached so a field read never allocates.
    private static readonly string[] GenderNames = Enum.GetNames<Gender>();
    private static readonly string[] TreatmentNames = Enum.GetNames<TreatmentType>();
    private static readonly string[] ProvisionNames = Enum.GetNames<ProvisionType>();
    private static readonly string[] KindNames = Enum.GetNames<ProcedureKind>();

    public static readonly RecordType Patient = new RecordType("patient")
        .Field("ref", DslType.String, o => ((Patient)o).PatientRef, "Opaque synthetic patient token (never a national id).")
        .Field("birth_date", DslType.Date, o => ((Patient)o).BirthDate, "Date of birth.")
        .Field("sex", Sex, o => GenderNames[(int)((Patient)o).Gender], "Female or Male.");

    public static readonly RecordType Provision = new RecordType("provision")
        .Field("tracking_no", DslType.String, o => ((Provision)o).TrackingNo, "MEDULA tracking number (takip numarası).")
        .Field("admission", DslType.Date, o => ((Provision)o).AdmissionDate, "Admission date.")
        .Field("discharge", DslType.Date, o => ((Provision)o).DischargeDate, "Discharge date; null while admitted or for outpatients.")
        .Field("treatment", Treatment, o => TreatmentNames[(int)((Provision)o).TreatmentType], "Outpatient, Inpatient or DayCase.")
        .Field("type", ProvisionKind, o => ProvisionNames[(int)((Provision)o).ProvisionType], "Normal, Emergency, WorkAccident, TrafficAccident or Forensic.")
        .Field("branch", DslType.String, o => ((Provision)o).BranchCode, "Branch code of the provision.");

    public static readonly RecordType Doctor = new RecordType("doctor")
        .Field("registration", DslType.String, o => ((Doctor)o).RegistrationNo, "Physician registration number.")
        .Field("branch", DslType.String, o => ((Doctor)o).BranchCode, "Specialty branch code.");

    public static readonly RecordType Diagnosis = new RecordType("diagnosis")
        .Field("icd", DslType.String, o => ((Diagnosis)o).IcdCode, "ICD-10 code.")
        .Field("primary", DslType.Bool, o => Values.Bool(((Diagnosis)o).IsPrimary), "True for the primary diagnosis.");

    public static readonly RecordType Line = new RecordType("line")
        .Field("no", DslType.Int, o => Values.Int(((ServiceLine)o).LineNo), "Line number.")
        .Field("code", DslType.String, o => ((ServiceLine)o).SutCode, "SUT code.")
        .Field("date", DslType.Date, o => ((ServiceLine)o).ServiceDate, "Service date.")
        .Field("quantity", DslType.Int, o => Values.Int(((ServiceLine)o).Quantity), "Billed quantity.")
        .Field("unit_price", DslType.Decimal, o => ((ServiceLine)o).UnitPrice, "Billed unit price (TRY).")
        .Field("amount", DslType.Decimal, o => ((ServiceLine)o).Amount, "quantity * unit_price.")
        .Field("doctor", DslType.String, o => ((ServiceLine)o).DoctorRegistrationNo, "Registration number of the performing physician.");

    public static readonly RecordType Prior = new RecordType("prior_service")
        .Field("code", DslType.String, o => ((PriorService)o).SutCode, "SUT code billed earlier.")
        .Field("date", DslType.Date, o => ((PriorService)o).ServiceDate, "Service date of the earlier billing.")
        .Field("quantity", DslType.Int, o => Values.Int(((PriorService)o).Quantity), "Quantity billed earlier.")
        .Field("branch", DslType.String, o => ((PriorService)o).BranchCode, "Branch that performed it.");

    public static readonly RecordType Invoice = new RecordType("invoice")
        .Field("no", DslType.String, o => ((Invoice)o).InvoiceNo, "Invoice number.")
        .Field("date", DslType.Date, o => ((Invoice)o).InvoiceDate, "Invoice date; selects the SUT version.")
        .Field("facility", DslType.String, o => ((Invoice)o).FacilityCode, "Facility code.")
        .Field("total", DslType.Decimal, o => ((Invoice)o).Total, "Sum of line amounts.");

    public static readonly RecordType Frequency = new RecordType("frequency")
        .Field("count", DslType.Int, o => Values.Int(((FrequencyLimit)o).Count), "Maximum units in the window.")
        .Field("days", DslType.Int, o => Values.Int(((FrequencyLimit)o).Days), "Rolling window length in days.");

    public static readonly RecordType Procedure = new RecordType("procedure")
        .Field("code", DslType.String, o => ((ProcedureDefinition)o).Code, "SUT code.")
        .Field("name_tr", DslType.String, o => ((ProcedureDefinition)o).NameTr, "Turkish name.")
        .Field("name_en", DslType.String, o => ((ProcedureDefinition)o).NameEn, "English name.")
        .Field("kind", Kind, o => KindNames[(int)((ProcedureDefinition)o).Kind], "Examination, Laboratory, Imaging, Procedure, Package, BedDay or Consumable.")
        .Field("price", DslType.Decimal, o => ((ProcedureDefinition)o).Price, "Tariff price in this SUT version.")
        .Field("sex", Sex, o => ((ProcedureDefinition)o).Gender is { } g ? GenderNames[(int)g] : null, "Only billable for this sex; null when unrestricted.")
        .Field("min_age", DslType.Int, o => Values.Int(((ProcedureDefinition)o).MinAge), "Minimum age in completed years, or null.")
        .Field("max_age", DslType.Int, o => Values.Int(((ProcedureDefinition)o).MaxAge), "Maximum age in completed years, or null.")
        .Field("max_per_day", DslType.Int, o => Values.Int(((ProcedureDefinition)o).MaxPerDay), "Daily quantity limit, or null.")
        .Field("frequency", Frequency, o => ((ProcedureDefinition)o).Frequency, "Rolling-window frequency limit, or null.")
        .Field("required_icd", new ListType(DslType.String), o => ((ProcedureDefinition)o).RequiredIcdPrefixes, "ICD-10 prefixes, one of which must be on the invoice.")
        .Field("allowed_branches", new ListType(DslType.String), o => ((ProcedureDefinition)o).AllowedBranches, "Branches allowed to perform it; empty = any.")
        .Field("inpatient_only", DslType.Bool, o => Values.Bool(((ProcedureDefinition)o).InpatientOnly), "Billable in inpatient care only.")
        .Field("package_contents", new ListType(DslType.String), o => ((ProcedureDefinition)o).PackageContents, "Codes a package absorbs.");

    public static readonly RecordType Pair = new RecordType("incompatible_pair")
        .Field("primary", DslType.String, o => ((IncompatiblePair)o).Primary, "The code that stays.")
        .Field("secondary", DslType.String, o => ((IncompatiblePair)o).Secondary, "The code rejected when billed the same day.");

    public static readonly RecordType SexRange = new RecordType("diagnosis_sex_range")
        .Field("from", DslType.String, o => ((DiagnosisGenderRange)o).From, "First ICD-10 category of the range.")
        .Field("to", DslType.String, o => ((DiagnosisGenderRange)o).To, "Last ICD-10 category of the range.")
        .Field("sex", Sex, o => GenderNames[(int)((DiagnosisGenderRange)o).Gender], "The only sex the range is valid for.");

    public static readonly RecordType Sut = new RecordType("sut_version")
        .Field("id", DslType.String, o => ((SutVersion)o).Id, "SUT version id, e.g. SUT-2026.01.")
        .Field("effective_from", DslType.Date, o => ((SutVersion)o).EffectiveFrom, "First day the version is in force.")
        .Field("incompatible_pairs", new ListType(Pair), o => ((SutVersion)o).IncompatiblePairs, "Codes that may not be billed on the same day.")
        .Field("diagnosis_sex", new ListType(SexRange), o => ((SutVersion)o).DiagnosisGender, "Sex-specific ICD-10 ranges.");

    /// <summary>Root names visible in every rule, with how each is read from the evaluation context.</summary>
    public static readonly IReadOnlyDictionary<string, RootInfo> Roots = new Dictionary<string, RootInfo>(StringComparer.Ordinal)
    {
        ["invoice"] = new(Invoice, c => c.Invoice, "The invoice being checked."),
        ["patient"] = new(Patient, c => c.Invoice.Patient, "invoice.patient."),
        ["provision"] = new(Provision, c => c.Invoice.Provision, "The MEDULA provision."),
        ["lines"] = new(new ListType(Line), c => c.Invoice.Lines, "Service lines in invoice order."),
        ["diagnoses"] = new(new ListType(Diagnosis), c => c.Invoice.Diagnoses, "Diagnoses on the provision."),
        ["doctors"] = new(new ListType(Doctor), c => c.Invoice.Doctors, "Physicians listed on the invoice."),
        ["prior"] = new(new ListType(Prior), c => c.Invoice.PriorServices, "Services billed for the patient before this invoice."),
        ["sut"] = new(Sut, c => c.Version, "The SUT version valid on the invoice date."),
    };

    public static readonly IReadOnlyList<RecordType> Records =
        [Invoice, Patient, Provision, Line, Diagnosis, Doctor, Prior, Procedure, Frequency, Sut, Pair, SexRange];

    public static DslType? ParamType(string name) => name switch
    {
        "int" => DslType.Int,
        "decimal" => DslType.Decimal,
        "string" => DslType.String,
        "bool" => DslType.Bool,
        "date" => DslType.Date,
        _ => null,
    };
}

public sealed record RootInfo(DslType Type, Func<Engine.EvalContext, object?> Read, string Description);
