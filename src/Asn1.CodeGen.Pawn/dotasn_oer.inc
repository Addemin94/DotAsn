/* DotAsn OER core for Pawn: the byte-level helpers that generated encoders and decoders call.
 *
 * Encodings are canonical OER (COER, X.696) on 32-bit cells.
 * The encoded message is a packed array, written and read with the {} operator.
 * Octet, bit and character strings live inside the value layout as packed bytes,
 * four to a cell with the first byte in the cell's most significant byte,
 * the same order as Pawn's own packed strings.
 * Every function returns false when the buffer is too small or the input is not canonical.
 */

#if defined _dotasn_oer_included
    #endinput
#endif
#define _dotasn_oer_included

/* Unsigned comparison of two cells, for values above cellmax. */
stock bool:oer_unsigned_less_or_equal(first, second) {
    return (first ^ cellmin) <= (second ^ cellmin);
}

/* The fewest octets that hold an unsigned value, at least one. */
stock oer_unsigned_octets(value) {
    if ((value & 0xFF000000) != 0) {
        return 4;
    }
    if ((value & 0x00FF0000) != 0) {
        return 3;
    }
    if ((value & 0x0000FF00) != 0) {
        return 2;
    }
    return 1;
}

/* The fewest octets that hold a value in two's complement. */
stock oer_signed_octets(value) {
    if ((value >= -128) && (value <= 127)) {
        return 1;
    }
    if ((value >= -32768) && (value <= 32767)) {
        return 2;
    }
    if ((value >= -8388608) && (value <= 8388607)) {
        return 3;
    }
    return 4;
}

/* Byte number byte_offset of a value layout, counted from the layout's first cell. */
stock oer_layout_byte(const value[], byte_offset) {
    return (value[byte_offset >>> 2] >>> (24 - ((byte_offset & 3) << 3))) & 0xFF;
}

stock oer_set_layout_byte(value[], byte_offset, byte) {
    new shift = 24 - ((byte_offset & 3) << 3);
    new cell_index = byte_offset >>> 2;
    value[cell_index] = (value[cell_index] & ~(0xFF << shift)) | ((byte & 0xFF) << shift);
}

/* Writes the low octets of value, most significant first. Signed values give their two's complement. */
stock bool:oer_put_uint(buffer{}, &position, capacity, value, octets) {
    if ((position + octets) > capacity) {
        return false;
    }
    for (new shift = (octets - 1) << 3; shift >= 0; shift -= 8) {
        buffer{position} = (value >>> shift) & 0xFF;
        position++;
    }
    return true;
}

stock bool:oer_get_uint(const buffer{}, &position, length, &value, octets) {
    if ((position + octets) > length) {
        return false;
    }
    value = 0;
    for (new index = 0; index < octets; index++) {
        value = (value << 8) | buffer{position};
        position++;
    }
    return true;
}

/* Reads a two's complement value of the given octets and sign-extends it to a cell. */
stock bool:oer_get_int(const buffer{}, &position, length, &value, octets) {
    if (!oer_get_uint(buffer, position, length, value, octets)) {
        return false;
    }
    if (octets < 4) {
        new unused_bits = 32 - (octets << 3);
        value = (value << unused_bits) >> unused_bits;
    }
    return true;
}

/* A fixed-size INTEGER with lower and upper as unsigned bounds, the OER form of a non-negative range. */
stock bool:oer_put_unsigned_in(buffer{}, &position, capacity, value, octets, lower, upper) {
    return oer_unsigned_less_or_equal(lower, value)
        && oer_unsigned_less_or_equal(value, upper)
        && oer_put_uint(buffer, position, capacity, value, octets);
}

stock bool:oer_get_unsigned_in(const buffer{}, &position, length, &value, octets, lower, upper) {
    return oer_get_uint(buffer, position, length, value, octets)
        && oer_unsigned_less_or_equal(lower, value)
        && oer_unsigned_less_or_equal(value, upper);
}

/* A fixed-size INTEGER with a signed range. */
stock bool:oer_put_signed_in(buffer{}, &position, capacity, value, octets, lower, upper) {
    return (value >= lower)
        && (value <= upper)
        && oer_put_uint(buffer, position, capacity, value, octets);
}

stock bool:oer_get_signed_in(const buffer{}, &position, length, &value, octets, lower, upper) {
    return oer_get_int(buffer, position, length, value, octets)
        && (value >= lower)
        && (value <= upper);
}

/* A string with a length: count bytes of a value layout, from byte_offset on, after their length determinant. */
stock bool:oer_put_counted_bytes(buffer{}, &position, capacity, const value[], byte_offset, count, minimum, maximum) {
    return (count >= minimum)
        && (count <= maximum)
        && oer_put_length(buffer, position, capacity, count)
        && oer_put_layout_bytes(buffer, position, capacity, value, byte_offset, count);
}

/* Reads a length determinant into value[count_index], then that many bytes into the layout from byte_offset on. */
stock bool:oer_get_counted_bytes(const buffer{}, &position, length, value[], count_index, byte_offset, minimum, maximum) {
    new count;
    if (!oer_get_length(buffer, position, length, count)) {
        return false;
    }
    if ((count < minimum) || (count > maximum)) {
        return false;
    }
    value[count_index] = count;
    return oer_get_layout_bytes(buffer, position, length, value, byte_offset, count);
}

stock bool:oer_put_bool(buffer{}, &position, capacity, value) {
    return oer_put_uint(buffer, position, capacity, (value != 0) ? 0xFF : 0x00, 1);
}

