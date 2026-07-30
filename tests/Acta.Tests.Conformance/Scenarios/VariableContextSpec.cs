using System.Data.Common;
using System.Text;
using Acta.Configuration;
using Acta.Modules.Execution;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

[ConformanceSpec(
    "variables.context-api",
    "Job variables round-trip through the context API with versioning and validation",
    Area = "Variables",
    Contract = "The variable context API persists set/get/get-or-set/delete/exists with last-writer-wins versioning, idempotent delete, format fidelity and payload validation.",
    Arrange = "Variable-exercising job definitions are registered, including a race probe and a job that reads a deliberately corrupted JSON variable.",
    Act = "Handlers drive the full variable API - set, get, get-or-set, delete, exists, and progress - across lifecycle, versioning, race, and validation jobs.",
    Assert = "Variables persist with last-writer-wins versioning and idempotent delete, and invalid names or payloads are rejected."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CheckpointSlotAsync))]
public abstract class VariableContextSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "The full variable lifecycle round-trips through the context API with a factory run once and idempotent delete")]
    public async Task Variable_lifecycle_round_trips_through_context_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-lifecycle", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariableLifecycleResult>(enqueued, ct))!;
        Assert.False(result.ExistsBefore);
        Assert.Null(result.AbsentValue);
        Assert.Equal("fallback", result.DefaultValue);
        Assert.Equal(-1, result.DefaultIntAbsent);
        Assert.True(result.RequiredAbsentValueTypeThrew);
        Assert.Equal("Job variable 'never.set' does not exist.", result.RequiredAbsentMessage);
        Assert.True(result.ExistsAfterSet);
        Assert.Equal("done", result.RequiredValue);

        // GetOrSet: first call inserts, second returns the stored winner and never runs its factory.
        Assert.Equal(7, result.InsertedRetryCount);
        Assert.Equal(7, result.ExistingRetryCount);
        Assert.Equal(1, result.FactoryCalls);

        // Delete is idempotent: first removes the row, second is a no-op, value then reads as absent.
        Assert.True(result.DeleteFirst);
        Assert.False(result.DeleteSecond);
        Assert.Null(result.DeletedValue);
    }

    [Fact(DisplayName = "Payload formats (JSON, empty Text, empty Bytes) persist faithfully")]
    public async Task Variable_payload_formats_are_persisted_as_expected()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-persistence", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariablePersistenceResult>(enqueued, ct))!;
        Assert.Equal(string.Empty, result.EmptyText);
        Assert.Equal(0, result.EmptyBytesLength);

        var rows = await Db.From<JobCheckpoint>()
            .Where(v => v.JobId == enqueued.JobId && v.Kind == JobCheckpointKindCode.Variable)
            .ToListAsync(ct);

        Assert.Contains(rows, v => v.Name == "fetch.status" && v.ValueFormatId == JobPayloadFormat.Json.Id);
        Assert.Contains(
            rows,
            v => v.Name == "payload.empty-text" && v.ValueFormatId == JobPayloadFormat.Text.Id && v.Value is { Length: 0 }
        );
        Assert.Contains(
            rows,
            v => v.Name == "payload.empty-bytes" && v.ValueFormatId == JobPayloadFormat.Bytes.Id && v.Value is { Length: 0 }
        );
    }

    [Fact(DisplayName = "Progress is written as the sys.progress progress checkpoint in JSON")]
    public async Task Progress_is_written_as_system_variable()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-persistence", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var progress = await Db.From<JobCheckpoint>()
            .Where(v => v.JobId == enqueued.JobId && v.Kind == JobCheckpointKindCode.Progress && v.Name == "sys.progress")
            .SingleOrDefaultAsync(ct);

        Assert.NotNull(progress);
        Assert.Equal(JobPayloadFormat.Json.Id, progress!.ValueFormatId);
    }

    [Fact(DisplayName = "Variables are inspectable as text with plain SQL over the checkpoints table")]
    public async Task Variables_are_inspectable_with_plain_sql()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-persistence", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var fetchValueText = await ReadValueTextAsync(Db, enqueued.JobId, "fetch.status", ct);
        var progressValueText = await ReadValueTextAsync(Db, enqueued.JobId, "sys.progress", ct);

        Assert.Equal("\"done\"", fetchValueText);
        Assert.Equal("\"stage-two\"", progressValueText);
    }

    [Fact(DisplayName = "Set is last-writer-wins and increments the version")]
    public async Task Set_is_last_writer_wins_and_increments_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-versioning", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariableVersioningResult>(enqueued, ct))!;
        Assert.Equal("v2", result.OverwrittenValue);

        var row = await ReadVariableRowAsync(enqueued.JobId, "fetch.status", ct);
        Assert.NotNull(row);
        Assert.Equal("\"v2\"", Encoding.UTF8.GetString(row!.Value!));
        Assert.Equal(1, row.Version);
    }

    [Fact(DisplayName = "Get-or-set preserves the existing row, runs the factory once and does not bump the version")]
    public async Task Get_or_set_preserves_existing_row_without_incrementing_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-versioning", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariableVersioningResult>(enqueued, ct))!;
        Assert.Equal(7, result.GetOrSetInsertedValue);
        Assert.Equal(7, result.GetOrSetExistingValue);
        Assert.Equal(1, result.FactoryCalls);

        var row = await ReadVariableRowAsync(enqueued.JobId, "retry-count", ct);
        Assert.NotNull(row);
        Assert.Equal("7", Encoding.UTF8.GetString(row!.Value!));
        Assert.Equal(0, row.Version);
    }

    [Fact(DisplayName = "Validation rejects invalid names, nulls and invalid payloads")]
    public async Task Variable_validation_rejects_invalid_names_nulls_and_invalid_payloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-validation", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariableValidationResult>(enqueued, ct))!;
        Assert.True(result.InvalidNamesRejected);
        Assert.True(result.ReservedSetRejected);
        Assert.True(result.ReservedDeleteRejected);
        Assert.True(result.NonePayloadRejected);
        Assert.True(result.UnregisteredPayloadRejected);
        Assert.True(result.JsonNullPayloadRejected);
        Assert.Equal(0, result.EmptyBytesLength);
        Assert.True(result.NullSetRejected);
        Assert.True(result.NullFactoryRejected);
    }

    [Fact(DisplayName = "Concurrent get-or-set stores one value and every caller observes the winner")]
    public async Task Concurrent_get_or_set_stores_one_value_and_returns_the_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-race", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariableRaceResult>(enqueued, ct))!;
        Assert.Equal(1, result.DistinctObservedValues);
        Assert.All(result.ObservedValues, value => Assert.Equal(result.StoredValue, value));
        Assert.InRange(result.StoredValue, 0, 15);
        Assert.InRange(result.FactoryCalls, 1, 16);

        var rowCount = await Db.From<JobCheckpoint>()
            .Where(v => v.JobId == enqueued.JobId && v.Kind == JobCheckpointKindCode.Variable && v.Name == "race.winner")
            .CountAsync(ct);
        Assert.Equal(1, rowCount);
    }

    [Fact(DisplayName = "Variables round-trip common JSON value shapes including large values")]
    public async Task Variables_round_trip_common_json_value_shapes()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-roundtrip", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariableRoundTripResult>(enqueued, ct))!;
        Assert.True(result.PrimitiveValues);
        Assert.True(result.StringValues);
        Assert.True(result.ObjectValues);
        Assert.Equal("updated", result.OverwrittenValue);
        Assert.Equal(10_000, result.LargeValueLength);
    }

    [Fact(DisplayName = "A corrupted JSON variable read fails instead of falling back")]
    public async Task Corrupted_json_variable_read_fails_instead_of_using_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-variable-corrupt-reader", JobPayload.None), ct);

        {
            await InsertCorruptedJsonVariableAsync(Db, enqueued.JobId, ct);
        }

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<VariableCorruptReadResult>(enqueued, ct))!;
        Assert.True(result.Rejected);
        Assert.False(result.FactoryRan);
    }

    private async Task<JobCheckpoint?> ReadVariableRowAsync(long jobId, string name, CancellationToken ct)
    {
        return await Db.From<JobCheckpoint>()
            .Where(v => v.JobId == jobId && v.Kind == JobCheckpointKindCode.Variable && v.Name == name)
            .SingleOrDefaultAsync(ct);
    }

    // The operator inspectability contract: a stored text/json payload decodes with one provider-native
    // cast over the base table, no view or helper required (the same recipe docs/09 documents).
    private static async Task<string?> ReadValueTextAsync(IDbSession session, long jobId, string name, CancellationToken ct)
    {
        var cast = session.Provider switch
        {
            DbProvider.Postgres => "convert_from(value, 'UTF8')",
            DbProvider.SqlServer => "CAST(value AS varchar(max)) COLLATE Latin1_General_100_BIN2_UTF8",
            _ => "CAST(value AS TEXT)",
        };

        await using var connection = await session.GetConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {cast} FROM {session.Schema}.checkpoints WHERE job_id = @p_job_id AND name = @p_name";
        Add(cmd, "@p_job_id", jobId);
        Add(cmd, "@p_name", name);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value as string;
    }

    private static async Task InsertCorruptedJsonVariableAsync(IDbSession session, long jobId, CancellationToken ct)
    {
        // Bypass the JobContext API on purpose: write a row whose value_format_id claims JSON but whose
        // bytes are not valid JSON, simulating a value corrupted outside the framework's write path.
        await session.ExecuteRawAsync(
            """
            INSERT INTO {schema}.checkpoints (
                job_id, kind_code, name,
                value_format_id, value,
                created_at_utc, modified_at_utc, version)
            VALUES (
                @p_job_id, @p_kind_code, @p_name,
                @p_value_format_id, @p_value,
                @p_now, @p_now, 0)
            """,
            ct,
            ("@p_job_id", jobId),
            ("@p_kind_code", (byte)JobCheckpointKindCode.Variable),
            ("@p_name", "corrupt.value"),
            ("@p_value_format_id", JobPayloadFormat.Json.Id),
            ("@p_value", Encoding.UTF8.GetBytes("{InvalidJson")),
            ("@p_now", DateTime.UtcNow)
        );
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        // SQLite stores instants as epoch milliseconds (INTEGER); the driver would otherwise bind a
        // DateTime as TEXT. Match the provider encoding for these raw seeding inserts.
        if (value is DateTime dt && cmd.Connection?.GetType().Name == "SqliteConnection")
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
