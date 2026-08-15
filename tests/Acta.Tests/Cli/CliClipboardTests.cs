using Acta.Runtime.Cli;
using Xunit;

namespace Acta.Tests.Cli;

public class CliClipboardTests
{
    [Theory]
    [InlineData("123", "123")]
    [InlineData(" 123 \r\n", "123")]
    [InlineData("order-9", "order-9")]
    public void Valid_clipboard_text_resolves(string text, string expected)
    {
        Assert.True(CliClipboard.TryResolveTarget(text, out var target));
        Assert.Equal(expected, target);
    }

    [Fact]
    public void Strings_up_to_deduplication_key_max_resolve()
    {
        var key = new string('k', DeduplicationKey.MaxLength);

        Assert.True(CliClipboard.TryResolveTarget(key, out var target));
        Assert.Equal(key, target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("first\nsecond")]
    public void Missing_or_multiline_clipboard_text_does_not_resolve(string? text)
    {
        Assert.False(CliClipboard.TryResolveTarget(text, out _));
    }

    [Fact]
    public void Strings_over_deduplication_key_max_do_not_resolve()
    {
        Assert.False(CliClipboard.TryResolveTarget(new string('k', DeduplicationKey.MaxLength + 1), out _));
    }
}

/// <summary>
/// The runner fills a missing target from the clipboard before building the lookup, and reports
/// a usage error when the clipboard yields nothing usable. The fake IJobs resolves exactly one job
/// (<see cref="NothingJobs.KnownJobId"/>), so a not-found exit proves the clipboard value reached
/// the lookup and the signal cases still have a job to address.
/// </summary>
public class CliRunnerClipboardTests
{
    private static async Task<(int ExitCode, string Error)> RunInfoAsync(Func<string?> clipboard)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(jobs, jobs, [], ["shop"], output, error, clipboard);

        var exitCode = await runner.RunAsync(
            new CliCommand(CliVerb.Info, Target: null, null, null, null, null, Json: false),
            TestContext.Current.CancellationToken
        );
        return (exitCode, error.ToString());
    }

    [Fact]
    public async Task Missing_target_resolves_a_job_id_from_the_clipboard()
    {
        var (exitCode, error) = await RunInfoAsync(() => "999\r\n");

        Assert.Equal(2, exitCode);
        Assert.Contains("not found", error);
    }

