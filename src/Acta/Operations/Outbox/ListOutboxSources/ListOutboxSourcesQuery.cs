namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IOutbox.ListSourcesAsync"/>. Sources are discovered through the
/// namespace catalog (name ascending), so the cursor pages namespaces; a page may carry fewer items
/// than namespaces when some have no relay slot.
/// </summary>
/// <param name="JobNamespace">Restrict to one namespace; the cursor is ignored when set.</param>
/// <param name="PageSize">Namespaces scanned per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
public sealed record ListOutboxSourcesQuery(string? JobNamespace = null, int? PageSize = null, string? Cursor = null);
