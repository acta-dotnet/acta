namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ICalendars.ListAsync"/>; families are ordered by name, then namespace.
/// <c>JobNamespace</c> null lists every namespace's families, and a namespace lists its own and the shared ones in
/// <c>sys</c>; <c>Role</c> null matches both roles; <c>NameSearch</c> matches a dotted kebab fragment anywhere in the name;
/// <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous page's
/// <see cref="IPagedResponse{T}.NextCursor"/>.
/// </summary>
public sealed record ListCalendarsQuery(
    string? JobNamespace = null,
    CalendarRoleCode? Role = null,
    string? NameSearch = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false
);