    [Fact]
    public async Task Missing_target_resolves_a_deduplication_key_from_the_clipboard()
    {
        var (exitCode, error) = await RunInfoAsync(() => "order-9");

        Assert.Equal(2, exitCode);
        Assert.Contains("not found", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("multi\nline clipboard junk")]
    public async Task Unusable_clipboard_reports_missing_id(string? clipboardText)
    {
        var (exitCode, error) = await RunInfoAsync(() => clipboardText);

        Assert.Equal(64, exitCode);
        Assert.Contains("missing", error);
    }

    [Fact]
    public async Task Explicit_target_never_touches_the_clipboard()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(
            jobs,
            jobs,
            [],
            ["shop"],
            output,
            error,
            () => throw new InvalidOperationException("clipboard must not be read")
        );

        var exitCode = await runner.RunAsync(
            new CliCommand(CliVerb.Info, Target: "43", null, null, null, null, Json: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Signal_without_value_raises_presence_only()
    {
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(jobs, jobs, [], ["shop"], new StringWriter(), new StringWriter(), () => null);

        var exit = await runner.RunAsync(
            new CliCommand(CliVerb.Signal, Target: "42", SignalName: "approval", SignalValue: null, null, null, Json: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, exit);
        var call = Assert.Single(jobs.Signals);
        Assert.Equal(("approval", (byte)0, (byte[]?)null), call);
    }

    [Fact]
    public async Task Signal_with_value_passes_a_json_payload_through()
    {
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(jobs, jobs, [], ["shop"], new StringWriter(), new StringWriter(), () => null);
        const string json = "{\"approved\":true}";

        var exit = await runner.RunAsync(
            new CliCommand(CliVerb.Signal, Target: "42", SignalName: "approval", SignalValue: json, null, null, Json: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, exit);
        var (Name, FormatId, Value) = Assert.Single(jobs.Signals);
        Assert.Equal((byte)1, FormatId);
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(Value!));
    }

    /// <summary>
    /// The read verbs resolve the target once, up front, then read again. Between the two the row can
    /// be purged - and "the job produced nothing" and "the job is gone" arrive at the CLI identically.
    /// Reporting the first as success would tell an operator a purged job simply had no output.
    /// </summary>
    [Theory]
    [InlineData("result")]
    [InlineData("events")]
    public async Task A_read_verb_on_a_job_that_vanished_after_the_resolve_exits_not_found(string verb)
    {
        var (exit, output, error) = await RunVerbAsync(verb, vanished: true);

        Assert.Equal(2, exit);
        Assert.Contains("job not found", error);
        Assert.DoesNotContain("(no result)", output);
        Assert.DoesNotContain("(no events)", output);
    }

    [Theory]
    [InlineData("result", "(no result)")]
    [InlineData("events", "(no events)")]
    public async Task A_read_verb_on_a_live_job_with_nothing_to_show_still_succeeds(string verb, string expected)
    {
        var (exit, output, error) = await RunVerbAsync(verb, vanished: false);

        Assert.Equal(0, exit);
        Assert.Contains(expected, output);
        Assert.DoesNotContain("job not found", error);
    }

    // The verb arrives as its CLI spelling because CliVerb is internal to the runtime; parsing it here
    // also proves the two cases address the same verbs an operator actually types.
    private static async Task<(int Exit, string Output, string Error)> RunVerbAsync(string verb, bool vanished)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var jobs = new NothingJobs { JobVanishedAfterResolve = vanished };
        var runner = new CliCommandRunner(jobs, jobs, [], ["shop"], output, error, () => null);

        Assert.True(CliCommandParser.TryParse([verb, "42"], out var command, out var parseError), parseError);
        var exit = await runner.RunAsync(command, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class NothingJobs : IJobs, IActaOperations, ILedger
    {
        /// <summary>The one job id this fake resolves; every other target reads as absent.</summary>
        public const long KnownJobId = 42;

        /// <summary>
        /// Simulates the purge race the read verbs have to survive: the up-front resolve still returns
        /// the snapshot, but every later read finds the row gone. Without it, "no result" / "no events"
        /// and "no job" are indistinguishable.
        /// </summary>
        public bool JobVanishedAfterResolve { get; set; }

        private static readonly JobRef KnownJobRef = new(new Guid("019826f0-0000-7000-8000-00000000002a"));

        public ValueTask<JobDetail?> GetAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult(
                job.Kind == JobLookupKind.JobId && job.JobId == KnownJobId
                    ? new JobDetail(
                        JobId: KnownJobId,
                        JobRef: KnownJobRef,
                        JobNamespace: "shop",
                        DefinitionId: 1,
                        JobName: "send-email",
                        LineageRootId: null,
                        LineageRootJobRef: null,
                        ParentJobId: null,
                        ParentJobRef: null,
                        TenantId: null,
                        TenantKey: null,
                        DeduplicationKey: null,
                        CorrelationKey: null,
                        ExclusiveKey: null,
                        InputFormatId: 0,
                        Status: JobStatusCode.Ready,
                        Priority: JobPriorityCode.Normal,
                        NextRunAtUtc: null,
                        ExecutionNumber: 0,
                        FailureCount: 0,
                        LeasedByWorkerId: null,
                        LeaseExpiresAtUtc: null,
                        RetentionUntilUtc: null,
                        CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
                        ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
                        LeasedByWorkerRef: null
                    )
                    : null
            );

        public ValueTask<JobExplanation?> ExplainAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult<JobExplanation?>(null);

        public ValueTask<JobLineageMap?> GetLineageMapAsync(
            JobLookup job,
            JobLineageMapOptions? options = null,
            CancellationToken ct = default
        ) => ValueTask.FromResult<JobLineageMap?>(null);

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ISchedules Schedules => throw new NotSupportedException();
        public IDefinitions Definitions => throw new NotSupportedException();
        public IWorkers Workers => throw new NotSupportedException();
        public IAlerts Alerts => throw new NotSupportedException();
        public ITenants Tenants => throw new NotSupportedException();
        public INamespaces Namespaces => throw new NotSupportedException();
        public ITags Tags => throw new NotSupportedException();
        public ISettings Settings => throw new NotSupportedException();
        public IOutbox Outbox => throw new NotSupportedException();
        public ILedger Ledger => this;
        public DbProvider Provider => DbProvider.Sqlite;

        public ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        /// <summary>The job carries no retained events; whether it still exists is the status read.</summary>
        public ValueTask<PagedResult<EventListItem>> ListEventsAsync(ListEventsQuery query, CancellationToken ct = default) =>
            ValueTask.FromResult(new PagedResult<EventListItem>([], null, false, 50, null));

        public ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public JobInputTemplate? GetInputTemplate(string jobNamespace, string jobName) => null;

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            JobContract<TInput> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput, TResult>(
            JobContract<TInput, TResult> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(
            JobContract<NoInput> job,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
            JobContract<TInput, TResult> job,
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
            IReadOnlyList<JobEnqueueRequest> requests,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(
            System.Data.Common.DbTransaction transaction,
            JobEnqueueRequest request,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            System.Data.Common.DbTransaction transaction,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            System.Data.Common.DbTransaction transaction,
            JobContract<TInput> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput, TResult>(
            System.Data.Common.DbTransaction transaction,
            JobContract<TInput, TResult> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(
            System.Data.Common.DbTransaction transaction,
            JobContract<NoInput> job,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
            System.Data.Common.DbTransaction transaction,
            IReadOnlyList<JobEnqueueRequest> requests,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<long?> GetJobIdAsync(JobLookup job, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<JobStatusCode?> GetStatusAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult<JobStatusCode?>(
                JobVanishedAfterResolve || job.Kind != JobLookupKind.JobId || job.JobId != KnownJobId ? null : JobStatusCode.Ready
            );

        public ValueTask<JobPayload?> GetInputAsync(JobLookup job, CancellationToken ct = default) => throw new NotSupportedException();

        /// <summary>The job produced no result; whether it still exists is the status read.</summary>
        public ValueTask<JobPayload?> GetResultAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult<JobPayload?>(null);

        public ValueTask<IReadOnlyList<JobCheckpointItem>> GetCheckpointsAsync(JobLookup job, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobControlResult> CancelAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> PauseAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> ResumeAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> RestartAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> RescheduleAsync(
            JobLookup job,
            DateTime nextRunAtUtc,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> ReprioritizeAsync(
            JobLookup job,
            JobPriorityCode priority,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> UpdateJobInputAsync(
            JobLookup job,
            JobPayload input,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> PurgeAsync(JobLookup job, string? actorKey = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobControlResult> RaiseSignalAsync(
            JobLookup job,
            string name,
            CancellationToken ct = default,
            string? actorKey = null
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> RaiseSignalAsync<T>(
            JobLookup job,
            string name,
            T value,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        /// <summary>Recorded signal raises with the delivered payload format and bytes (null when presence-only).</summary>
        public List<(string Name, byte FormatId, byte[]? Value)> Signals { get; } = [];

        public ValueTask<JobControlResult> RaiseSignalAsync(
            JobLookup job,
            string name,
            JobPayload value,
            string? actorKey = null,
            CancellationToken ct = default
        )
        {
            Signals.Add((name, value.Format.Id, value.IsNone ? null : value.Data.ToArray()));
            return ValueTask.FromResult(new JobControlResult(1, ControlAction.Applied, JobStatusCode.Ready));
        }
    }
}
