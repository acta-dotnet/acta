namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ILedger.ListJobsAsync"/>; jobs are ordered newest first (created_at_utc
/// descending, id descending). <c>JobNamespace</c> is required with <c>JobName</c>; <c>TenantKey</c> and <c>Lane</c>
/// fold lowercase, <c>CorrelationKey</c> matches verbatim and case-sensitively; <c>TerminalOnly</c> true restricts to
/// Succeeded, Failed, and Cancelled, and <c>RecurringOnly</c> true to jobs with at least one live schedule (null or
/// false skips either); <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous page's
/// <see cref="PagedResult{T}.NextCursor"/>; <c>Tags</c> restricts to jobs carrying every supplied tag filter.
/// </summary>
public sealed record ListJobsQuery(
    string? JobNamespace = null,
    JobStatusCode? Status = null,
    string? JobName = null,
    long? ParentJobId = null,
    int? TenantId = null,
    string? TenantKey = null,
    string? CorrelationKey = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null,
    bool? TerminalOnly = null,
    bool? RecurringOnly = null,
    string? Lane = null
);
