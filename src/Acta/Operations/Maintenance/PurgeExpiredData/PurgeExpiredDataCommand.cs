namespace Acta;

/// <summary>One bounded purge batch: a section of one namespace older than the cutoff.</summary>
internal sealed record PurgeExpiredDataCommand(int NamespaceId, RetentionSection Section, DateTime CutoffUtc, int BatchSize);
