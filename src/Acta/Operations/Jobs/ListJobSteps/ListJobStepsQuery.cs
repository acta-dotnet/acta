namespace Acta;

/// <summary>
/// One page of a job's step slots for <see cref="IJobs.ListStepsAsync"/>, newest first. <c>PageSize</c> null defaults to
/// 50 and clamps at 100; <c>Cursor</c> is the previous page's <see cref="IPagedResponse{T}.NextCursor"/>; a job that does
/// not exist answers an empty page.
/// </summary>
public sealed record ListJobStepsQuery(JobLookup Job, int? PageSize = null, string? Cursor = null, bool IncludeTotal = false);
