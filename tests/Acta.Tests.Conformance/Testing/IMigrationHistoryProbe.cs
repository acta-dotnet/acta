namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// A throwaway database location holding at most a migration-history ledger, so a spec can put the
/// bootstrap preflight in front of a history the shared <c>acta_test</c> schema could never have.
/// </summary>
/// <remarks>
/// Deliberately not a schema reset and not an Acta schema: the preflight reads history and nothing
/// else, so the probe carries only the <c>migrations</c> table (or, for the unprovisioned case, not
/// even that). It allocates no namespace ids, touches no shared row, and drops itself on dispose.
/// </remarks>
public interface IMigrationHistoryProbe : IAsyncDisposable
{
    /// <summary>Connection string a provider bootstrap can be pointed at.</summary>
    string ConnectionString { get; }

    /// <summary>Schema name the ledger lives in; <c>main</c> on SQLite.</summary>
    string SchemaName { get; }
}
