using Acta.Sqlite;
using Acta.Verify;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Verify.Tests;

/// <summary>
/// Chaos proves its outbox cohort: staging counts only the batches that committed and keeps the error that stopped it,
/// and a cohort staged short leaves the case inconclusive, never passed, because delivery is only checked against what
/// committed.
/// </summary>
public sealed class ChaosCaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acta-verify-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(2500)]
    public void A_cohort_staged_short_is_inconclusive_never_pass(int staged) =>
        Assert.Equal(("INCONCLUSIVE", "INCOMPLETE"), ChaosCase.Verdict(safe: true, live: true, killsCovered: true, 5000, staged));

    [Fact]
    public void A_whole_cohort_with_its_kills_passes_and_a_safety_failure_still_fails()
    {
        Assert.Equal(("PASS", "COMPLETE"), ChaosCase.Verdict(safe: true, live: true, killsCovered: true, 5000, 5000));
        Assert.Equal(("FAIL", "INCOMPLETE"), ChaosCase.Verdict(safe: false, live: true, killsCovered: true, 5000, 0));
        Assert.Equal(("FAIL", "COMPLETE"), ChaosCase.Verdict(safe: true, live: false, killsCovered: true, 5000, 5000));
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(2, 2, 2)]
    [InlineData(2, 4, 0)]
    public async Task Staging_counts_only_committed_batches_and_keeps_the_error(int failAfter, int chunk, int committed)
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await ProducerAsync(failAfter, ct);

        var (staged, error) = await ChaosCase.StageAsync(source, total: 6, chunk, seconds: 0, ct);

        Assert.Equal(committed, staged);
        Assert.Contains("producer failed", error, StringComparison.Ordinal);
        await using var connection = new SqliteConnection(source);
        await connection.OpenAsync(ct);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT count(*) FROM domain_obligations) || ':' || (SELECT count(*) FROM acta_outbox)";
        Assert.Equal($"{committed}:{committed}", (string?)await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task A_batch_whose_commit_fails_is_not_counted()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await ProducerAsync(failAfter: 2, ct, atCommit: true);

        var (staged, error) = await ChaosCase.StageAsync(source, total: 6, chunk: 2, seconds: 0, ct);

        Assert.Equal(2, staged);
        Assert.Contains("FOREIGN KEY constraint failed", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staging_that_commits_every_batch_reports_no_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await ProducerAsync(failAfter: null, ct);

        Assert.Equal((6, (string?)null), await ChaosCase.StageAsync(source, total: 6, chunk: 4, seconds: 0, ct));
    }

    /// <summary>
    /// The chaos producer database, with a trigger past <paramref name="failAfter"/> rows that fails the insert, or with
    /// <paramref name="atCommit"/> leaves a deferred foreign key that fails the batch's commit.
    /// </summary>
    private async Task<string> ProducerAsync(int? failAfter, CancellationToken ct, bool atCommit = false)
    {
        Directory.CreateDirectory(_root);
        var source = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, $"producer-{Guid.NewGuid():N}.db"),
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
        await using var connection = new SqliteConnection(source);
        await connection.OpenAsync(ct);
        await using var create = connection.CreateCommand();
        create.CommandText =
            "CREATE TABLE domain_obligations(business_key TEXT PRIMARY KEY);"
            + SqliteMigrations.CreateOutboxScript()
            + (
                failAfter is not { } limit ? ""
                : atCommit
                    ? "CREATE TABLE parents(id INTEGER PRIMARY KEY);"
                        + "CREATE TABLE orphans(parent_id INTEGER REFERENCES parents(id) DEFERRABLE INITIALLY DEFERRED);"
                        + $"CREATE TRIGGER fail_at_commit AFTER INSERT ON domain_obligations WHEN (SELECT count(*) FROM domain_obligations) > {limit} BEGIN INSERT INTO orphans VALUES (42); END;"
                : $"CREATE TRIGGER fail_after BEFORE INSERT ON domain_obligations WHEN (SELECT count(*) FROM domain_obligations) >= {limit} BEGIN SELECT RAISE(ABORT, 'producer failed'); END;"
            );
        await create.ExecuteNonQueryAsync(ct);
        return source;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
