using System.Globalization;
using System.Numerics;
using System.Text;
using Asn1.Diagnostics;

namespace Asn1.CodeGen.Pawn;

/// <summary>Writes Pawn source with four-space indentation.</summary>
internal sealed class PawnWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Line(string text = "")
    {
        if (text.Length > 0)
        {
            _builder.Append(' ', _indent * 4).Append(text);
        }

        _builder.Append('\n');
    }

    public void Open(string text)
    {
        Line(text + " {");
        _indent++;
    }

    public void Block()
    {
        Line("{");
        _indent++;
    }

    public void Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
    }

    public override string ToString() => _builder.ToString();
}

/// <summary>Emits the cell offsets of a value layout as Pawn constants, named by the type prefix and a path.</summary>
internal sealed class PawnConstantsEmitter(PawnWriter writer, Func<string, SourceLocation, string> allocate)
{
    public void Emit(PawnTypeEntry entry)
    {
        writer.Line($"const {entry.CellsName} = {PawnGenerator.Literal(entry.Layout!.Cells)};");
        foreach (var (suffix, value) in Collect(entry.Layout!))
        {
            writer.Line($"const {allocate(entry.Prefix + suffix, entry.Assignment.Location)} = {PawnGenerator.Literal(value)};");
        }
    }

    /// <summary>The constants of a layout: name suffixes after the type prefix, and their values.</summary>
    public static List<(string Suffix, long Value)> Collect(PawnLayout layout)
    {
        var constants = new List<(string Suffix, long Value)>();
        Value(constants, layout, string.Empty, 0, topLevel: true);
        return constants;
    }

    private static void Value(List<(string Suffix, long Value)> constants, PawnLayout layout, string path, int offset, bool topLevel = false)
    {
        switch (layout)
        {
            case BooleanLayout or IntegerLayout or ReferenceLayout:
                if (!topLevel)
                {
                    constants.Add((path, offset));
                }

                break;
            case EnumeratedLayout enumerated:
                if (!topLevel)
                {
                    constants.Add((path, offset));
                }

                constants.AddRange(enumerated.Items.Select(item => (path + "_" + item.Name, item.Value)));
                break;
            case NullLayout:
                break;
            case StringLayout text:
                if (!text.IsFixed)
                {
                    constants.Add((path + "_LENGTH", offset));
                }

                constants.Add((path, offset + text.DataOffset));
                constants.Add((path + "_MAX", text.Maximum));
                break;
            case SequenceLayout sequence:
                if (!topLevel)
                {
                    constants.Add((path, offset));
                }

                foreach (var field in sequence.Fields)
                {
                    var fieldPath = path + "_" + field.Name;
                    if (field.HasPresenceCell)
                    {
                        constants.Add((fieldPath + "_PRESENT", offset + field.PresenceOffset));
                    }

                    Value(constants, field.Layout, fieldPath, offset + field.ValueOffset);
                }

                break;
            case ChoiceLayout choice:
                constants.Add((path + "_CHOICE", offset));
                foreach (var alternative in choice.Alternatives)
                {
                    var alternativePath = path + "_" + alternative.Name;
                    constants.Add((alternativePath + "_CHOSEN", alternative.Index));
                    Value(constants, alternative.Layout, alternativePath, offset + 1);
                }

                break;
            case SequenceOfLayout list:
                constants.Add((path + "_COUNT", offset));
                constants.Add((path, offset + 1));
                constants.Add((path + "_MAX", list.Maximum));
                constants.Add((path + "_STRIDE", list.Element.Cells));
                if (list.Element is not (BooleanLayout or IntegerLayout or ReferenceLayout or NullLayout))
                {
                    Value(constants, list.Element, path + "_ITEM", 0);
                }

                break;
        }
    }
}

/// <summary>Emits the COER encoder and decoder of a value layout.</summary>
internal sealed class PawnCodeEmitter(PawnWriter writer)
{
    private int _counter;

    private string Local(string stem) => stem + (_counter++).ToString(CultureInfo.InvariantCulture);

    private static string At(string frame, int offset) => offset == 0 ? frame : $"{frame} + {offset.ToString(CultureInfo.InvariantCulture)}";

