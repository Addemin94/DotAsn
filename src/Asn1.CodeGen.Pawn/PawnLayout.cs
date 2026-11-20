using System.Numerics;
using Asn1.Semantics;

namespace Asn1.CodeGen.Pawn;

/// <summary>How a value of one ASN.1 type sits in a Pawn cell array, and how OER encodes it.</summary>
internal abstract class PawnLayout
{
    /// <summary>The cells the value takes.</summary>
    public abstract int Cells { get; }
}

internal sealed class BooleanLayout : PawnLayout
{
    public override int Cells => 1;
}

internal enum IntegerForm
{
    UnsignedFixed,
    UnsignedVariable,
    SignedFixed,
    SignedVariable,
}

internal sealed class IntegerLayout(IntegerForm form, int octets, BigInteger? lower, BigInteger? upper) : PawnLayout
{
    public IntegerForm Form { get; } = form;

    /// <summary>Octets of the fixed forms, 0 for the variable forms.</summary>
    public int Octets { get; } = octets;

    public BigInteger? Lower { get; } = lower;

    public BigInteger? Upper { get; } = upper;

    public bool IsSigned => Form is IntegerForm.SignedFixed or IntegerForm.SignedVariable;

    public override int Cells => 1;
}

internal sealed record EnumeratedItem(string Name, long Value);

internal sealed class EnumeratedLayout(IReadOnlyList<EnumeratedItem> items, bool isExtensible) : PawnLayout
{
    public IReadOnlyList<EnumeratedItem> Items { get; } = items;

    public bool IsExtensible { get; } = isExtensible;

    public override int Cells => 1;
}

internal sealed class NullLayout : PawnLayout
{
    public override int Cells => 0;
}

internal enum StringKind
{
    Octets,
    Bits,
    KnownMultiplierCharacters,
    Utf8Characters,
}

/// <summary>
/// Octet, bit and character strings: an optional length cell (octets, bits or UTF-8 octets),
/// followed by the content as packed bytes.
/// </summary>
internal sealed class StringLayout(StringKind kind, long minimum, long maximum, bool isFixed) : PawnLayout
{
    public StringKind Kind { get; } = kind;

    /// <summary>The smallest length, in bits for a bit string, otherwise in octets.</summary>
    public long Minimum { get; } = minimum;

    /// <summary>The largest length, in bits for a bit string, otherwise in octets.</summary>
    public long Maximum { get; } = maximum;

    /// <summary>A fixed size has no length cell and no length on the wire.</summary>
    public bool IsFixed { get; } = isFixed;

    public long MaximumOctets => Kind == StringKind.Bits ? (Maximum + 7) / 8 : Maximum;

    /// <summary>The cell offset of the content within this layout.</summary>
    public int DataOffset => IsFixed ? 0 : 1;

    public override int Cells => DataOffset + (int)((MaximumOctets + 3) / 4);
}

internal enum FieldPresence
{
    Required,
    Optional,
    Default,
}

internal sealed class SequenceField(string name, FieldPresence presence, PawnLayout layout, long? defaultValue)
{
    public string Name { get; } = name;

    public FieldPresence Presence { get; } = presence;

    public PawnLayout Layout { get; } = layout;

    /// <summary>The DEFAULT value of an INTEGER, BOOLEAN or ENUMERATED field, compared on encoding.</summary>
    public long? DefaultValue { get; } = defaultValue;

    public bool HasPresenceCell => Presence != FieldPresence.Required;

    /// <summary>Cell offset of the presence flag, -1 without one.</summary>
    public int PresenceOffset { get; set; } = -1;

    public int ValueOffset { get; set; }
}

internal sealed class SequenceLayout(IReadOnlyList<SequenceField> fields, bool isExtensible) : PawnLayout
{
    public IReadOnlyList<SequenceField> Fields { get; } = fields;

    public bool IsExtensible { get; } = isExtensible;

    public int PreambleBits => (IsExtensible ? 1 : 0) + Fields.Count(f => f.HasPresenceCell);

    public int PreambleOctets => (PreambleBits + 7) / 8;

    public override int Cells => Fields.Sum(f => (f.HasPresenceCell ? 1 : 0) + f.Layout.Cells);
}

internal sealed record ChoiceAlternative(string Name, int Index, byte TagOctet, PawnLayout Layout);

/// <summary>A CHOICE: the chosen alternative's index in the first cell, then the alternative's value.</summary>
internal sealed class ChoiceLayout(IReadOnlyList<ChoiceAlternative> alternatives) : PawnLayout
{
    public IReadOnlyList<ChoiceAlternative> Alternatives { get; } = alternatives;

    public override int Cells => 1 + Alternatives.Max(a => a.Layout.Cells);
}

/// <summary>A SEQUENCE OF: the item count in the first cell, then room for the largest number of items.</summary>
internal sealed class SequenceOfLayout(long minimum, long maximum, PawnLayout element) : PawnLayout
{
    public long Minimum { get; } = minimum;

    public long Maximum { get; } = maximum;

    public PawnLayout Element { get; } = element;

    public override int Cells => 1 + (int)(Maximum * Element.Cells);
}

/// <summary>A reference to a generated type: encoded and decoded by calling that type's functions.</summary>
internal sealed class ReferenceLayout(PawnTypeEntry target) : PawnLayout
{
    public PawnTypeEntry Target { get; } = target;

    public override int Cells => Target.Layout!.Cells;
}

/// <summary>A generated type assignment.</summary>
internal sealed class PawnTypeEntry(TypeAssignmentSymbol assignment, string prefix)
{
    public TypeAssignmentSymbol Assignment { get; } = assignment;

    public string AsnName => Assignment.QualifiedName;

    /// <summary>The Pawn name prefix of the type's constants and functions.</summary>
    public string Prefix { get; } = prefix;

    public PawnLayout? Layout { get; set; }

    public string EncodeName { get; set; } = string.Empty;

    public string DecodeName { get; set; } = string.Empty;

    public string CellsName { get; set; } = string.Empty;
}
