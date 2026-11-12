namespace SutRules.Domain;

// The invoice model has the same JSON shape as medula-precheck's, so both engines read the same files and a
// parity run can compare them invoice by invoice.

/// <summary>Patient sex as MEDULA records it (K = kadın / female, E = erkek / male).</summary>
public enum Gender
{
    Female,
    Male,
}

/// <summary>Treatment setting of the provision (ayaktan / yatarak / günübirlik).</summary>
public enum TreatmentType
{
    Outpatient,
    Inpatient,
    DayCase,
}

/// <summary>Provision reason; accident and forensic cases carry extra coding obligations.</summary>
public enum ProvisionType
{
    Normal,
    Emergency,
    WorkAccident,
    TrafficAccident,
    Forensic,
}

/// <summary>
/// Patient demographics the rules need. <see cref="PatientRef"/> is an opaque synthetic token, not a national id,
/// and the model has no name field: a rule needs age and sex, not identity (data minimisation, KVKK art. 4).
/// </summary>
public sealed record Patient(string PatientRef, DateOnly BirthDate, Gender Gender)
{
    /// <summary>Age in completed years on <paramref name="date"/> (29 February birthdays count from 1 March).</summary>
    public int AgeOn(DateOnly date) => Age(BirthDate, date);

    /// <summary>Completed years between <paramref name="birth"/> and <paramref name="on"/>.</summary>
    public static int Age(DateOnly birth, DateOnly on)
    {
        var age = on.Year - birth.Year;
        if (on.Month < birth.Month || (on.Month == birth.Month && on.Day < birth.Day))
        {
            age--;
        }

        return age;
    }
}

/// <summary>The MEDULA provision (provizyon) and its tracking number (takip numarası).</summary>
public sealed record Provision(
    string TrackingNo,
    DateOnly AdmissionDate,
    DateOnly? DischargeDate,
    TreatmentType TreatmentType,
    ProvisionType ProvisionType,
    string BranchCode);

/// <summary>A physician on the invoice, identified by a (synthetic) registration number and specialty branch.</summary>
public sealed record Doctor(string RegistrationNo, string BranchCode);

/// <summary>An ICD-10 diagnosis attached to the provision.</summary>
public sealed record Diagnosis(string IcdCode, bool IsPrimary);

/// <summary>A billed service line (hizmet kalemi).</summary>
public sealed record ServiceLine(
    int LineNo,
    string SutCode,
    DateOnly ServiceDate,
    int Quantity,
    decimal UnitPrice,
    string DoctorRegistrationNo)
{
    public decimal Amount => Quantity * UnitPrice;
}

/// <summary>A service already billed for the same patient before this invoice, used by cross-invoice rules.</summary>
public sealed record PriorService(string SutCode, DateOnly ServiceDate, int Quantity, string BranchCode);

/// <summary>The invoice (fatura) as it would be sent to SGK through MEDULA.</summary>
public sealed record Invoice(
    string InvoiceNo,
    DateOnly InvoiceDate,
    string FacilityCode,
    Patient Patient,
    Provision Provision,
    IReadOnlyList<Doctor> Doctors,
    IReadOnlyList<Diagnosis> Diagnoses,
    IReadOnlyList<ServiceLine> Lines,
    IReadOnlyList<PriorService> PriorServices)
{
    public decimal Total
    {
        get
        {
            var total = 0m;
            foreach (var line in Lines)
            {
                total += line.Amount;
            }

            return total;
        }
    }
}

/// <summary>The request and file format: <c>{ "invoices": [ ... ] }</c>.</summary>
public sealed record InvoiceBatch(IReadOnlyList<Invoice> Invoices);
