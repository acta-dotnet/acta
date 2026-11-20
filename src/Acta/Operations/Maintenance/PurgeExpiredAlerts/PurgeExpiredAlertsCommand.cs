namespace Acta;

/// <summary>One batch of one namespace's alerts created at or before <see cref="CutoffUtc"/>, delivered or not.</summary>
internal sealed record PurgeExpiredAlertsCommand(int NamespaceId, DateTime CutoffUtc, int BatchSize);
