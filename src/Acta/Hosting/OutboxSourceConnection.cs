namespace Acta;

/// <summary>A producer's source database as its provider package reaches it: its own session and outbox dialect, never the ledger's.</summary>
internal sealed record OutboxSourceConnection(IDbSession Session, ISqlDialect Dialect);
