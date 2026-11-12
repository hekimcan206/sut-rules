using SutRules.Catalog;
using SutRules.Domain;

namespace SutRules.Engine;

/// <summary>One invoice evaluated against one SUT version, with lookups built at most once per invoice.</summary>
public sealed class EvalContext
{
    private Dictionary<string, Doctor>? _doctors;

    public EvalContext(Invoice invoice, SutVersion version)
    {
        Invoice = invoice;
        Version = version;
    }

    public Invoice Invoice { get; }

    public SutVersion Version { get; }

    public ProcedureDefinition? Procedure(string? code) =>
        code is not null && Version.Procedures.TryGetValue(code, out var p) ? p : null;

    public Doctor? Doctor(string? registrationNo)
    {
        if (registrationNo is null)
        {
            return null;
        }

        if (_doctors is null)
        {
            // Last one wins on duplicates: a repeated registration is a data-entry issue, not a billing rule.
            _doctors = new Dictionary<string, Doctor>(StringComparer.Ordinal);
            foreach (var d in Invoice.Doctors)
            {
                _doctors[d.RegistrationNo] = d;
            }
        }

        return _doctors.GetValueOrDefault(registrationNo);
    }
}

/// <summary>Mutable evaluation state of one check: local slots, the rule's parameter values for the version, the context.</summary>
public sealed class Frame
{
    public Frame(int slots, object?[] parameters, EvalContext context)
    {
        Slots = new object?[slots];
        Parameters = parameters;
        Context = context;
    }

    public object?[] Slots { get; }

    public object?[] Parameters { get; }

    public EvalContext Context { get; }
}

/// <summary>A compiled expression: a closure over pre-resolved slots, getters and operators.</summary>
public delegate object? Eval(Frame frame);
