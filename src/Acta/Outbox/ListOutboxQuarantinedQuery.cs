namespace Acta;

/// <summary>
/// Paging for <see cref="IOutbox.ListQuarantinedAsync"/> over one source's Quarantined rows, ordered
/// by outbox id (the one portable unique order across source encodings).
/// </summary>
/// <param name="JobNamespace">The namespace whose registered source to read; required.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also count the source's quarantined rows.</param>
public sealed record ListOutboxQuarantinedQuery(
    string JobNamespace,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false
);