    private static string Bytes(string frame, int offset) => $"({At(frame, offset)}) * 4";

    private void Fail(string condition) => writer.Line($"if ({condition}) {{ return false; }}");

    private void Require(string call) => writer.Line($"if (!{call}) {{ return false; }}");

    // ------------------------------------------------------------------ encoding

    public void EmitEncode(PawnTypeEntry entry)
    {
        writer.Line($"/* Encodes the {entry.Assignment.Name} at value[base] into buffer from position on, false if it does not fit or is invalid. */");
        writer.Open($"stock bool:{entry.EncodeName}(const value[], buffer{{}}, &position, capacity, base = 0)");
        Encode(entry.Layout!, "base", 0);
        writer.Line("return true;");
        writer.Close();
    }

    private void Encode(PawnLayout layout, string frame, int offset)
    {
        var cell = $"value[{At(frame, offset)}]";
        switch (layout)
        {
            case ReferenceLayout reference:
                Require($"{reference.Target.EncodeName}(value, buffer, position, capacity, {At(frame, offset)})");
                break;
            case BooleanLayout:
                Require($"oer_put_bool(buffer, position, capacity, {cell})");
                break;
            case IntegerLayout { Form: IntegerForm.UnsignedFixed or IntegerForm.SignedFixed } integer:
                if (RangeCheck(integer, cell) is null)
                {
                    Require($"oer_put_uint(buffer, position, capacity, {cell}, {integer.Octets})");
                }
                else
                {
                    var (helper, lower, upper) = FixedRange(integer, "put");
                    Require($"{helper}(buffer, position, capacity, {cell}, {integer.Octets}, {lower}, {upper})");
                }

                break;
            case IntegerLayout integer:
                if (RangeCheck(integer, cell) is { } valid)
                {
                    Fail($"!({valid})");
                }

                Require(integer.Form == IntegerForm.UnsignedVariable
                    ? $"oer_put_varuint(buffer, position, capacity, {cell})"
                    : $"oer_put_varint(buffer, position, capacity, {cell})");
                break;
            case EnumeratedLayout enumerated:
                EnumeratedCheck(enumerated, cell);
                Require($"oer_put_enumerated(buffer, position, capacity, {cell})");
                break;
            case NullLayout:
                break;
            case StringLayout text:
                EncodeString(text, frame, offset);
                break;
            case SequenceLayout sequence:
                EncodeSequence(sequence, frame, offset);
                break;
            case ChoiceLayout choice:
                writer.Open($"switch ({cell})");
                foreach (var alternative in choice.Alternatives)
                {
                    writer.Open($"case {alternative.Index}:");
                    Require($"oer_put_uint(buffer, position, capacity, 0x{alternative.TagOctet:X2}, 1)");
                    Encode(alternative.Layout, frame, offset + 1);
                    writer.Close();
                }

                writer.Line("default: { return false; }");
                writer.Close();
                break;
            case SequenceOfLayout list:
                {
                    var count = Local("count");
                    var index = Local("i");
                    var item = Local("item");
                    writer.Block();
                    writer.Line($"new {count} = {cell};");
                    Fail($"({count} < {list.Minimum}) || ({count} > {list.Maximum})");
                    Require($"oer_put_quantity(buffer, position, capacity, {count})");
                    writer.Open($"for (new {index} = 0; {index} < {count}; {index}++)");
                    writer.Line($"new {item} = {At(frame, offset + 1)} + ({index} * {list.Element.Cells});");
                    Encode(list.Element, item, 0);
                    writer.Close();
                    writer.Close();
                    break;
                }
        }
    }

