using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Explicit query-string binding with predictable failures: a malformed value produces a caller
/// error message instead of a silently ignored filter.
/// </summary>
internal static class QueryBinding
{
    public static bool TryEnum<TEnum>(IQueryCollection query, string name, out TEnum? value, ref string? error)
        where TEnum : struct, Enum
    {
        value = null;
        var raw = query[name];
        if (raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return true;
        }

        // Kebab wire names ("retry-after") parse too: stripping the dashes leaves the member name.
        var candidate = raw[0]!.Contains('-') ? raw[0]!.Replace("-", "") : raw[0]!;
        if (Enum.TryParse<TEnum>(candidate, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Query parameter '{name}' is not a valid {typeof(TEnum).Name} value.";
        return false;
    }

    public static bool TryInt(IQueryCollection query, string name, out int? value, ref string? error)
    {
        value = null;
        var raw = query[name];
        if (raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return true;
        }

        if (int.TryParse(raw[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Query parameter '{name}' is not a valid integer.";
        return false;
    }

    /// <summary>
    /// Binds a <see cref="EventCode"/> from its exact dotted-kebab wire name: the <c>[Code]</c>
    /// string the API emits and accepts (e.g. "definition.overrides-updated"), rather than the .NET
    /// member name that the generic <see cref="TryEnum{TEnum}"/> would try to parse. An unknown code is
    /// a caller error mapped to 400.
    /// </summary>
    public static bool TryEventCode(IQueryCollection query, string name, out EventCode? value, ref string? error)
    {
        value = null;
        var raw = query[name];
        if (raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return true;
        }

        try
        {
            value = EventCode.FromCode(raw[0]!);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = $"Query parameter '{name}' is not a valid EventCode value.";
            return false;
        }
    }

    /// <summary>
    /// Binds a <c>[Code]</c>-family enum from its exact dotted-kebab wire name via the family's generated
    /// <c>FromCode</c> parser (e.g. <c>ActorCodeExtensions.FromCode</c>), rather than the .NET member
    /// name. An unknown code is a caller error mapped to 400.
    /// </summary>
    public static bool TryCode<TEnum>(
        IQueryCollection query,
        string name,
        Func<string, TEnum> fromCode,
        out TEnum? value,
        ref string? error
    )
        where TEnum : struct, Enum
    {
        value = null;
        var raw = query[name];
        if (raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return true;
        }

        try
        {
            value = fromCode(raw[0]!);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = $"Query parameter '{name}' is not a valid {typeof(TEnum).Name} value.";
            return false;
        }
    }

    public static bool TryDateTime(IQueryCollection query, string name, out DateTime? value, ref string? error)
    {
        value = null;
        var raw = query[name];
        if (raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return true;
        }

        if (
            DateTime.TryParse(
                raw[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed
            )
        )
        {
            value = parsed;
            return true;
        }

        error = $"Query parameter '{name}' is not a valid ISO-8601 timestamp.";
        return false;
    }

    public static bool TryLong(IQueryCollection query, string name, out long? value, ref string? error)
    {
        value = null;
        var raw = query[name];
        if (raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return true;
        }

        if (long.TryParse(raw[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Query parameter '{name}' is not a valid integer.";
        return false;
    }

    public static bool TryBool(IQueryCollection query, string name, out bool? value, ref string? error)
    {
        value = null;
        var raw = query[name];
        if (raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return true;
        }

        if (bool.TryParse(raw[0], out var parsed))
        {
            value = parsed;
            return true;
        }

        error = $"Query parameter '{name}' is not a valid boolean.";
        return false;
    }

    public static string? Text(IQueryCollection query, string name)
    {
        var raw = query[name];
        return raw.Count == 0 || string.IsNullOrEmpty(raw[0]) ? null : raw[0];
    }

    /// <summary>
    /// Repeated <c>tag</c> filters: each value is <c>name</c> or <c>name:value</c> (split on the
    /// first colon; values may contain colons). Name syntax is validated downstream by the query
    /// service, which maps invalid names to a 400 through the endpoint's
    /// <see cref="InvalidQueryException"/> guard.
    /// </summary>
    public static IReadOnlyList<Acta.TagFilter>? Tags(IQueryCollection query, string name = "tag")
    {
        var raw = query[name];
        if (raw.Count == 0)
        {
            return null;
        }

        var filters = new List<Acta.TagFilter>(raw.Count);
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            // `name` alone, or `name:value`. A trailing colon ("env:") carries no value and must match
            // the tag by name (any value), so an empty value part collapses back to a name-only filter.
            var separator = item.IndexOf(':');
            var value = separator < 0 ? null : item[(separator + 1)..];
            var filterName = separator < 0 ? item : item[..separator];
            filters.Add(string.IsNullOrEmpty(value) ? new Acta.TagFilter(filterName) : new Acta.TagFilter(filterName, value));
        }

        return filters.Count == 0 ? null : filters;
    }
}
