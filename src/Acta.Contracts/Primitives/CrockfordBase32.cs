using System.Buffers.Binary;

namespace Acta;

/// <summary>
/// Lowercase Crockford Base32 codec for 16-byte values: 26 characters, two leading zero pad
/// bits, MSB-first. Decoding is case-insensitive and maps the Crockford o/i/l aliases to 0/1/1.
/// </summary>
internal static class CrockfordBase32
{
    public const int EncodedLength = 26;

    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public static string EncodeLower(ReadOnlySpan<byte> bytes)
    {
        var value = new UInt128(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
        Span<char> chars = stackalloc char[EncodedLength];
        for (var i = EncodedLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }

    public static bool TryDecode(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        // 26 characters carry 130 bits; the first character holds the two pad bits, so it must
        // decode below 8 for the value to fit 128 bits.
        if (chars.Length != EncodedLength || DigitOf(chars[0]) is < 0 or > 7)
        {
            return false;
        }

        UInt128 value = 0;
        foreach (var c in chars)
        {
            var digit = DigitOf(c);
            if (digit < 0)
            {
                return false;
            }

            value = (value << 5) | (uint)digit;
        }

        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)(value >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], (ulong)value);
        return true;
    }

    private static int DigitOf(char c)
    {
        var lower = char.ToLowerInvariant(c);
        return lower switch
        {
            'o' => 0,
            'i' or 'l' => 1,
            _ => Alphabet.IndexOf(lower),
        };
    }
}
