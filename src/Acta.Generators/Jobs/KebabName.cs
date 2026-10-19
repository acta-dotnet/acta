using System;

namespace Acta.Generators;

/// <summary>
/// The name grammar the runtime's <c>NameSyntax</c> applies, for the persisted names a <c>[Job]</c> declares: job,
/// schedule, rate meter, alert channel, payload format, and lane names. One or more <c>[a-z][a-z0-9-]*</c> segments
/// without a trailing <c>-</c>, joined by <c>.</c>, lowercase as written. The bare <c>sys</c> and the <c>sys.</c>
/// prefix belong to Acta and pass only when the caller allows them (the framework assembly).
/// </summary>
internal static class KebabName
{
    private const string SystemPrefix = "sys.";

    /// <summary>True for a dotted kebab name within the length cap, outside the reserved names unless allowed.</summary>
    public static bool IsValid(string name, int maxLength, bool allowSystemPrefix = false)
    {
        if (string.IsNullOrEmpty(name) || name.Length > maxLength)
        {
            return false;
        }
        if (!allowSystemPrefix && (name == "sys" || name.StartsWith(SystemPrefix, StringComparison.Ordinal)))
        {
            return false;
        }

        var start = 0;
        for (var dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', start))
        {
            if (!SegmentIsKebab(name, start, dot))
            {
                return false;
            }
            start = dot + 1;
        }
        return SegmentIsKebab(name, start, name.Length);
    }

    private static bool SegmentIsKebab(string text, int start, int end)
    {
        if (start >= end || text[start] < 'a' || text[start] > 'z' || text[end - 1] == '-')
        {
            return false;
        }

        for (var i = start + 1; i < end; i++)
        {
            var c = text[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
            {
                return false;
            }
        }
        return true;
    }
}