    private void EncodeString(StringLayout text, string frame, int offset)
    {
        if (text.IsFixed)
        {
            Require($"oer_put_layout_bytes(buffer, position, capacity, value, {Bytes(frame, offset)}, {text.MaximumOctets})");
            return;
        }

        var length = $"value[{At(frame, offset)}]";
        writer.Block();
        if (text.Kind == StringKind.Bits)
        {
            var bits = Local("bits");
            var octets = Local("octets");
            writer.Line($"new {bits} = {length};");
            Fail($"({bits} < {text.Minimum}) || ({bits} > {text.Maximum})");
            writer.Line($"new {octets} = ({bits} + 7) >>> 3;");
            Require($"oer_put_length(buffer, position, capacity, {octets} + 1)");
            Require($"oer_put_uint(buffer, position, capacity, ({octets} << 3) - {bits}, 1)");
            Require($"oer_put_layout_bytes(buffer, position, capacity, value, {Bytes(frame, offset + 1)}, {octets})");
        }
        else
        {
            Require($"oer_put_counted_bytes(buffer, position, capacity, value, {Bytes(frame, offset + 1)}, {length}, {text.Minimum}, {text.Maximum})");
        }

        writer.Close();
    }

    private void EncodeSequence(SequenceLayout sequence, string frame, int offset)
    {
        if (sequence.PreambleBits > 0)
        {
            var preamble = Local("preamble");
            writer.Block();
            writer.Line($"new {preamble} = 0;");
            var bit = sequence.IsExtensible ? 1 : 0;
            foreach (var field in sequence.Fields.Where(f => f.HasPresenceCell))
            {
                writer.Line($"if ({Sent(field, frame, offset)}) {{ {preamble} |= {Mask(sequence, bit++)}; }}");
            }

            Require($"oer_put_uint(buffer, position, capacity, {preamble}, {sequence.PreambleOctets})");
            writer.Close();
        }

        foreach (var field in sequence.Fields)
        {
            if (field.HasPresenceCell)
            {
                writer.Open($"if ({Sent(field, frame, offset)})");
                Encode(field.Layout, frame, offset + field.ValueOffset);
                writer.Close();
            }
            else
            {
                Encode(field.Layout, frame, offset + field.ValueOffset);
            }
        }
    }

    /// <summary>COER leaves out a DEFAULT component whose value is the default.</summary>
    private static string Sent(SequenceField field, string frame, int offset)
    {
        var present = $"(value[{At(frame, offset + field.PresenceOffset)}] != 0)";
        return field is { Presence: FieldPresence.Default, DefaultValue: { } fallback }
            ? $"{present} && (value[{At(frame, offset + field.ValueOffset)}] != {PawnGenerator.Literal(fallback)})"
            : present;
    }

    private static string Mask(SequenceLayout sequence, int bit) =>
        PawnGenerator.Literal(1L << ((sequence.PreambleOctets * 8) - 1 - bit));

    // ------------------------------------------------------------------ decoding

    public void EmitDecode(PawnTypeEntry entry)
    {
        writer.Line($"/* Decodes a {entry.Assignment.Name} from buffer at position into value[base], false if the input is not valid COER. */");
        writer.Open($"stock bool:{entry.DecodeName}(value[], const buffer{{}}, &position, length, base = 0)");
        Decode(entry.Layout!, "base", 0);
        writer.Line("return true;");
        writer.Close();
    }

