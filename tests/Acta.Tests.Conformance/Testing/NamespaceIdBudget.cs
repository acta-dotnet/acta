namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Fails a server-provider bootstrap fast, with one readable message, when the shared
/// <c>acta_test</c> database's <c>namespaces.id</c> space is nearly spent. <c>namespaces.id</c> is a
/// <c>smallint</c> IDENTITY/sequence column on both PostgreSQL and SQL Server (ceiling
/// <see cref="short.MaxValue"/>); SQLite's <c>namespaces.id</c> is a 64-bit AUTOINCREMENT and is out
/// of scope. Ids are never reclaimed in the append-only <c>acta_test</c> schema (see
/// <c>ActaTestBase.cs</c>), so consumption only ever grows across every run against a given database.
/// Without this check the sequence eventually overflows mid-suite and both providers fail every spec
/// at once with a raw <c>nextval</c> / <c>IDENTITY</c> overflow exception - a failure that reads as a
/// total infrastructure collapse rather than what it actually is: a counter running out.
/// </summary>
/// <remarks>
/// The two provider fixtures each read their own high-water mark (never <c>MAX(id)</c>, which
/// understates consumption once any row has been deleted, since ids are never reused) and hand it to
/// <see cref="ThrowIfExhausted"/>, which owns the threshold arithmetic and the message text so the two
/// providers cannot drift on either.
/// </remarks>
public static class NamespaceIdBudget
{
    /// <summary>
    /// Highest id a smallint IDENTITY/sequence column can hand out. Derived from
    /// <see cref="short.MaxValue"/> rather than the literal 32767 so this file and both provider
    /// fixtures can never disagree on it.
    /// </summary>
    public const int Ceiling = short.MaxValue;

    /// <summary>
    /// Measured, not estimated: one full-solution <c>dotnet test Acta.slnx</c> run advances the
    /// namespaces id counter by exactly this many, deterministically, because every conformance spec
    /// in the run shares the one append-only <c>acta_test</c> schema.
    /// </summary>
    public const int IdsPerRun = 658;

    /// <summary>
    /// Warn this many runs before the wall. A fresh database has roughly <c>Ceiling / IdsPerRun</c>
    /// ~= 49 runs of total budget; 5 runs of headroom (3,290 ids) is enough warning to notice the
    /// message, let whatever is already in flight finish, and reset before the counter actually
    /// reaches <see cref="Ceiling"/> - without firing so early that most of that 49-run budget reads
    /// as "nearly exhausted."
    /// </summary>
    public const int WarningRuns = 5;

    /// <summary>
    /// Remaining headroom, in ids, below which <see cref="ThrowIfExhausted"/> fails the run.
    /// </summary>
    public const int Threshold = WarningRuns * IdsPerRun;

    /// <summary>
    /// Throws when fewer than <see cref="Threshold"/> ids of headroom remain under <see cref="Ceiling"/>.
    /// Throws rather than skipping: a skip would let CI go green while every spec that depends on this
    /// server provider silently does not run, which is worse than a loud, actionable failure.
    /// </summary>
    /// <param name="providerName">Display name for the message, e.g. "PostgreSQL" or "SQL Server".</param>
    /// <param name="consumedIds">
    /// The provider's own allocator high-water mark for ids already handed out (the sequence's
    /// <c>last_value</c> on Postgres, <c>IDENT_CURRENT</c> on SQL Server). A brand-new, never-advanced
    /// allocator reads as something at or near 0 on both providers - Postgres reports it directly as
    /// 0, SQL Server as its identity seed of 1 - either of which is far below <see cref="Threshold"/>,
    /// so a fresh database never trips this check.
    /// </param>
    public static void ThrowIfExhausted(string providerName, long consumedIds)
    {
        var remaining = Ceiling - consumedIds;
        if (remaining >= Threshold)
        {
            return;
        }

        var runsRemaining = remaining / (double)IdsPerRun;
        throw new InvalidOperationException(BuildMessage(providerName, consumedIds, remaining, runsRemaining));
    }

    // Invariant, because the one interpolation with a decimal point renders as "4,2" on a machine whose
    // locale uses a comma - which reads as a thousands separator to half the people who will see it, in
    // the one message whose entire job is to be unambiguous about a number.
    private static string BuildMessage(string providerName, long consumedIds, long remaining, double runsRemaining) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"""
            {providerName}: the shared acta_test database's namespaces.id space is nearly exhausted.

            namespaces.id is a smallint IDENTITY/sequence column (ceiling {Ceiling}, from short.MaxValue).
            Current high-water mark: {consumedIds}. Remaining headroom: {remaining} ids, about
            {runsRemaining:F1} more full-solution `dotnet test Acta.slnx` runs at the measured cost of
            {IdsPerRun} ids per run.

            This is NOT a product defect and NOT a broken container. acta_test is an append-only shared
            schema by design and ids are never reclaimed between runs, so this is a consumed test-database
            resource that has simply been run against enough times to approach its 16-bit ceiling.

            You are being stopped with headroom still left on purpose. Running out mid-suite does not fail
            one spec - it fails roughly thirteen hundred at once, on providers that were green minutes
            earlier, with a raw overflow message that names namespaces and explains nothing. That wreckage
            is far more expensive than this refusal, so the budget is spent down to a reserve and no
            further.

            Fix: drop the acta-test DATABASE (not just the acta_test schema) on both servers.
            EnsureDatabaseAndApplyAsync recreates the database and reapplies the schema automatically on
            the next run, which resets this counter to zero. Never drop or touch acta-dev - that is the
            everyday database, not the test one.

              docker compose exec -T postgres psql -U postgres -d acta-dev -c 'DROP DATABASE IF EXISTS "acta-test";'
              docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<sa-password>" -C -b -Q "ALTER DATABASE [acta-test] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [acta-test];"

            If the Postgres DROP DATABASE reports acta-test is in use, terminate its open backends first
            (pg_terminate_backend over pg_stat_activity for datname = 'acta-test'), then retry the drop.
            """
        );
}
