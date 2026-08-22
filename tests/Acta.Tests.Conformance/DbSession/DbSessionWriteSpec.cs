using Acta.Relational.Entities;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.DbSession;

/// <summary>
/// Conformance for the test ORM write seam on <see cref="IDbSession"/>: <c>InsertAsync</c> assigns
/// and reads back a DB identity, <c>UpdateOnlyAsync</c> sets only the listed columns for matching
/// rows, and <c>DeleteAsync</c> removes matching rows. Exercised over <c>JobNamespace</c> on every
/// provider so reads and writes agree on column mapping, quoting, and identity readback.
/// </summary>
[ConformanceSpec(
    "test-orm.write-roundtrip",
    "IDbSession insert/update-only/delete round-trip on every provider",
    Area = "Test ORM",
    Contract = "The test ORM round-trips writes on every provider: insert assigns an identity, update sets only listed columns, delete removes by predicate.",
    Arrange = "A live provider schema exposes namespace rows through the IDbSession test ORM.",
    Act = "InsertAsync, UpdateOnlyAsync, and DeleteAsync run against namespace rows.",
    Assert = "Insert returns the DB-assigned identity, update sets only the listed columns with UTC normalization, and delete removes rows matching the predicate."
)]
public abstract class DbSessionWriteSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "InsertAsync returns a non-zero DB-assigned identity and the row is readable")]
    public async Task InsertAsync_assigns_identity_and_row_is_readable()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        var id = await Db.From<JobNamespace>()
            .InsertAsync<int>(
                new JobNamespace
                {
                    Name = $"{TestNamespace}-insert",
                    OwnerTeam = "team-a",
                    Status = NamespaceStatusCode.Active,
                    CreatedAtUtc = now,
                    ModifiedAtUtc = now,
                },
                ct
            );

        Assert.NotEqual(0, id);
        Assert.NotEqual(TestNamespaceId, id);

        var row = await Db.From<JobNamespace>().Where(n => n.Id == id).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        Assert.Equal($"{TestNamespace}-insert", row!.Name);
        Assert.Equal("team-a", row.OwnerTeam);
    }

    [Fact(DisplayName = "UpdateOnlyAsync sets only the assigned columns for rows matching the predicate")]
    public async Task UpdateOnlyAsync_sets_only_listed_columns_for_matching_rows()
    {
        var ct = TestContext.Current.CancellationToken;

        var affected = await Db.From<JobNamespace>()
            .Where(n => n.Id == TestNamespaceId)
            .UpdateOnlyAsync(() => new JobNamespace { OwnerTeam = "changed", Description = "updated" }, ct);

        Assert.Equal(1, affected);

        var row = await Db.From<JobNamespace>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        Assert.Equal("changed", row!.OwnerTeam);
        Assert.Equal("updated", row.Description);
        // The columns not named in the set selector are untouched.
        Assert.Equal(TestNamespace, row.Name);
    }

    [Fact(DisplayName = "UpdateOnlyAsync normalizes a non-UTC-kind DateTime to UTC on write and read")]
    public async Task UpdateOnlyAsync_normalizes_non_utc_kind_datetime_to_utc()
    {
        var ct = TestContext.Current.CancellationToken;

        // Kind=Unspecified would otherwise be rejected by Npgsql timestamptz binding. The write path
        // must normalize it to UTC, and the read path must return Kind=Utc.
        var ts = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        var affected = await Db.From<JobNamespace>()
            .Where(n => n.Id == TestNamespaceId)
            .UpdateOnlyAsync(() => new JobNamespace { ModifiedAtUtc = ts }, ct);
        Assert.Equal(1, affected);

        var row = await Db.From<JobNamespace>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        Assert.Equal(DateTimeKind.Utc, row!.ModifiedAtUtc.Kind);
        Assert.Equal(DateTime.SpecifyKind(ts, DateTimeKind.Utc), row.ModifiedAtUtc);
    }

    [Fact(DisplayName = "UpdateOnlyAsync with DbFn.UtcNow stamps the server clock")]
    public async Task UpdateOnlyAsync_DbFn_UtcNow_stamps_the_server_clock()
    {
        var ct = TestContext.Current.CancellationToken;

        // DbFn.UtcNow emits SYSUTCDATETIME() / now() as literal SQL, not a bound parameter, so the
        // written value comes from the DB server clock. Bracket the write with two reads of that same
        // clock instead of the test-process clock, so the bound never folds in this process's skew
        // from the container's.
        var clock = Services.GetRequiredService<IActaClock>();
        var before = await clock.GetUtcNowAsync(ct);
        var affected = await Db.From<JobNamespace>()
            .Where(n => n.Id == TestNamespaceId)
            .UpdateOnlyAsync(() => new JobNamespace { ModifiedAtUtc = DbFn.UtcNow }, ct);
        Assert.Equal(1, affected);
        var after = await clock.GetUtcNowAsync(ct);

        var row = await Db.From<JobNamespace>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        Assert.Equal(DateTimeKind.Utc, row!.ModifiedAtUtc.Kind);
        // A one-second slack absorbs modified_at_utc's coarser column precision against the clock
        // read's, the same margin RelativeDelayUsesDbClockSpec uses for the same reason.
        Assert.InRange(row.ModifiedAtUtc, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact(DisplayName = "DeleteAsync and UpdateOnlyAsync with no Where and no All() throw InvalidOperationException")]
    public async Task Unfiltered_write_with_no_All_throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Db.From<JobNamespace>().DeleteAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Db.From<JobNamespace>().UpdateOnlyAsync(() => new JobNamespace { OwnerTeam = "x" }, ct)
        );
    }

    [Fact(DisplayName = "DeleteAsync removes only rows matching the predicate")]
    public async Task DeleteAsync_removes_only_matching_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        var id = await Db.From<JobNamespace>()
            .InsertAsync<int>(
                new JobNamespace
                {
                    Name = $"{TestNamespace}-delete",
                    OwnerTeam = "team-b",
                    Status = NamespaceStatusCode.Active,
                    CreatedAtUtc = now,
                    ModifiedAtUtc = now,
                },
                ct
            );

        var affected = await Db.From<JobNamespace>().Where(n => n.Id == id).DeleteAsync(ct);
        Assert.Equal(1, affected);

        var row = await Db.From<JobNamespace>().Where(n => n.Id == id).SingleOrDefaultAsync(ct);
        Assert.Null(row);
        // The seeded namespace is untouched by the predicate.
        var seeded = await Db.From<JobNamespace>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);
        Assert.NotNull(seeded);
    }
}
