namespace Acta;

/// <summary>
/// One batch of the jobs walk of one namespace: it reads the jobs before <see cref="Before"/>, newer to older, and deletes
/// the finished ones whose retention ran out before <see cref="CutoffUtc"/>; null starts a cycle at the newest job old
/// enough to have expired.
/// </summary>
internal sealed record PurgeExpiredJobsCommand(int NamespaceId, DateTime CutoffUtc, int BatchSize, RetentionCursor? Before = null);
