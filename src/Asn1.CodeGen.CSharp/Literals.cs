using System.Numerics;
using Asn1.Constraints;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Asn1.CodeGen.CSharp.Cs;

namespace Asn1.CodeGen.CSharp;

/// <summary>Syntax for constant values, constraint trees and tag lists as they appear in generated descriptors.</summary>
internal static class Literals
{
    public static ExpressionSyntax Bits(Asn1BitString bits) => Call(Member(Core("Asn1BitString"), "FromBinary"), Str(bits.ToBinaryString()));

    /// <summary>Expression for a constraint value in its canonical runtime representation.</summary>
    public static ExpressionSyntax Object(object? value) => value switch
    {
        null => Null,
        BigInteger b => Big(b),
        string s => Str(s),
        bool b => Bool(b),
        double d => Float64(d),
        byte[] bytes => NewByteArray(bytes),
        Asn1BitString bits => Bits(bits),
        ObjectIdentifier oid => Call(Member(Core("ObjectIdentifier"), "Parse"), Str(oid.ToString())),
        RelativeObjectIdentifier rel => Call(Member(Core("RelativeObjectIdentifier"), "Parse"), Str(rel.ToString())),
        Asn1Null => Member(Core("Asn1Null"), "Value"),
        int i => Int32(i),
        _ => throw new NotSupportedException($"constraint value of type {value.GetType().Name}"),
    };

    /// <summary>A collection expression of constraints (<c>[]</c> when empty).</summary>
    public static ExpressionSyntax ConstraintList(IEnumerable<Asn1Constraint> constraints) => Collection(constraints.Select(Constraint));

    public static ExpressionSyntax Constraint(Asn1Constraint c) =>
        New(ConstraintModel("Asn1Constraint"), c.Root is null ? Null : Element(c.Root), Bool(c.IsExtensible), c.Additions is null ? Null : Element(c.Additions));

    public static ExpressionSyntax Element(ConstraintElement e) => e switch
    {
        AllValuesElement => Member(ConstraintModel("AllValuesElement"), "Instance"),
        SingleValueElement sv => New(ConstraintModel("SingleValueElement"), Object(sv.Value)),
        ValueRangeElement r => New(ConstraintModel("ValueRangeElement"), Object(r.Lower), Object(r.Upper), Bool(r.LowerOpen), Bool(r.UpperOpen)),
        SizeElement s => New(ConstraintModel("SizeElement"), Constraint(s.Constraint)),
        PermittedAlphabetElement p => New(ConstraintModel("PermittedAlphabetElement"), Constraint(p.Constraint)),
        PatternElement p => New(ConstraintModel("PatternElement"), Str(p.Pattern)),
        InnerTypeElement it => New(ConstraintModel("InnerTypeElement"), Constraint(it.Constraint)),
        InnerComponentsElement ic => New(ConstraintModel("InnerComponentsElement"), Bool(ic.IsPartial), Collection(ic.Components.Select(c =>
            New(ConstraintModel("ComponentConstraint"), Str(c.Name), c.Value is null ? Null : Constraint(c.Value), Member(ConstraintModel("ComponentPresenceConstraint"), c.Presence.ToString()))))),
        UnionElement u => New(ConstraintModel("UnionElement"), Collection(u.Items.Select(Element))),
        IntersectionElement x => New(ConstraintModel("IntersectionElement"), Collection(x.Items.Select(Element))),
        ExceptElement x => New(ConstraintModel("ExceptElement"), Element(x.Included), Element(x.Excluded)),
        NestedElement n => New(ConstraintModel("NestedElement"), Constraint(n.Inner), Bool(n.IsContainedSubtype)),
        OpaqueElement o => New(ConstraintModel("OpaqueElement"), Str(o.Kind), Str(o.Notation)),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    /// <summary>A collection expression of <c>Asn1TagStep</c>s.</summary>
    public static ExpressionSyntax Tags(IEnumerable<(Asn1Tag Tag, bool IsExplicit)> tags) => Collection(tags.Select(t =>
        New(Runtime("Asn1TagStep"), New(Core("Asn1Tag"), Member(Core("TagClass"), t.Tag.Class.ToString()), Int32(t.Tag.Number)), Bool(t.IsExplicit))));
}
