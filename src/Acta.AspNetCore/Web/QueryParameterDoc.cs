using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Endpoint metadata describing one query-string parameter an endpoint reads through
/// <see cref="QueryBinding"/>. Binding by <c>HttpContext.Request.Query</c> keeps the handlers
/// allocation-lean but leaves the generated OpenAPI document blind to the filter surface, so each
/// endpoint declares its parameters with this record and the (test-only) document generation
/// renders them; the product assembly stays free of any OpenAPI dependency.
/// <see cref="CodeKind"/> names a persisted code family (its <c>CodeKind</c> slug) whose kebab
/// codes are the parameter's accepted values; the generator resolves them from
/// <see cref="CodeManifests"/> so the documented vocabulary can never drift from the model.
/// <c>QueryDocCoverageTests</c> holds each endpoint's declarations equal to the query keys its
/// handler actually reads.
/// </summary>
internal sealed record QueryParameterDoc(
    string Name,
    QueryParameterKind Kind,
    string Description,
    bool Repeatable = false,
    bool Required = false,
    string? CodeKind = null
);

/// <summary>Wire shape of a documented query parameter.</summary>
internal enum QueryParameterKind : byte
{
    String = 1,
    Int = 2,
    Bool = 3,
    Instant = 4,
    Long = 5,
}

internal static class QueryParameterDocExtensions
{
    /// <summary>The keyset-paging pair every paged endpoint carries.</summary>
    public static readonly ImmutableArray<QueryParameterDoc> PagingCore =
    [
        new("pageSize", QueryParameterKind.Int, "Rows per page; the server clamps to its bounds."),
        new("cursor", QueryParameterKind.String, "Opaque keyset cursor from the previous page's nextCursor; omit for the first page."),
    ];

    /// <summary>The opt-in filter-wide total most paged endpoints support unconditionally.</summary>
    public static readonly ImmutableArray<QueryParameterDoc> IncludeTotal =
    [
        new("includeTotal", QueryParameterKind.Bool, "Also compute the filter-wide row count (an extra aggregate read)."),
    ];

    /// <summary>The repeatable exact-tag filter block (AND semantics across repeats).</summary>
    public static readonly ImmutableArray<QueryParameterDoc> TagFilter =
    [
        new(
            "tag",
            QueryParameterKind.String,
            "Exact tag filter as name or name:value; repeatable, and every repeat must match (AND).",
            Repeatable: true
        ),
    ];

    public static TBuilder WithQueryParameters<TBuilder>(this TBuilder builder, params QueryParameterDoc[] docs)
        where TBuilder : IEndpointConventionBuilder
    {
        foreach (var doc in docs)
        {
            builder.WithMetadata(doc);
        }
        return builder;
    }
}
