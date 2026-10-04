using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>
/// One request's query string, read the <see cref="QueryBinding"/> way: an absent value is null, and the first
/// malformed one is kept, so a list answers it as a 400 before any read runs.
/// </summary>
internal sealed class QueryReader(HttpRequest request)
{
    private string? _error;

    /// <summary>Builds a list's query, or throws the first malformed value as <see cref="InvalidQueryException"/>.</summary>
    public static TQuery Bind<TQuery>(HttpRequest request, Func<QueryReader, TQuery> bind)
    {
        var reader = new QueryReader(request);
        var query = bind(reader);
        return reader._error is { } error ? throw new InvalidQueryException(error) : query;
    }

    public string? Text(string name) => QueryBinding.Text(request.Query, name);

    /// <summary>A value the route itself carries, for a list addressed under its target.</summary>
    public string Route(string name) => RouteKeys.Value(request.HttpContext, name);

    public IReadOnlyList<TagFilter>? Tags() => QueryBinding.Tags(request.Query);

    /// <summary>A job-ref filter: absent is null, and one that does not parse is the 400 every ref-valued parameter gives.</summary>
    public JobLookup? Job(string name, ActaEndpointOptions options)
    {
        if (Text(name) is not { } text)
        {
            return null;
        }

        if (JobTargetBinding.TryParseTarget(text, options, out var job))
        {
            return job;
        }

        _error ??= $"{name} is not a valid job ref.";
        return null;
    }

    public int? Int(string name)
    {
        string? error = null;
        QueryBinding.TryInt(request.Query, name, out var value, ref error);
        _error ??= error;
        return value;
    }

    public bool? Bool(string name)
    {
        string? error = null;
        QueryBinding.TryBool(request.Query, name, out var value, ref error);
        _error ??= error;
        return value;
    }

    public DateTime? DateTime(string name)
    {
        string? error = null;
        QueryBinding.TryDateTime(request.Query, name, out var value, ref error);
        _error ??= error;
        return value;
    }

    public TEnum? Enum<TEnum>(string name)
        where TEnum : struct, System.Enum
    {
        string? error = null;
        QueryBinding.TryEnum<TEnum>(request.Query, name, out var value, ref error);
        _error ??= error;
        return value;
    }

    public TEnum? Code<TEnum>(string name, Func<string, TEnum> fromCode)
        where TEnum : struct, System.Enum
    {
        string? error = null;
        QueryBinding.TryCode(request.Query, name, fromCode, out var value, ref error);
        _error ??= error;
        return value;
    }

    public EventCode? EventCode(string name)
    {
        string? error = null;
        QueryBinding.TryEventCode(request.Query, name, out var value, ref error);
        _error ??= error;
        return value;
    }
}
