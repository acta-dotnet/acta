using Acta.Relational.Outbox;
using Acta.Runtime.Modules.Outbox;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// Homes the canonical-outbox-shape table-contract coverage onto the staging core in Acta.Relational:
/// the shared <see cref="OutboxStaging"/> validation and projection, and the <see cref="OutboxMetaWriter"/>
/// meta.tags shape. A staged row is reconstructed the way the relay will (from the columns, parsing meta
/// with the relay's own <see cref="OutboxMetaReader"/> reader) so a new request member cannot silently drop.
/// Also covers the provider staging extension guards (lowercase table override, structural transaction
/// validation) proven on an in-memory SQLite transaction.
/// </summary>
public sealed class OutboxStagingCoreTests
{
    private static JobEnqueueRequest AllMembers() =>
        new(
            "orders",
            "send-receipt",
            JobPayload.Bytes([5, 6, 7]),
            DeduplicationKey: "order-9",
            CorrelationKey: "corr-9",
            ExclusiveKey: "excl-9",
            Priority: JobPriorityCode.High,
            NextRunAtUtc: null,
            DelaySeconds: 45,
            Tags: [new TagInput("env", "prod"), new TagInput("flag", null)],
            ParentJobId: null,
            TenantKey: "tenant-9"
        );

    private static JobEnqueueRequest Reconstruct(OutboxStagingRow row)
    {
        var input =
            row.InputFormatId == 0 ? JobPayload.None : JobPayload.CopyBytes(JobPayloadFormat.ForId(row.InputFormatId), row.InputData ?? []);
        return new JobEnqueueRequest(
            row.JobNamespace,
            row.JobName,
            input,
            row.DeduplicationKey,
            row.CorrelationKey,
            row.ExclusiveKey,
            row.PriorityCode is { } priority ? (JobPriorityCode)priority : null,
            row.NextRunAtUtc,
            row.DelaySeconds,
            OutboxMetaReader.Parse(row.Meta),
            ParentJobId: null,
            row.TenantKey
        );
    }

    [Fact]
    public void All_members_survive_the_row_projection()
    {
        var request = AllMembers();

        var rebuilt = Reconstruct(OutboxStaging.Stage(request));

        Assert.Equal(request.JobNamespace, rebuilt.JobNamespace);
        Assert.Equal(request.JobName, rebuilt.JobName);
        Assert.Equal(request.Input.Format.Id, rebuilt.Input.Format.Id);
        Assert.Equal(request.Input.Data.ToArray(), rebuilt.Input.Data.ToArray());
        Assert.Equal(request.DeduplicationKey, rebuilt.DeduplicationKey);
        Assert.Equal(request.CorrelationKey, rebuilt.CorrelationKey);
        Assert.Equal(request.ExclusiveKey, rebuilt.ExclusiveKey);
        Assert.Equal(request.Priority, rebuilt.Priority);
        Assert.Equal(request.NextRunAtUtc, rebuilt.NextRunAtUtc);
        Assert.Equal(request.DelaySeconds, rebuilt.DelaySeconds);
        Assert.Equal(request.TenantKey, rebuilt.TenantKey);
        Assert.Null(rebuilt.ParentJobId);
        Assert.Collection(
            rebuilt.Tags!,
            t => Assert.Equal(("env", "prod"), (t.Name, t.Value)),
            t => Assert.Equal(("flag", (string?)null), (t.Name, t.Value))
        );
    }

    public static TheoryData<string, JobPayload> Payloads() =>
        new()
        {
            { "none", JobPayload.None },
            { "json", JobPayload.CopyBytes(JobPayloadFormat.Json, "{\"a\":1}"u8) },
            { "text", JobPayload.Text("hello") },
            { "empty-text", JobPayload.Text("") },
            { "bytes", JobPayload.Bytes([1, 2, 3, 4]) },
            { "custom-format", JobPayload.CopyBytes(JobPayloadFormat.Custom(200, "proto"), [9, 8, 7]) },
        };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Payload_kinds_round_trip(string _, JobPayload payload)
    {
        var request = new JobEnqueueRequest("orders", "send-receipt", payload, DeduplicationKey: "order-1");

        var rebuilt = Reconstruct(OutboxStaging.Stage(request));

        Assert.Equal(request.Input.Format.Id, rebuilt.Input.Format.Id);
        Assert.Equal(request.Input.Data.ToArray(), rebuilt.Input.Data.ToArray());
    }

