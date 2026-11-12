namespace SutRules.Language;

/// <summary>
/// Static types of the rule language. Every type admits null (see ADR 0002): reference data is sparse, most
/// procedures have no age limit or frequency, and a rule reads more naturally as "min_age is set and the patient
/// is younger" than with option types.
/// </summary>
public abstract record DslType(string Name)
{
    public static readonly PrimitiveType Bool = new("bool");
    public static readonly PrimitiveType Int = new("int");
    public static readonly PrimitiveType Decimal = new("decimal");
    public static readonly PrimitiveType String = new("string");
    public static readonly PrimitiveType Date = new("date");

    /// <summary>The type of the <c>null</c> literal; compatible with every type.</summary>
    public static readonly PrimitiveType Null = new("null");

    public bool IsNumeric => this == Int || this == Decimal;

    /// <summary>Types with an order: numbers, strings (ordinal) and dates.</summary>
    public bool IsOrdered => IsNumeric || this == String || this == Date;

    public override string ToString() => Name;

    /// <summary>
    /// The common type of two operands, or null when they are incompatible. int widens to decimal, null adopts the
    /// other type, lists unify element-wise.
    /// </summary>
    public static DslType? Unify(DslType a, DslType b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a == b)
        {
            return a;
        }

        if (a == Null)
        {
            return b;
        }

        if (b == Null)
        {
            return a;
        }

        if (a.IsNumeric && b.IsNumeric)
        {
            return Decimal;
        }

        if (a is ListType la && b is ListType lb && Unify(la.Element, lb.Element) is { } element)
        {
            return new ListType(element);
        }

        return null;
    }
}

public sealed record PrimitiveType(string Name) : DslType(Name)
{
    public override string ToString() => Name;
}

/// <summary>An enumeration; values are carried at run time as their member name (a string).</summary>
public sealed record EnumType(string Name, IReadOnlyList<string> Members) : DslType(Name)
{
    public bool Equals(EnumType? other) => other is not null && other.Name == Name;

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Name;
}

public sealed record ListType(DslType Element) : DslType("list<" + Element.Name + ">")
{
    public override string ToString() => Name;
}

/// <summary>A record exposed to rules; fields read CLR objects through precompiled getters.</summary>
public sealed record RecordType(string Name) : DslType(Name)
{
    private readonly Dictionary<string, FieldInfo> _fields = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, FieldInfo> Fields => _fields;

    public RecordType Field(string field, DslType type, Func<object, object?> getter, string description)
    {
        _fields.Add(field, new FieldInfo(field, type, getter, description));
        return this;
    }

    // Records are nominal: two record types are the same type only if they are the same schema object.
    public bool Equals(RecordType? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Name;
}

public sealed record FieldInfo(string Name, DslType Type, Func<object, object?> Getter, string Description);
