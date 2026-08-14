using Microsoft.AspNetCore.Builder;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Endpoint metadata describing one query-string parameter an endpoint reads through
/// <see cref="QueryBinding"/>. Binding by <c>HttpContext.Request.Query</c> keeps the handlers
/// allocation-lean but leaves the generated OpenAPI document blind to the filter surface, so each
/// endpoint declares its parameters with this record and the (test-only) document generation
/// renders them; the product assembly stays free of any OpenAPI dependency. The committed
/// <c>docs/reference/openapi.json</c> gate therefore protects the query vocabulary the same way it
/// protects routes and bodies.
/// </summary>
internal sealed record QueryParameterDoc(string Name, QueryParameterKind Kind, string Description, bool Repeatable = false);

/// <summary>Wire shape of a documented query parameter.</summary>
internal enum QueryParameterKind : byte
{
    String = 1,
    Int = 2,
    Bool = 3,
    Instant = 4,
}

internal static class QueryParameterDocExtensions
{
    /// <summary>The shared cursor-paging block every list endpoint carries.</summary>
    public static readonly QueryParameterDoc[] Paging =
    [
        new("pageSize", QueryParameterKind.Int, "Rows per page; the server clamps to its bounds."),
        new("cursor", QueryParameterKind.String, "Opaque keyset cursor from the previous page's nextCursor; omit for the first page."),
        new("includeTotal", QueryParameterKind.Bool, "Also compute the filter-wide row count (an extra aggregate read)."),
    ];

    /// <summary>The repeatable exact-tag filter block (AND semantics across repeats).</summary>
    public static readonly QueryParameterDoc[] TagFilter =
    [
        new(
            "tag",
            QueryParameterKind.String,
            "Exact tag filter as name or name:value; repeatable, and every repeat must match (AND).",
            Repeatable: true
        ),
    ];

    public static TBuilder WithQueryParameters<TBuilder>(this TBuilder builder, params object[] docsOrBlocks)
        where TBuilder : IEndpointConventionBuilder
    {
        foreach (var entry in docsOrBlocks)
        {
            switch (entry)
            {
                case QueryParameterDoc doc:
                    builder.WithMetadata(doc);
                    break;
                case QueryParameterDoc[] block:
                    foreach (var doc in block)
                    {
                        builder.WithMetadata(doc);
                    }
                    break;
                default:
                    throw new ArgumentException($"Unsupported query parameter doc entry: {entry?.GetType().Name ?? "null"}.");
            }
        }
        return builder;
    }
}