    private void Decode(PawnLayout layout, string frame, int offset)
    {
        var cell = $"value[{At(frame, offset)}]";
        switch (layout)
        {
            case ReferenceLayout reference:
                Require($"{reference.Target.DecodeName}(value, buffer, position, length, {At(frame, offset)})");
                break;
            case BooleanLayout:
                Require($"oer_get_bool(buffer, position, length, {cell})");
                break;
            case IntegerLayout { Form: IntegerForm.UnsignedFixed or IntegerForm.SignedFixed } integer:
                if (RangeCheck(integer, cell) is null)
                {
                    Require(integer.Form == IntegerForm.UnsignedFixed
                        ? $"oer_get_uint(buffer, position, length, {cell}, {integer.Octets})"
                        : $"oer_get_int(buffer, position, length, {cell}, {integer.Octets})");
                }
                else
                {
                    var (helper, lower, upper) = FixedRange(integer, "get");
                    Require($"{helper}(buffer, position, length, {cell}, {integer.Octets}, {lower}, {upper})");
                }

                break;
            case IntegerLayout integer:
                Require(integer.Form == IntegerForm.UnsignedVariable
                    ? $"oer_get_varuint(buffer, position, length, {cell})"
                    : $"oer_get_varint(buffer, position, length, {cell})");
                if (RangeCheck(integer, cell) is { } valid)
                {
                    Fail($"!({valid})");
                }

                break;
            case EnumeratedLayout enumerated:
                Require($"oer_get_enumerated(buffer, position, length, {cell})");
                EnumeratedCheck(enumerated, cell);
                break;
            case NullLayout:
                break;
            case StringLayout text:
                DecodeString(text, frame, offset);
                break;
            case SequenceLayout sequence:
                DecodeSequence(sequence, frame, offset);
                break;
            case ChoiceLayout choice:
                {
                    var tag = Local("tag");
                    writer.Block();
                    writer.Line($"new {tag};");
                    Require($"oer_get_uint(buffer, position, length, {tag}, 1)");
                    writer.Open($"switch ({tag})");
                    foreach (var alternative in choice.Alternatives)
                    {
                        writer.Open($"case 0x{alternative.TagOctet:X2}:");
                        writer.Line($"{cell} = {alternative.Index};");
                        Decode(alternative.Layout, frame, offset + 1);
                        writer.Close();
                    }

                    writer.Line("default: { return false; }");
                    writer.Close();
                    writer.Close();
                    break;
                }

            case SequenceOfLayout list:
                {
                    var count = Local("count");
                    var index = Local("i");
                    var item = Local("item");
                    writer.Block();
                    writer.Line($"new {count};");
                    Require($"oer_get_quantity(buffer, position, length, {count})");
                    Fail($"({count} < {list.Minimum}) || ({count} > {list.Maximum})");
                    writer.Line($"{cell} = {count};");
                    writer.Open($"for (new {index} = 0; {index} < {count}; {index}++)");
                    writer.Line($"new {item} = {At(frame, offset + 1)} + ({index} * {list.Element.Cells});");
                    Decode(list.Element, item, 0);
                    writer.Close();
                    writer.Close();
                    break;
                }
        }
    }

    private void DecodeString(StringLayout text, string frame, int offset)
    {
        if (text.IsFixed)
        {
            Require($"oer_get_layout_bytes(buffer, position, length, value, {Bytes(frame, offset)}, {text.MaximumOctets})");
            var unusedBits = (text.MaximumOctets * 8) - text.Maximum;
            if (text.Kind == StringKind.Bits && unusedBits > 0)
            {
                var last = $"oer_layout_byte(value, {Bytes(frame, offset)} + {text.MaximumOctets - 1})";
                Fail($"({last} & 0x{(1 << (int)unusedBits) - 1:X2}) != 0");
            }

            return;
        }

        var lengthCell = $"value[{At(frame, offset)}]";
        writer.Block();
        if (text.Kind == StringKind.Bits)
        {
            var octets = Local("octets");
            var unused = Local("unused");
            var bits = Local("bits");
            writer.Line($"new {octets};");
            writer.Line($"new {unused};");
            Require($"oer_get_length(buffer, position, length, {octets})");
            Fail($"{octets} < 1");
            Require($"oer_get_uint(buffer, position, length, {unused}, 1)");
            Fail($"({unused} > 7) || (({octets} == 1) && ({unused} != 0))");
            writer.Line($"new {bits} = (({octets} - 1) << 3) - {unused};");
            Fail($"({bits} < {text.Minimum}) || ({bits} > {text.Maximum})");
            writer.Line($"{lengthCell} = {bits};");
            Require($"oer_get_layout_bytes(buffer, position, length, value, {Bytes(frame, offset + 1)}, {octets} - 1)");
        }
        else
        {
            Require($"oer_get_counted_bytes(buffer, position, length, value, {At(frame, offset)}, {Bytes(frame, offset + 1)}, {text.Minimum}, {text.Maximum})");
        }

        writer.Close();
    }

