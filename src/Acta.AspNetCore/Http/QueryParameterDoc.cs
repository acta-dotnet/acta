namespace Acta.AspNetCore;

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
