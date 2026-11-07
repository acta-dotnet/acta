namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ICalendars.ListAsync"/>; families are ordered by name, then namespace.
/// <c>JobNamespace</c> null lists every namespace's families, and a namespace lists its own and the shared ones in
/// <c>sys</c>; <c>Role</c> null matches both roles; <c>NameContains</c> matches a dotted kebab fragment anywhere in the name;
/// <c>LatestStatus</c> null matches every family, else only those whose latest version has that status, such as Draft for a
/// family with a version waiting to be published; <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the
/// previous page's <see cref="IPagedResponse{T}.NextCursor"/>.
/// </summary>
public sealed record ListCalendarsQuery(
    string? JobNamespace = null,
    CalendarRoleCode? Role = null,
    string? NameContains = null,
    CalendarStatusCode? LatestStatus = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false
);
