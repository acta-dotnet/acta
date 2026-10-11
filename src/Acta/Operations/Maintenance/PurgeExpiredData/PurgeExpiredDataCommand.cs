namespace Acta;

/// <summary>
/// One bounded purge batch: a section of one namespace older than the cutoff. The jobs section walks runtimes downward
/// from <see cref="BeforeJobId"/>, or from the newest row when zero, and deletes the namespace's expired jobs in it.
/// </summary>
internal sealed record PurgeExpiredDataCommand(
    int NamespaceId,
    RetentionSection Section,
    DateTime CutoffUtc,
    int BatchSize,
    long BeforeJobId = 0
);