/* COER: only 0x00 and 0xFF are valid. */
stock bool:oer_get_bool(const buffer{}, &position, length, &value) {
    new octet;
    if (!oer_get_uint(buffer, position, length, octet, 1)) {
        return false;
    }
    if (octet == 0x00) {
        value = 0;
        return true;
    }
    if (octet == 0xFF) {
        value = 1;
        return true;
    }
    return false;
}

/* X.696 8.6: the length determinant, short form below 128, otherwise the shortest long form. */
stock bool:oer_put_length(buffer{}, &position, capacity, count) {
    if ((count >= 0) && (count < 128)) {
        return oer_put_uint(buffer, position, capacity, count, 1);
    }
    new octets = oer_unsigned_octets(count);
    return oer_put_uint(buffer, position, capacity, 0x80 | octets, 1)
        && oer_put_uint(buffer, position, capacity, count, octets);
}

stock bool:oer_get_length(const buffer{}, &position, length, &count) {
    new first;
    if (!oer_get_uint(buffer, position, length, first, 1)) {
        return false;
    }
    if (first < 128) {
        count = first;
        return true;
    }
    new octets = first & 0x7F;
    if ((octets < 1) || (octets > 4)) {
        return false;
    }
    if (!oer_get_uint(buffer, position, length, count, octets)) {
        return false;
    }
    return (count >= 128) && (oer_unsigned_octets(count) == octets);
}

/* X.696 10.3 and 10.4 without a fixed size: a length octet, then the fewest octets of the value. */
stock bool:oer_put_varuint(buffer{}, &position, capacity, value) {
    new octets = oer_unsigned_octets(value);
    return oer_put_uint(buffer, position, capacity, octets, 1)
        && oer_put_uint(buffer, position, capacity, value, octets);
}

stock bool:oer_get_varuint(const buffer{}, &position, length, &value) {
    new octets;
    if (!oer_get_uint(buffer, position, length, octets, 1)) {
        return false;
    }
    if ((octets < 1) || (octets > 4)) {
        return false;
    }
    if (!oer_get_uint(buffer, position, length, value, octets)) {
        return false;
    }
    return oer_unsigned_octets(value) == octets;
}

stock bool:oer_put_varint(buffer{}, &position, capacity, value) {
    new octets = oer_signed_octets(value);
    return oer_put_uint(buffer, position, capacity, octets, 1)
        && oer_put_uint(buffer, position, capacity, value, octets);
}

stock bool:oer_get_varint(const buffer{}, &position, length, &value) {
    new octets;
    if (!oer_get_uint(buffer, position, length, octets, 1)) {
        return false;
    }
    if ((octets < 1) || (octets > 4)) {
        return false;
    }
    if (!oer_get_int(buffer, position, length, value, octets)) {
        return false;
    }
    return oer_signed_octets(value) == octets;
}

/* X.696 11: one octet for 0 to 127, otherwise 0x80 plus the octet count, then two's complement. */
stock bool:oer_put_enumerated(buffer{}, &position, capacity, value) {
    if ((value >= 0) && (value <= 127)) {
        return oer_put_uint(buffer, position, capacity, value, 1);
    }
    new octets = oer_signed_octets(value);
    return oer_put_uint(buffer, position, capacity, 0x80 | octets, 1)
        && oer_put_uint(buffer, position, capacity, value, octets);
}

stock bool:oer_get_enumerated(const buffer{}, &position, length, &value) {
    new first;
    if (!oer_get_uint(buffer, position, length, first, 1)) {
        return false;
    }
    if (first < 128) {
        value = first;
        return true;
    }
    new octets = first & 0x7F;
    if ((octets < 1) || (octets > 4)) {
        return false;
    }
    if (!oer_get_int(buffer, position, length, value, octets)) {
        return false;
    }
    return ((value < 0) || (value > 127)) && (oer_signed_octets(value) == octets);
}

/* X.696 17.2: the quantity field of SEQUENCE OF, a length octet and the fewest octets of the count. */
stock bool:oer_put_quantity(buffer{}, &position, capacity, count) {
    return oer_put_varuint(buffer, position, capacity, count);
}

stock bool:oer_get_quantity(const buffer{}, &position, length, &count) {
    return oer_get_varuint(buffer, position, length, count);
}

/* Copies count bytes from a value layout, starting at byte_offset, into the message. */
stock bool:oer_put_layout_bytes(buffer{}, &position, capacity, const value[], byte_offset, count) {
    if ((position + count) > capacity) {
        return false;
    }
    for (new index = 0; index < count; index++) {
        buffer{position} = oer_layout_byte(value, byte_offset + index);
        position++;
    }
    return true;
}

stock bool:oer_get_layout_bytes(const buffer{}, &position, length, value[], byte_offset, count) {
    if ((position + count) > length) {
        return false;
    }
    for (new index = 0; index < count; index++) {
        oer_set_layout_byte(value, byte_offset + index, buffer{position});
        position++;
    }
    return true;
}

/* X.696 16.4: skips the extension additions of a SEQUENCE, which this layout does not hold. */
stock bool:oer_skip_extensions(const buffer{}, &position, length) {
    new bitmap_octets;
    if (!oer_get_length(buffer, position, length, bitmap_octets)) {
        return false;
    }
    if ((bitmap_octets < 1) || ((position + bitmap_octets) > length)) {
        return false;
    }
    position++;
    new present_count = 0;
    for (new index = 1; index < bitmap_octets; index++) {
        new octet = buffer{position};
        position++;
        while (octet != 0) {
            present_count += octet & 1;
            octet >>>= 1;
        }
    }
    for (new index = 0; index < present_count; index++) {
        new addition_octets;
        if (!oer_get_length(buffer, position, length, addition_octets)) {
            return false;
        }
        if ((position + addition_octets) > length) {
            return false;
        }
        position += addition_octets;
    }
    return true;
}
