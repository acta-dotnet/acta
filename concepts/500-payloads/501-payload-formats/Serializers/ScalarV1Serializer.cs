using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Acta.Payloads;

namespace Acta.Concepts.PayloadFormats;

/// <summary>
/// Serializer for the <c>scalar-v1</c> wire format. Supports primitive types only; see
/// <see cref="ScalarV1Encoding"/> for the wire spec.
/// </summary>
[JobPayloadFormatDeclaration(128, "scalar-v1")]
public sealed class ScalarV1Serializer : IJobPayloadSerializer
{
    public JobPayloadFormat Format => PayloadFormats.ScalarV1Format;

    public JobPayload Serialize<T>(T value) => JobPayload.FromBytes(Format, ScalarV1Encoding.Serialize(value));

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"Expected payload format {Format}, got {payload.Format}.");
        }
        return ScalarV1Encoding.Deserialize<T>(payload.Data.Span);
    }
}

/// <summary>
/// Wire spec for <c>scalar-v1</c>.
/// int/long: big-endian, 4/8 bytes.
/// bool: 1 byte; 0x00 false, 0x01 true.
/// Guid: 16 bytes, RFC 4122 network byte order.
/// DateTimeOffset: int64 Unix milliseconds, big-endian.
/// DateOnly: int32 days since 1970-01-01, big-endian.
/// string: UTF-8, no BOM.
/// </summary>
internal static class ScalarV1Encoding
{
    public static byte[] Serialize<T>(T value)
    {
        if (value is null)
        {
            throw new NotSupportedException("scalar-v1 does not support null values.");
        }

        if (typeof(T) == typeof(bool))
        {
            return [Unsafe.As<T, bool>(ref value) ? (byte)1 : (byte)0];
        }
        if (typeof(T) == typeof(int))
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, Unsafe.As<T, int>(ref value));
            return bytes;
        }
        if (typeof(T) == typeof(long))
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, Unsafe.As<T, long>(ref value));
            return bytes;
        }
        if (typeof(T) == typeof(Guid))
        {
            var bytes = new byte[16];
            if (!Unsafe.As<T, Guid>(ref value).TryWriteBytes(bytes, bigEndian: true, out _))
            {
                throw new InvalidOperationException("Failed to write Guid in network byte order.");
            }
            return bytes;
        }
        if (typeof(T) == typeof(DateTimeOffset))
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, Unsafe.As<T, DateTimeOffset>(ref value).ToUnixTimeMilliseconds());
            return bytes;
        }
        if (typeof(T) == typeof(DateOnly))
        {
            var bytes = new byte[4];
            var epoch = new DateOnly(1970, 1, 1);
            BinaryPrimitives.WriteInt32BigEndian(bytes, Unsafe.As<T, DateOnly>(ref value).DayNumber - epoch.DayNumber);
            return bytes;
        }
        if (typeof(T) == typeof(string))
        {
            return Encoding.UTF8.GetBytes((string)(object)value);
        }

        throw new NotSupportedException($"scalar-v1 does not support values of type '{typeof(T).FullName}'.");
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (typeof(T) == typeof(bool))
        {
            EnsureLength(typeof(T), bytes.Length, 1);
            bool result = bytes[0] switch
            {
                0 => false,
                1 => true,
                _ => throw new FormatException($"Invalid scalar-v1 bool value 0x{bytes[0]:x2}."),
            };
            return Unsafe.As<bool, T>(ref result);
        }
        if (typeof(T) == typeof(int))
        {
            EnsureLength(typeof(T), bytes.Length, 4);
            int result = BinaryPrimitives.ReadInt32BigEndian(bytes);
            return Unsafe.As<int, T>(ref result);
        }
        if (typeof(T) == typeof(long))
        {
            EnsureLength(typeof(T), bytes.Length, 8);
            long result = BinaryPrimitives.ReadInt64BigEndian(bytes);
            return Unsafe.As<long, T>(ref result);
        }
        if (typeof(T) == typeof(Guid))
        {
            EnsureLength(typeof(T), bytes.Length, 16);
            Guid result = new(bytes[..16], bigEndian: true);
            return Unsafe.As<Guid, T>(ref result);
        }
        if (typeof(T) == typeof(DateTimeOffset))
        {
            EnsureLength(typeof(T), bytes.Length, 8);
            DateTimeOffset result = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(bytes));
            return Unsafe.As<DateTimeOffset, T>(ref result);
        }
        if (typeof(T) == typeof(DateOnly))
        {
            EnsureLength(typeof(T), bytes.Length, 4);
            var epoch = new DateOnly(1970, 1, 1);
            DateOnly result = epoch.AddDays(BinaryPrimitives.ReadInt32BigEndian(bytes));
            return Unsafe.As<DateOnly, T>(ref result);
        }
        if (typeof(T) == typeof(string))
        {
            string result = Encoding.UTF8.GetString(bytes);
            return (T)(object)result;
        }

        throw new NotSupportedException($"scalar-v1 does not support values of type '{typeof(T).FullName}'.");
    }

    private static void EnsureLength(Type type, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new FormatException($"Invalid scalar-v1 {type.Name} length. Expected {expected} bytes, got {actual}.");
        }
    }
}