    [Fact]
    public void Stage_requires_a_deduplication_key()
    {
        var request = new JobEnqueueRequest("orders", "send-receipt", JobPayload.None, DeduplicationKey: null);

        var ex = Assert.Throws<ArgumentException>(() => OutboxStaging.Stage(request));
        Assert.Contains("DeduplicationKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_rejects_a_parent_id()
    {
        var request = new JobEnqueueRequest("orders", "send-receipt", JobPayload.Text("hi"), DeduplicationKey: "k") with
        {
            ParentJobId = 42,
        };

        var ex = Assert.Throws<ArgumentException>(() => OutboxStaging.Stage(request));
        Assert.Contains("ParentJobId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_rejects_next_run_and_delay_together()
    {
        var request = new JobEnqueueRequest("orders", "send-receipt", JobPayload.None, DeduplicationKey: "k")
        {
            NextRunAtUtc = DateTime.UtcNow,
            DelaySeconds = 30,
        };

        Assert.Throws<ArgumentException>(() => OutboxStaging.Stage(request));
    }

    [Fact]
    public void Meta_writes_the_exact_tag_array_shape_with_explicit_null()
    {
        var json = OutboxMetaWriter.Write([new TagInput("tenant", "acme"), new TagInput("urgent", null)]);

        Assert.Equal("{\"tags\":[{\"name\":\"tenant\",\"value\":\"acme\"},{\"name\":\"urgent\",\"value\":null}]}", json);
    }

    [Fact]
    public void A_non_utc_next_run_is_normalized_to_utc()
    {
        // The staging projection must normalize like the owned enqueue path (DbParams.ToUtc): a Local instant
        // is converted to UTC (never persisting the wall-clock reading as UTC, which PG rejects and
        // mssql/sqlite would silently mis-store).
        var local = new DateTime(2035, 6, 1, 12, 0, 0, DateTimeKind.Local);

        var row = OutboxStaging.Stage(
            new JobEnqueueRequest("orders", "send-receipt", JobPayload.None, DeduplicationKey: "k", NextRunAtUtc: local)
        );

        Assert.NotNull(row.NextRunAtUtc);
        Assert.Equal(DateTimeKind.Utc, row.NextRunAtUtc!.Value.Kind);
        Assert.Equal(local.ToUniversalTime(), row.NextRunAtUtc.Value);
    }

    [Fact]
    public void No_tags_produce_a_null_meta_column()
    {
        Assert.Null(OutboxMetaWriter.Write(null));
        Assert.Null(OutboxMetaWriter.Write([]));
        Assert.Null(OutboxStaging.Stage(new JobEnqueueRequest("orders", "j", JobPayload.None, DeduplicationKey: "k")).Meta);
    }

    // Extension-level guards on the provider staging primitive, proven on an in-memory SQLite transaction
    // (no server): an uppercase table override is rejected before any I/O by the same lowercase identifier
    // validation the relay source uses, and a completed transaction is rejected by the structural transaction
    // validation that mirrors the transactional IJobs overloads.
    private static JobEnqueueRequest Valid() => new("orders", "send", JobPayload.Text("hi"), DeduplicationKey: "k");

    [Fact]
    public async Task An_uppercase_table_override_is_rejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            transaction.AddToActaOutboxAsync(Valid(), "Orders", cancellationToken: TestContext.Current.CancellationToken)
        );
        Assert.Contains("uppercase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_completed_transaction_is_rejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transaction.AddToActaOutboxAsync(Valid(), cancellationToken: TestContext.Current.CancellationToken)
        );
    }
}
