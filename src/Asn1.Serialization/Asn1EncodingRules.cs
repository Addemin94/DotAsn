using System.Collections.Frozen;
using Asn1.Runtime.Ber;
using Asn1.Runtime.Jer;
using Asn1.Runtime.Oer;
using Asn1.Runtime.Per;
using Asn1.Runtime.Xer;

namespace Asn1.Runtime;

/// <summary>The implemented encoding rules, for use with <see cref="Asn1Serializer"/>.</summary>
public static class Asn1EncodingRules
{
    /// <summary>Basic Encoding Rules (X.690).</summary>
    public static Asn1EncodingRule Ber => BerEncodingRule.Instance;

    /// <summary>Canonical Encoding Rules (X.690).</summary>
    public static Asn1EncodingRule Cer => CerEncodingRule.Instance;

    /// <summary>Distinguished Encoding Rules (X.690).</summary>
    public static Asn1EncodingRule Der => DerEncodingRule.Instance;

    /// <summary>Packed Encoding Rules, ALIGNED variant (X.691).</summary>
    public static Asn1EncodingRule Aper => PerEncodingRule.Aligned;

    /// <summary>Packed Encoding Rules, UNALIGNED variant (X.691).</summary>
    public static Asn1EncodingRule Uper => PerEncodingRule.Unaligned;

    /// <summary>Octet Encoding Rules, BASIC-OER (X.696).</summary>
    public static Asn1EncodingRule Oer => OerEncodingRule.Basic;

    /// <summary>Octet Encoding Rules, CANONICAL-OER (X.696 clause 31).</summary>
    public static Asn1EncodingRule Coer => OerEncodingRule.Canonical;

    /// <summary>JavaScript Object Notation Encoding Rules (X.697), without encoding instructions.</summary>
    public static Asn1EncodingRule Jer => JerEncodingRule.Instance;

    /// <summary>XML Encoding Rules, BASIC-XER (X.693).</summary>
    public static Asn1EncodingRule Xer => XerEncodingRule.Basic;

    /// <summary>XML Encoding Rules, CANONICAL-XER (X.693 clause 9).</summary>
    public static Asn1EncodingRule Cxer => XerEncodingRule.Canonical;

    private static readonly FrozenDictionary<string, Asn1EncodingRule> s_byName = new Dictionary<string, Asn1EncodingRule>(StringComparer.OrdinalIgnoreCase)
    {
        ["ber"] = Ber,
        ["cer"] = Cer,
        ["der"] = Der,
        ["aper"] = Aper,
        ["per"] = Aper,
        ["uper"] = Uper,
        ["oer"] = Oer,
        ["coer"] = Coer,
        ["jer"] = Jer,
        ["xer"] = Xer,
        ["cxer"] = Cxer,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Encoding rules defined by the standards but not implemented yet.</summary>
    public static IReadOnlyList<string> NotImplemented { get; } = ["EXER"];

    /// <summary>All implemented rules.</summary>
    public static IReadOnlyList<Asn1EncodingRule> All { get; } = [Ber, Cer, Der, Aper, Uper, Oer, Coer, Jer, Xer, Cxer];

    /// <summary>Looks up a rule by name (case-insensitive; "per" means APER).</summary>
    public static bool TryGet(string name, out Asn1EncodingRule rule) => s_byName.TryGetValue(name, out rule!);
}
