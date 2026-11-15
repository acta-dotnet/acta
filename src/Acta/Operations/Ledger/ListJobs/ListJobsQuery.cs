namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ILedger.ListJobsAsync"/>, newest first (created_at_utc, id descending). <c>JobNamespace</c> is
/// required with <c>JobName</c>; the names (<c>JobNamespace</c>, <c>JobName</c>, <c>TenantName</c>, <c>Lane</c>) fold lowercase,
/// <c>CorrelationKey</c> is trimmed like a key and matches case-sensitively; <c>Parent</c> restricts to its children, none when it is not
/// there; <c>Finished</c> true restricts to Succeeded, Failed, and Cancelled, false to every other status, and null skips it; <c>Kind</c>
/// restricts to jobs of that kind, a permanent job with or without a schedule; <c>PageSize</c> null defaults to 50 and clamps at 100;
/// <c>Cursor</c> is the previous page's <see cref="IPagedResponse{T}.NextCursor"/>; <c>Tags</c> restricts to jobs with every tag filter.
/// </summary>
public sealed record ListJobsQuery(
    string? JobNamespace = null,
    JobStatusCode? Status = null,
    string? JobName = null,
    JobLookup? Parent = null,
    int? TenantId = null,
    string? TenantName = null,
    string? CorrelationKey = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null,
    bool? Finished = null,
    JobKindCode? Kind = null,
    string? Lane = null
);
