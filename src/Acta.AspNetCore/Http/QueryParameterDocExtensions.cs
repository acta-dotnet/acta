using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;

namespace Acta.AspNetCore;

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