    private void DecodeSequence(SequenceLayout sequence, string frame, int offset)
    {
        string? preamble = null;
        if (sequence.PreambleBits > 0)
        {
            preamble = Local("preamble");
            writer.Line($"new {preamble};");
            Require($"oer_get_uint(buffer, position, length, {preamble}, {sequence.PreambleOctets})");
            var unusedBits = (sequence.PreambleOctets * 8) - sequence.PreambleBits;
            if (unusedBits > 0)
            {
                Fail($"({preamble} & {PawnGenerator.Literal((1L << unusedBits) - 1)}) != 0");
            }

            var bit = sequence.IsExtensible ? 1 : 0;
            foreach (var field in sequence.Fields.Where(f => f.HasPresenceCell))
            {
                writer.Line($"value[{At(frame, offset + field.PresenceOffset)}] = (({preamble} & {Mask(sequence, bit++)}) != 0) ? 1 : 0;");
            }
        }

        foreach (var field in sequence.Fields)
        {
            if (!field.HasPresenceCell)
            {
                Decode(field.Layout, frame, offset + field.ValueOffset);
                continue;
            }

            writer.Open($"if (value[{At(frame, offset + field.PresenceOffset)}] != 0)");
            Decode(field.Layout, frame, offset + field.ValueOffset);
            if (field is { Presence: FieldPresence.Default, DefaultValue: { } canonicalFallback })
            {
                Fail($"value[{At(frame, offset + field.ValueOffset)}] == {PawnGenerator.Literal(canonicalFallback)}");
            }

            if (field is { Presence: FieldPresence.Default, DefaultValue: { } fallback })
            {
                writer.Close(" else {");
                writer.Line($"    value[{At(frame, offset + field.ValueOffset)}] = {PawnGenerator.Literal(fallback)};");
                writer.Line("}");
            }
            else
            {
                writer.Close();
            }
        }

        if (sequence.IsExtensible && preamble is not null)
        {
            writer.Open($"if (({preamble} & {Mask(sequence, 0)}) != 0)");
            Require("oer_skip_extensions(buffer, position, length)");
            writer.Close();
        }
    }

    // ------------------------------------------------------------------ checks

    private void EnumeratedCheck(EnumeratedLayout enumerated, string cell)
    {
        if (enumerated.IsExtensible)
        {
            return;
        }

        var values = string.Join(", ", enumerated.Items.Select(i => PawnGenerator.Literal(i.Value)));
        writer.Open($"switch ({cell})");
        writer.Line($"case {values}: {{}}");
        writer.Line("default: { return false; }");
        writer.Close();
    }

    /// <summary>The fused core helper of a range-checked fixed-size INTEGER and its bounds.</summary>
    private static (string Helper, string Lower, string Upper) FixedRange(IntegerLayout integer, string direction) =>
        integer.IsSigned
            ? ($"oer_{direction}_signed_in", PawnGenerator.Literal(integer.Lower ?? int.MinValue), PawnGenerator.Literal(integer.Upper ?? int.MaxValue))
            : ($"oer_{direction}_unsigned_in", PawnGenerator.Literal(integer.Lower ?? BigInteger.Zero), PawnGenerator.Literal(integer.Upper ?? uint.MaxValue));

    /// <summary>The range test of an INTEGER cell, null when every cell value is in range.</summary>
    private static string? RangeCheck(IntegerLayout integer, string cell)
    {
        var conditions = new List<string>();
        if (integer.IsSigned)
        {
            if (integer.Lower is { } lower && lower > int.MinValue)
            {
                conditions.Add($"({cell} >= {PawnGenerator.Literal(lower)})");
            }

            if (integer.Upper is { } upper && upper < int.MaxValue)
            {
                conditions.Add($"({cell} <= {PawnGenerator.Literal(upper)})");
            }
        }
        else
        {
            var lower = integer.Lower ?? BigInteger.Zero;
            if (integer.Upper is { } upper && upper <= int.MaxValue)
            {
                conditions.Add($"({cell} >= {PawnGenerator.Literal(lower)})");
                conditions.Add($"({cell} <= {PawnGenerator.Literal(upper)})");
            }
            else
            {
                if (lower > 0)
                {
                    conditions.Add($"oer_unsigned_less_or_equal({PawnGenerator.Literal(lower)}, {cell})");
                }

                if (integer.Upper is { } wide && wide < uint.MaxValue)
                {
                    conditions.Add($"oer_unsigned_less_or_equal({cell}, {PawnGenerator.Literal(wide)})");
                }
            }
        }

        return conditions.Count == 0 ? null : string.Join(" && ", conditions);
    }
}
