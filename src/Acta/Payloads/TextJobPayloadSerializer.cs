using System.Globalization;
using System.Text;

namespace Acta;

/// <summary>
/// Built-in <see cref="IJobPayloadSerializer"/> for <see cref="JobPayloadFormat.Text"/>: renders scalar values as
/// UTF-8 text with <see cref="CultureInfo.InvariantCulture"/> on every format and parse, so culture cannot drift
/// between producer and consumer. Strings pass through verbatim, and text holding U+0000 throws; <c>Guid</c> uses
/// <c>"D"</c>; <c>DateTime</c>/<c>DateTimeOffset</c> round-trip <c>"O"</c>; <c>TimeSpan</c> <c>"c"</c>; <c>DateOnly</c>
/// <c>"yyyy-MM-dd"</c>; <c>TimeOnly</c> <c>"HH:mm:ss.fffffff"</c>; enums round-trip by name, parsed case-insensitively;
/// other numerics, <c>bool</c>, and <c>char</c> use invariant <c>IConvertible</c>. Any other type is a programming error.
/// </summary>
public sealed class TextJobPayloadSerializer : IJobPayloadSerializer
{
    /// <summary>
    /// Shared stateless instance reused by <see cref="JobPayload.Text"/> so callers don't allocate
    /// a fresh serializer per call.
    /// </summary>
    public static TextJobPayloadSerializer Default { get; } = new();

    public JobPayloadFormat Format => JobPayloadFormat.Text;

    public JobPayload Serialize<T>(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), $"TextJobPayloadSerializer cannot serialize a null {typeof(T).Name}.");
        }

        var text = FormatValue(value);
        return JobPayload.FromBytes(Format, Encoding.UTF8.GetBytes(text));
    }

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"TextJobPayloadSerializer cannot deserialize payload format '{payload.Format}'.");
        }

        var text = Encoding.UTF8.GetString(payload.Data.Span);
        return (T)ParseValue(text, typeof(T));
    }

    private static string FormatValue<T>(T value) =>
        value switch
        {
            string s => s,
            Guid g => g.ToString("D", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            IConvertible c => c.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"TextJobPayloadSerializer cannot serialize {typeof(T).FullName}. "
                    + "Expected string, Guid, DateTime, DateTimeOffset, TimeSpan, DateOnly, TimeOnly, "
                    + "enum, or a primitive scalar."
            ),
        };

    private static object ParseValue(string text, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return text;
        }
        if (targetType == typeof(Guid))
        {
            return Guid.ParseExact(text, "D");
        }
        if (targetType == typeof(DateTime))
        {
            return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        if (targetType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        if (targetType == typeof(TimeSpan))
        {
            return TimeSpan.ParseExact(text, "c", CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(DateOnly))
        {
            return DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(TimeOnly))
        {
            return TimeOnly.ParseExact(text, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }
        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, text, ignoreCase: true);
        }

        // Primitive scalars (bool, char, int8/16/32/64, uint8/16/32/64, float/double/decimal) all
        // implement IConvertible; Convert.ChangeType uses invariant culture for parsing.
        return Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException($"TextJobPayloadSerializer cannot parse '{text}' as {targetType.FullName}.");
    }
}
