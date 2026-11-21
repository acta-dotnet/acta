namespace Acta;

/// <summary>
/// One batch of the jobs walk of one namespace: it reads the jobs whose retention ran out before <see cref="CutoffUtc"/>,
/// in order of that date after <see cref="After"/>, and deletes the ones its guards let go; null starts at the oldest.
/// </summary>
internal sealed record PurgeExpiredJobsCommand(int NamespaceId, DateTime CutoffUtc, int BatchSize, RetentionCursor? After = null);
