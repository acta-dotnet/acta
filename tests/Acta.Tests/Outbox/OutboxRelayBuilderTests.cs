using Acta.Runtime.Modules.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// Configuration-time contract for <c>AddOutboxRelay</c>: exactly one source provider, at most one
/// source per worker namespace, canonical source name and valid overrides, and the relay-vs-automatic
/// framework-job partition. No source connection is opened.
/// </summary>
public sealed class OutboxRelayBuilderTests
{
    private static OutboxRelayRegistration? Register(Action<IWorkerBuilder> configure)
    {
        var jb = new ActaBuilder(new ServiceCollection());
        jb.Run("relay-ns", configure);
        return jb.Workers.Single().Relay;
    }

    [Fact]
    public void A_single_source_provider_records_the_relay()
    {
        var relay = Register(w => w.AddOutboxRelay("orders", s => s.UsePostgres(o => o.ConnectionString = "Host=db")));

        Assert.NotNull(relay);
        Assert.Equal("orders", relay!.SourceName);
        Assert.Equal("PostgresOutboxSourceStoreFactory", relay.SourceStoreFactory.GetType().Name);
        Assert.Equal(5, relay.QuarantineThreshold);
    }

    [Fact]
    public void Selecting_no_source_provider_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Register(w => w.AddOutboxRelay("orders", _ => { })));
        Assert.Contains("no provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Selecting_two_source_providers_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Register(w =>
                w.AddOutboxRelay(
                    "orders",
                    s =>
                    {
                        s.UsePostgres(o => o.ConnectionString = "Host=db");
                        s.UseSqlServer(o => o.ConnectionString = "Server=db");
                    }
                )
            )
        );
        Assert.Contains("more than one provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_second_source_on_the_same_worker_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Register(w =>
            {
                w.AddOutboxRelay("orders", s => s.UsePostgres(o => o.ConnectionString = "Host=db"));
                w.AddOutboxRelay("shipments", s => s.UsePostgres(o => o.ConnectionString = "Host=db2"));
            })
        );
        Assert.Contains("at most one", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_source_provider_distinct_from_the_ledger_is_honored()
    {
        // The ledger provider is not selected here; the source builder records SQL Server independently.
        var relay = Register(w => w.AddOutboxRelay("orders", s => s.UseSqlServer(o => o.ConnectionString = "Server=db")));

        Assert.Equal("SqlServerOutboxSourceStoreFactory", relay!.SourceStoreFactory.GetType().Name);
    }

    [Fact]
    public void An_invalid_source_name_throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Register(w => w.AddOutboxRelay("Bad Source!", s => s.UseSqlite(o => o.ConnectionString = "Data Source=x")))
        );
    }

    [Fact]
    public void An_invalid_schema_override_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Register(w =>
                w.AddOutboxRelay(
                    "orders",
                    s =>
                    {
                        s.Schema = "bad schema";
                        s.UsePostgres(o => o.ConnectionString = "Host=db");
                    }
                )
            )
        );
    }

    [Fact]
    public void An_uppercase_table_override_throws()
    {
        // Acta-owned names are lowercase; a mixed-case override folds under PostgreSQL and breaks shape checks.
        Assert.Throws<ArgumentException>(() =>
            Register(w =>
                w.AddOutboxRelay(
                    "orders",
                    s =>
                    {
                        s.Table = "Orders";
                        s.UsePostgres(o => o.ConnectionString = "Host=db");
                    }
                )
            )
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_quarantine_threshold_below_one_throws(int threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Register(w =>
                w.AddOutboxRelay(
                    "orders",
                    s =>
                    {
                        s.QuarantineThreshold = threshold;
                        s.UsePostgres(o => o.ConnectionString = "Host=db");
                    }
                )
            )
        );
    }

    [Fact]
    public void A_missing_source_connection_string_throws()
    {
        Assert.Throws<ArgumentException>(() => Register(w => w.AddOutboxRelay("orders", s => s.UsePostgres(_ => { }))));
    }

    [Fact]
    public void The_relay_framework_set_adds_sys_outbox_and_its_dependencies_without_forcing_retention()
    {
        Assert.Contains("sys.outbox", Acta.Runtime.Modules.Execution.Workers.FrameworkJobs.RelayNames);
        Assert.Contains("sys.recovery", Acta.Runtime.Modules.Execution.Workers.FrameworkJobs.RelayNames);
        Assert.Contains("sys.alerts", Acta.Runtime.Modules.Execution.Workers.FrameworkJobs.RelayNames);
        Assert.DoesNotContain("sys.retention", Acta.Runtime.Modules.Execution.Workers.FrameworkJobs.RelayNames);
        Assert.DoesNotContain("sys.outbox", Acta.Runtime.Modules.Execution.Workers.FrameworkJobs.AutomaticNames);
    }
}
