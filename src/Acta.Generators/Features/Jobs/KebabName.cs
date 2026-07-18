using System;

namespace Acta.Generators.Features.Jobs;

/// <summary>
/// The shared kebab grammar for persisted names: job names, schedule names, and payload format
/// names. System-internal names may use the reserved <c>sys.</c> dotted namespace when the
/// caller explicitly allows it.
/// </summary>
internal static class KebabName
{
    /// <summary>
    private const string SystemPrefix = "sys.";

    /// True for `[a-z][a-z0-9]*(-[a-z0-9]+)*` within the length cap. The `sys.` prefix is honored
    /// only when the caller allows it (framework assembly).
    /// </summary>
    public static bool IsValid(string name, int maxLength, bool allowSystemPrefix)
    {
        if (string.IsNullOrEmpty(name) || name.Length > maxLength)
        {
            return false;
        }

        var start = 0;
        if (name.StartsWith(SystemPrefix, StringComparison.Ordinal))
        {
            if (!allowSystemPrefix)
            {
                return false;
            }
            start = SystemPrefix.Length;
        }

        return SegmentIsKebab(name, start, name.Length);
    }

    private static bool SegmentIsKebab(string text, int start, int end)
    {
        if (start >= end || text[start] < 'a' || text[start] > 'z')
        {
            return false;
        }

        var previousDash = false;
        for (var i = start + 1; i < end; i++)
        {
            var c = text[i];
            if (c == '-')
            {
                if (previousDash)
                {
                    return false;
                }
                previousDash = true;
                continue;
            }
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
            previousDash = false;
        }
        return !previousDash;
    }
}
