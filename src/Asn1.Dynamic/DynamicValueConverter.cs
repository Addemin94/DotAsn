using Asn1.Runtime;
using Asn1.Semantics;

namespace Asn1.Dynamic;

/// <summary>Converts resolved semantic values (from ASN.1 value notation) into the dynamic value model.</summary>
public static class DynamicValueConverter
{
    /// <summary>Converts a semantic value of type <paramref name="type"/>; returns null for unsupported values.</summary>
    public static object? FromSemanticValue(AsnValue value, AsnType type)
    {
        var def = type.Definition;
        switch (value)
        {
            case IntegerValue i:
                return type.Kind == TypeKind.Real ? (double)i.Value : i.Value;
            case EnumeratedValue e:
                return e.Name;
            case SequenceValue s:
                {
                    var record = new Asn1Record();
                    foreach (var nv in s.Components)
                    {
                        var comp = def.Components.FirstOrDefault(c => c.Name == nv.Name);
                        if (comp is not null)
                        {
                            record[nv.Name] = FromSemanticValue(nv.Value, comp.Type);
                        }
                    }

                    return record;
                }

            case SequenceOfValue so:
                return def.ElementType is { } et ? so.Items.Select(i => FromSemanticValue(i, et)).ToList() : null;
            case ChoiceValue cv:
                {
                    var alt = def.Components.FirstOrDefault(c => c.Name == cv.Alternative);
                    return alt is null ? null : new Asn1Choice(cv.Alternative, FromSemanticValue(cv.Value, alt.Type));
                }

            case OpenTypeValue ov:
                {
                    var descriptor = new DynamicTypeFactory().Create(ov.Type);
                    return Asn1OpenValue.Create(descriptor, FromSemanticValue(ov.Value, ov.Type));
                }

            case ErrorValue:
                return null;
            default:
                return value.ToRuntimeObject();
        }
    }
}
