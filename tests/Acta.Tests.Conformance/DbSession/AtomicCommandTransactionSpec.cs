using System.Data.Common;
using Acta.Relational.Entities;
using Acta.Relational.Schema;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.DbSession;

[ConformanceSpec(
    "session.atomic-command",
    "Commands keep mutation and audit in one transaction",
    Area = "Storage",
    Contract = "SQL failure rolls back the operation and successful caller-owned writes leave transaction finalization to the caller.",
    Arrange = "A namespace with known metadata and version is updated using the provider's embedded command.",
    Act = "The audit insert or mapper fails, or the caller explicitly commits or rolls back the successful command.",
    Assert = "Mutation and audit persist or roll back together without replay after client mapping failures."
)]
public abstract class AtomicCommandTransactionSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly StoreCommand Command = new("Execution", "Namespaces/UpdateNamespace");

    private Action<DbCommand> Bind(int version, ActorCode actor = ActorCode.Operator) =>
        cmd =>
        {
            var dialect = Services.GetRequiredService<ISqlDialect>();
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, TestNamespace));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.OwnerTeam, "inline-updated"));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.Description, "inline-description"));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, version));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, actor));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, "inline-test"));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, "inline-transaction"));
        };

    private async Task<JobNamespace> ReadAsync(CancellationToken ct) =>
        (await Db.From<JobNamespace>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct))!;

    private Task<IReadOnlyList<JobEvent>> EventsAsync(CancellationToken ct) =>
        Db.From<JobEvent>().Where(e => e.NamespaceId == TestNamespaceId && e.EventCode == EventCode.NamespaceUpdated).ToListAsync(ct);

    [Fact(DisplayName = "An audit constraint failure rolls back the namespace mutation")]
    public async Task Sql_failure_rolls_back_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await ReadAsync(ct);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Db.ExecuteAsync(Command, Bind(before.Version, (ActorCode)255), static _ => true, ct)
        );

        var after = await ReadAsync(ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.OwnerTeam, after.OwnerTeam);
        Assert.Empty(await EventsAsync(ct));
    }

    [Fact(DisplayName = "A mapper failure cannot replay a server commit; SQLite rolls back its owned transaction")]
    public async Task Mapper_failure_preserves_atomicity_without_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await ReadAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Db.ExecuteAsync<bool>(Command, Bind(before.Version), static _ => throw new InvalidOperationException("mapper failed"), ct)
        );

        var after = await ReadAsync(ct);
        var applied = after.Version - before.Version;
        Assert.InRange(applied, 0, Db.Provider == DbProvider.Sqlite ? 0 : 1);
        Assert.Equal(applied, (await EventsAsync(ct)).Count);
    }

    [Theory(DisplayName = "A mutation joins the caller's transaction without finalizing it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_controls_commit_or_rollback(bool commit)
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await ReadAsync(ct);
        await using (var connection = await Db.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            var rows = await Db.ExecuteInTransactionAsync(
                transaction,
                Command,
                Bind(before.Version),
                static reader => Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                ct
            );
            Assert.Equal(1, Assert.Single(rows));
            Assert.Same(connection, transaction.Connection);
            if (commit)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }
        }

        var after = await ReadAsync(ct);
        Assert.Equal(before.Version + (commit ? 1 : 0), after.Version);
        Assert.Equal(commit ? "inline-updated" : before.OwnerTeam, after.OwnerTeam);
        Assert.Equal(commit ? 1 : 0, (await EventsAsync(ct)).Count);
    }
}
