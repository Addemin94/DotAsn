using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Asn1.Runtime;

/// <summary>Formats values in ASN.1 value notation (used by generated <c>ToString()</c> implementations).</summary>
public static class Asn1ValueFormatter
{
    /// <summary>Formats a typed value.</summary>
    public static string Format<T>(Asn1Type<T> type, T value)
    {
        var sb = new StringBuilder();
        Append(sb, type, value, 0);
        return sb.ToString();
    }

    private static void Append<T>(StringBuilder sb, Asn1Type<T> type, T value, int depth)
    {
        if (depth > 64)
        {
            sb.Append("...");
            return;
        }

        switch (type)
        {
            case SequenceType<T> seq:
                {
                    sb.Append("{ ");
                    var first = true;
                    foreach (var c in seq.Components)
                    {
                        if (!c.IsPresent(value))
                        {
                            continue;
                        }

                        if (!first)
                        {
                            sb.Append(", ");
                        }

                        first = false;
                        sb.Append(c.Name).Append(' ');
                        AppendBoxed(sb, c.Type, c.GetBoxed(value), depth + 1);
                    }

                    sb.Append(first ? "}" : " }");
                    return;
                }

            case ChoiceType<T> choice:
                {
                    var index = choice.GetIndex(value);
                    if (index < 0)
                    {
                        sb.Append("<unknown extension>");
                        return;
                    }

                    var alt = choice.Alternatives[index];
                    sb.Append(alt.Name).Append(" : ");
                    AppendBoxed(sb, alt.Type, alt.GetBoxed(value), depth + 1);
                    return;
                }

            case EnumeratedType<T> e:
                sb.Append(e.ItemOf(value)?.Name ?? value?.ToString());
                return;
            default:
                AppendBoxed(sb, type, value, depth);
                return;
        }
    }

    private static void AppendBoxed(StringBuilder sb, Asn1Type type, object? value, int depth)
    {
        switch (value)
        {
            case null:
                sb.Append("<absent>");
                return;
            case bool b:
                sb.Append(b ? "TRUE" : "FALSE");
                return;
            case string s:
                sb.Append('"').Append(s.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                return;
            case byte[] bytes:
                sb.Append('\'').Append(Convert.ToHexString(bytes)).Append("'H");
                return;
            case double d:
                sb.Append(double.IsPositiveInfinity(d) ? "PLUS-INFINITY" : double.IsNegativeInfinity(d) ? "MINUS-INFINITY" : double.IsNaN(d) ? "NOT-A-NUMBER" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case ObjectIdentifier oid:
                sb.Append("{ ").Append(string.Join(' ', oid.Arcs)).Append(" }");
                return;
            case Asn1Null:
                sb.Append("NULL");
                return;
            case IFormattable f when value is int or long or BigInteger or uint or ulong or short:
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
                return;
            case IList list when type is ICollectionTypeInfo c:
                {
                    sb.Append("{ ");
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }

                        AppendBoxed(sb, c.ElementTypeInfo, list[i], depth + 1);
                    }

                    sb.Append(list.Count == 0 ? "}" : " }");
                    return;
                }

            default:
                if (type is IFormatDispatch dispatch)
                {
                    dispatch.AppendFormatted(sb, value, depth);
                    return;
                }

                sb.Append(value);
                return;
        }
    }

    /// <summary>Formats a value whose descriptor is only known non-generically.</summary>
    internal static void AppendTyped<T>(StringBuilder sb, Asn1Type<T> type, object? value, int depth) => Append(sb, type, (T)value!, depth);
}

/// <summary>Lets the non-generic formatter re-enter typed formatting for structured descriptors.</summary>
internal interface IFormatDispatch
{
    void AppendFormatted(StringBuilder sb, object? value, int depth);
}
