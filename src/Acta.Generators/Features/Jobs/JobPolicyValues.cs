using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;

namespace Acta.Generators;

/// <summary>Parses and checks the single policy values a <c>[Job]</c> attribute names: lanes, rates, backoff, durations, codes.</summary>
internal static class JobPolicyValues
{
    /// <summary>
    /// The key rule the runtime enforces in <c>IdentifierSyntax.NormalizeKey</c>: trimmed, 1 to 128 key
    /// characters, lowercased, and not under the reserved <c>sys.</c> prefix. The descriptor carries the
    /// canonical form, the same value registration would store.
    /// </summary>
    internal static bool TryCanonicalizeLane(string lane, out string canonical)
    {
        var trimmed = lane.Trim();
        canonical = trimmed.ToLowerInvariant();
        if (trimmed.Length is 0 or > 128 || canonical.StartsWith("sys.", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            var keyChar =
                c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_' or ':' or '/' or '@' or '+' or '=';
            if (!keyChar)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the raw `RateLimit` string against the same format `RateLimitSpec` parses at runtime,
    /// so a typo is a build error rather than a definition that quietly meters nothing. The rate is
    /// carried forward as written; registration stores its canonical form.
    /// </summary>
    internal static string? ReadRateLimit(KeyValuePair<string, TypedConstant> named, List<DiagnosticRecord> diagnostics, Location location)
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var slash = text.IndexOf('/');
        var periodMilliseconds = slash > 0 && slash == text.Length - 2 ? PeriodMilliseconds(text[text.Length - 1]) : 0;
        if (
            periodMilliseconds != 0
            && int.TryParse(text.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count >= 1
            && count <= periodMilliseconds
            && text[0] != '0'
        )
        {
            return text;
        }

        diagnostics.Add(
            ManifestDiagnostics.InvalidPolicyValue(
                named.Key,
                $"\"{text}\"",
                "A rate limit is a positive count with no leading zero, a slash, and a period of s, m, or h (\"10/s\"), at most 1000 per second.",
                location
            )
        );
        return null;
    }

    internal static int PeriodMilliseconds(char period) =>
        period switch
        {
            's' => 1_000,
            'm' => 60_000,
            'h' => 3_600_000,
            _ => 0,
        };

    /// <summary>
    /// Validates the raw `Backoff` DSL string (format + the 64-char storage ceiling) but carries the
    /// RAW text forward - the definitions column stores the expression itself, never the parsed knobs.
    /// </summary>
    internal static string? ReadBackoff(KeyValuePair<string, TypedConstant> named, List<DiagnosticRecord> diagnostics, Location location)
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!BackoffExpressionValidator.TryParseBackoff(text, out _, out var error))
        {
            ReportDurationSyntaxError(named.Key, text, error, diagnostics, location);
            return null;
        }

        if (text.Length > 64)
        {
            diagnostics.Add(
                ManifestDiagnostics.InvalidPolicyValue(named.Key, $"\"{text}\"", "Backoff expressions are at most 64 characters.", location)
            );
            return null;
        }

        return text;
    }

    /// <summary>
    /// [Job] durations accept both the human syntax (e.g. "1m") and its ISO-8601 time-only
    /// equivalent (e.g. "PT1M"). The descriptor and DB carry whole seconds.
    /// </summary>
    internal static int? ReadDurationSeconds(
        KeyValuePair<string, TypedConstant> named,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (
            BackoffExpressionValidator.TryParseDuration(text, out var span, out var error)
            && BackoffExpressionValidator.TryToWholeSeconds(span, out var seconds)
        )
        {
            return seconds;
        }

        ReportDurationSyntaxError(named.Key, text, error, diagnostics, location);
        return null;
    }

    internal static void ReportDurationSyntaxError(
        string argument,
        string value,
        BackoffExpressionValidator.Error error,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        diagnostics.Add(
            error.Kind == BackoffExpressionValidator.ErrorKind.InvalidUnit
                ? ManifestDiagnostics.InvalidDurationUnit(error.Unit ?? value, location)
                : ManifestDiagnostics.InvalidDuration(argument, value, location)
        );
    }

    /// <summary>
    /// Enum-typed attribute arguments box as their underlying type: byte for the code-family enums
    /// (JobPriorityCode / DefinitionAuditLevelCode / AlertProfileCode), so an `is int` pattern silently
    /// misses them. Read whatever integral the constant carries and narrow to byte.
    /// </summary>
    internal static bool TryGetEnumByte(TypedConstant constant, out byte value)
    {
        value = 0;
        if (constant.IsNull || constant.Value is null)
        {
            return false;
        }
        try
        {
            value = Convert.ToByte(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }
    }

    internal static string? PriorityIdToName(byte id) =>
        id switch
        {
            0 => "Bulk",
            50 => "Normal",
            70 => "High",
            85 => "Critical",
            100 => "Realtime",
            _ => null,
        };

    internal static string? AuditLevelIdToName(byte id) =>
        id switch
        {
            0 => "Off",
            10 => "Failures",
            20 => "Audit",
            _ => null,
        };

    internal static string? AlertProfileIdToName(byte id) =>
        id switch
        {
            0 => "None",
            10 => "OnFailure",
            20 => "Info",
            30 => "OnTerminal",
            40 => "SysCritical",
            _ => null,
        };

    internal static string? TenantRequirementIdToName(byte id) =>
        id switch
        {
            0 => "Optional",
            10 => "Required",
            20 => "Forbidden",
            _ => null,
        };
}
