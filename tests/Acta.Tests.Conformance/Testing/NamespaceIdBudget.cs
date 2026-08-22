namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Fails a server-provider bootstrap fast, with one readable message, when the shared
/// <c>acta_test</c> database's <c>namespaces.id</c> space is nearly spent. <c>namespaces.id</c> is an
/// <c>int</c> IDENTITY/sequence column on both PostgreSQL and SQL Server (ceiling
/// <see cref="int.MaxValue"/>); SQLite's <c>namespaces.id</c> is a 64-bit AUTOINCREMENT and is out of
/// scope. Ids are never reclaimed in the append-only <c>acta_test</c> schema (see
/// <c>ActaTestBase.cs</c>), so consumption only ever grows across every run against a given database.
/// </summary>
/// <remarks>
/// <para>
/// At 32 bits this guard is no longer a countdown the suite walks into on its own: running the
/// solution often enough to spend two billion ids is not a thing that happens. What can still spend
/// them is a defect - an allocator advanced by work that creates no namespace (the shape
/// <c>StartWorkerIdAllocationSpec</c> pins), or a fixture registering namespaces in a loop. So the
/// guard is kept, and it is kept as a runaway detector: if it ever fires, the honest first reading is
/// that something is allocating ids far faster than the suite creates namespaces, and the number in
/// the message is the evidence for that rather than a chore reminder.
/// </para>
/// <para>
/// The two provider fixtures each read their own high-water mark (never <c>MAX(id)</c>, which
/// understates consumption once any row has been deleted, since ids are never reused) and hand it to
/// <see cref="ThrowIfExhausted"/>, which owns the threshold arithmetic and the message text so the two
/// providers cannot drift on either.
/// </para>
/// </remarks>
public static class NamespaceIdBudget
{
    /// <summary>
    /// Highest id an int IDENTITY/sequence column can hand out. Derived from
    /// <see cref="int.MaxValue"/> rather than the literal 2147483647 so this file and both provider
    /// fixtures can never disagree on it.
    /// </summary>
    public const int Ceiling = int.MaxValue;

    /// <summary>
    /// The namespaces-id cost of one full provider conformance leg - the run that actually spends a
    /// given server's ids - measured on 2026-08-22 against a freshly provisioned <c>acta_test</c>,
    /// where PostgreSQL and SQL Server agreed at 726 for the first leg and 725 for the second (the
    /// extra one is the seeded <c>sys</c> row). It sizes <see cref="Threshold"/> and the runs-remaining
    /// figure in the failure message, never whether the guard runs, so drift here is harmless: a stale
    /// value only shifts where the reserve sits, and against a 32-bit ceiling the reserve is a rounding
    /// error either way. Kept as a measured number rather than a round one because it is what makes
    /// the failure message say something concrete about the rate.
    /// </summary>
    public const int IdsPerRun = 725;

    /// <summary>
    /// Size of the reserve, in runs at the measured <see cref="IdsPerRun"/> rate. The guard stops the
    /// run while at least this much budget is left, so the failure is a refusal with headroom rather
    /// than an overflow mid-suite.
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

            namespaces.id is an int IDENTITY/sequence column (ceiling {Ceiling}, from int.MaxValue).
            Current high-water mark: {consumedIds}. Remaining headroom: {remaining} ids, about
            {runsRemaining:F1} more full conformance legs at the measured cost of {IdsPerRun} ids each.

            Read this as a defect report, not as a chore. acta_test is append-only by design and never
            reclaims ids, but at {IdsPerRun} ids per leg the 32-bit space is roughly three million legs
            deep: it is not reachable by testing. Something has been allocating namespace ids without
            creating namespaces - the shape StartWorkerIdAllocationSpec exists to catch - or a fixture
            is registering namespaces in a loop. Find that before doing anything else, because a reset
            hides it and buys only another three million legs of the same silence.

            Once the cause is understood, dropping the acta-test DATABASE (not just the acta_test
            schema) on both servers resets this counter to zero; EnsureDatabaseAndApplyAsync recreates
            the database and reapplies the schema on the next run. Never drop or touch acta-dev - that
            is the everyday database, not the test one.

              docker compose exec -T postgres psql -U postgres -d acta-dev -c 'DROP DATABASE IF EXISTS "acta-test";'
              docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<sa-password>" -C -b -Q "ALTER DATABASE [acta-test] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [acta-test];"

            If the Postgres DROP DATABASE reports acta-test is in use, terminate its open backends first
            (pg_terminate_backend over pg_stat_activity for datname = 'acta-test'), then retry the drop.
            """
        );
}
