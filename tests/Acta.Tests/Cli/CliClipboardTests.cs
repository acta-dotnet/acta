using Xunit;

namespace Acta.Tests.Cli;

public class CliClipboardTests
{
    [Theory]
    [InlineData("123", "123")]
    [InlineData(" 123 \r\n", "123")]
    [InlineData("order-9", "order-9")]
    [InlineData("id:42", "id:42")]
    [InlineData("job_019826f000007000800000000000002a\n", "job_019826f000007000800000000000002a")]
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

    // A job ref, an id:<n>, and a deduplication key are all one token of printable ASCII, so nothing else is a target.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("first\nsecond")]
    [InlineData("two words")]
    [InlineData("caf\u00e9")]
    public void Clipboard_text_that_is_not_one_target_token_does_not_resolve(string? text)
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
/// The runner resolves its target as the HTTP API does: a <c>job_...</c> target is a job ref, <c>id:&lt;n&gt;</c> the
/// internal id on a read verb only, and anything else, a bare integer included, a deduplication key. It fills a read
/// verb's missing target from the clipboard and names what it took, and refuses a control without one. The fake IJobs
/// resolves exactly one job, by <see cref="NothingJobs.KnownJobId"/>, its ref, or <see cref="NothingJobs.KnownDeduplicationKey"/>
/// in <c>shop</c>, and records every lookup and control, so each case shows what the target resolved to.
/// </summary>
public class CliRunnerClipboardTests
{
    private static async Task<(int ExitCode, string Error, NothingJobs Jobs)> RunInfoAsync(Func<string?> clipboard)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(jobs, jobs, [], ["shop"], output, error, clipboard);

        var exitCode = await runner.RunAsync(
            new CliCommand(CliVerb.Info, Target: null, null, null, null, null, Json: false),
            TestContext.Current.CancellationToken
        );
        return (exitCode, error.ToString(), jobs);
    }

    [Theory]
    [InlineData("4711\r\n", "4711", JobLookupKind.DeduplicationKey)]
    [InlineData("id:42", "id:42", JobLookupKind.JobId)]
    [InlineData(" job_019826f000007000800000000000002a ", "job_019826f000007000800000000000002a", JobLookupKind.JobRef)]
    public async Task A_read_takes_a_missing_target_from_the_clipboard_and_names_it(string clipboardText, string target, JobLookupKind kind)
    {
        var (exitCode, error, jobs) = await RunInfoAsync(() => clipboardText);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Target from the clipboard: {target}", error);
        Assert.Equal(kind, Assert.Single(jobs.Lookups).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("multi\nline clipboard junk")]
    [InlineData("two words")]
    public async Task Unusable_clipboard_reports_a_missing_target(string? clipboardText)
    {
        var (exitCode, error, jobs) = await RunInfoAsync(() => clipboardText);

        Assert.Equal(64, exitCode);
        Assert.Contains("missing", error);
        Assert.Empty(jobs.Lookups);
    }

    // internal because CliVerb is internal (CS0051); xUnit v3 discovers internal theories.
    [Theory]
    [InlineData(CliVerb.Cancel)]
    [InlineData(CliVerb.Pause)]
    [InlineData(CliVerb.Resume)]
    [InlineData(CliVerb.Restart)]
    [InlineData(CliVerb.Signal)]
    [InlineData(CliVerb.Debug)]
    internal async Task A_control_without_a_target_is_a_usage_error_and_never_reads_the_clipboard(CliVerb verb)
    {
        var error = new StringWriter();
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(
            jobs,
            jobs,
            [],
            ["shop"],
            new StringWriter(),
            error,
            () => throw new InvalidOperationException("clipboard must not be read")
        );

        var exit = await runner.RunAsync(
            new CliCommand(verb, Target: null, SignalName: "approval", null, null, null, Json: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(64, exit);
        Assert.Contains("job ref or deduplication key", error.ToString());
        Assert.Empty(jobs.Lookups);
    }

    [Theory]
    [InlineData("cancel", "id:42")]
    [InlineData("pause", "id:42")]
    [InlineData("resume", "id:42")]
    [InlineData("restart", "ID:42")]
    [InlineData("signal", "id:42", "approval")]
    [InlineData("debug", "id:42")]
    public async Task A_control_given_an_internal_id_is_a_usage_error_that_resolves_nothing(params string[] args)
    {
        var (exit, _, error, jobs) = await RunArgsAsync(args);

        Assert.Equal(64, exit);
        Assert.Contains("A control takes a job ref or a deduplication key", error);
        Assert.Empty(jobs.Lookups);
        Assert.Empty(jobs.Controls);
        Assert.Empty(jobs.Signals);
    }

    [Theory]
    [InlineData("info", "id:42")]
    [InlineData("info", "ID:42")]
    [InlineData("status", "id:42")]
    [InlineData("result", "id:42")]
    [InlineData("events", "id:42")]
    public async Task A_read_given_an_internal_id_looks_the_job_up_by_it(params string[] args)
    {
        var (exit, _, _, jobs) = await RunArgsAsync(args);

        Assert.Equal(0, exit);
        Assert.Equal(JobLookup.ById(NothingJobs.KnownJobId), Assert.Single(jobs.Lookups));
    }

    [Fact]
    public async Task Explain_given_an_internal_id_looks_the_job_up_by_it()
    {
        var (exit, _, _, jobs) = await RunArgsAsync("explain", "id:42");

        // The fake explains nothing, so the read ends not found rather than refused as a usage error.
        Assert.Equal(2, exit);
        Assert.Equal(JobLookup.ById(NothingJobs.KnownJobId), Assert.Single(jobs.Lookups));
    }

    [Theory]
    [InlineData("info", "id:")]
    [InlineData("info", "id:0")]
    [InlineData("info", "id:abc")]
    public async Task An_id_that_is_not_a_positive_integer_is_a_usage_error(params string[] args)
    {
        var (exit, _, error, jobs) = await RunArgsAsync(args);

        Assert.Equal(64, exit);
        Assert.Contains("positive integer", error);
        Assert.Empty(jobs.Lookups);
    }

    [Fact]
    public async Task A_bare_integer_is_a_deduplication_key_in_the_namespace()
    {
        var (exit, _, _, jobs) = await RunArgsAsync("cancel", NothingJobs.KnownDeduplicationKey, "--ns", "shop");

        Assert.Equal(0, exit);
        Assert.Equal(JobLookup.ByDeduplicationKey("shop", NothingJobs.KnownDeduplicationKey), Assert.Single(jobs.Lookups));
        Assert.Equal(JobLookup.ById(NothingJobs.KnownJobId), Assert.Single(jobs.Controls));
    }

    /// <summary>
    /// The integer is the known job's internal id, and no job holds it as a deduplication key, so the verb finds
    /// nothing and acts on nothing.
    /// </summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("debug")]
    [InlineData("info")]
    public async Task A_bare_integer_never_addresses_the_internal_id(string verb)
    {
        var (exit, _, error, jobs) = await RunArgsAsync(verb, "42", "--ns", "shop");

        Assert.Equal(2, exit);
        Assert.Contains("job not found", error);
        Assert.Equal(JobLookup.ByDeduplicationKey("shop", "42"), Assert.Single(jobs.Lookups));
        Assert.Empty(jobs.Controls);
    }

    [Fact]
    public async Task A_bare_integer_with_several_namespaces_needs_ns()
    {
        var (exit, _, error, jobs) = await RunArgsAsync("cancel", "42");

        Assert.Equal(64, exit);
        Assert.Contains("--ns is required", error);
        Assert.Empty(jobs.Lookups);
    }

    // Parsed from the operator's own spelling, against a host that registers two namespaces.
    private static async Task<(int Exit, string Output, string Error, NothingJobs Jobs)> RunArgsAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(jobs, jobs, [], ["shop", "billing"], output, error, () => null);

        Assert.True(CliCommandParser.TryParse(args, out var command, out var parseError), parseError);
        var exit = await runner.RunAsync(command, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString(), jobs);
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
            new CliCommand(
                CliVerb.Signal,
                Target: NothingJobs.KnownDeduplicationKey,
                SignalName: "approval",
                SignalValue: null,
                null,
                null,
                Json: false
            ),
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
            new CliCommand(
                CliVerb.Signal,
                Target: NothingJobs.KnownDeduplicationKey,
                SignalName: "approval",
                SignalValue: json,
                null,
                null,
                Json: false
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, exit);
        var (Name, FormatId, Value) = Assert.Single(jobs.Signals);
        Assert.Equal((byte)1, FormatId);
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(Value!));
    }

    [Fact]
    public async Task Signal_with_a_value_that_is_not_json_is_a_usage_error_and_raises_nothing()
    {
        var jobs = new NothingJobs();
        var error = new StringWriter();
        var runner = new CliCommandRunner(jobs, jobs, [], ["shop"], new StringWriter(), error, () => null);

        var exit = await runner.RunAsync(
            new CliCommand(
                CliVerb.Signal,
                Target: NothingJobs.KnownDeduplicationKey,
                SignalName: "approval",
                SignalValue: "approved",
                null,
                null,
                Json: false
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(64, exit);
        Assert.StartsWith("--value:", error.ToString());
        Assert.Empty(jobs.Signals);
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

        Assert.True(CliCommandParser.TryParse([verb, NothingJobs.KnownDeduplicationKey], out var command, out var parseError), parseError);
        var exit = await runner.RunAsync(command, TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class NothingJobs : IJobs, IActaOperations, ILedger
    {
        /// <summary>The one job this fake resolves, by this id, its ref, or its key in shop; every other target reads as absent.</summary>
        public const long KnownJobId = 42;

        /// <summary>The known job's deduplication key, an integer that is not its id.</summary>
        public const string KnownDeduplicationKey = "4711";

        /// <summary>Every lookup the runner resolved its target with.</summary>
        public List<JobLookup> Lookups { get; } = [];

        /// <summary>The jobs a cancel was applied to.</summary>
        public List<JobLookup> Controls { get; } = [];

        /// <summary>
        /// Simulates the purge race the read verbs have to survive: the up-front resolve still returns
        /// the snapshot, but every later read finds the row gone. Without it, "no result" / "no events"
        /// and "no job" are indistinguishable.
        /// </summary>
        public bool JobVanishedAfterResolve { get; set; }

        private static readonly JobRef KnownJobRef = new(new Guid("019826f0-0000-7000-8000-00000000002a"));

        private static bool Resolves(JobLookup job) =>
            job.Kind switch
            {
                JobLookupKind.JobId => job.JobId == KnownJobId,
                JobLookupKind.JobRef => job.JobRef == KnownJobRef,
                JobLookupKind.DeduplicationKey => job is { JobNamespace: "shop", DeduplicationKey: KnownDeduplicationKey },
                _ => false,
            };

        public ValueTask<GetJobResponse?> GetAsync(JobLookup job, CancellationToken ct = default)
        {
            Lookups.Add(job);
            return ValueTask.FromResult(
                Resolves(job)
                    ? new GetJobResponse(
                        JobId: KnownJobId,
                        JobRef: KnownJobRef,
                        Kind: JobKindCode.Ordinary,
                        JobNamespace: "shop",
                        DefinitionId: 1,
                        JobName: "send-email",
                        LineageRootJobId: null,
                        LineageRootJobRef: null,
                        ParentJobId: null,
                        ParentJobRef: null,
                        TenantId: null,
                        TenantName: null,
                        DeduplicationKey: KnownDeduplicationKey,
                        CorrelationKey: null,
                        ConcurrencyKey: null,
                        InputFormatId: 0,
                        Status: JobStatusCode.Ready,
                        Priority: JobPriorityCode.Normal,
                        Lane: null,
                        BlockedBehindJobId: null,
                        BlockedBehindJobRef: null,
                        NextRunAtUtc: null,
                        ExecutionNumber: 0,
                        FailureCount: 0,
                        LeasedByWorkerId: null,
                        LeaseExpiresAtUtc: null,
                        RetentionUntilUtc: null,
                        CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
                        ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
                        LeasedByWorkerRef: null,
                        Version: 3
                    )
                    : null
            );
        }

        public ValueTask<ExplainJobResponse?> ExplainAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult<ExplainJobResponse?>(null);

        public ValueTask<GetJobLineageMapResponse?> GetLineageMapAsync(
            JobLookup job,
            GetJobLineageMapOptions? options = null,
            CancellationToken ct = default
        ) => ValueTask.FromResult<GetJobLineageMapResponse?>(null);

        public ValueTask<EnqueueJobResponse> EnqueueAsync(EnqueueJobRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ISchedules Schedules => throw new NotSupportedException();
        public ICalendars Calendars => throw new NotSupportedException();
        public IDefinitions Definitions => throw new NotSupportedException();
        public IWorkers Workers => throw new NotSupportedException();
        public IAlerts Alerts => throw new NotSupportedException();
        public ITenants Tenants => throw new NotSupportedException();
        public INamespaces Namespaces => throw new NotSupportedException();
        public ITags Tags => throw new NotSupportedException();
        public IOutbox Outbox => throw new NotSupportedException();
        public ILedger Ledger => this;
        public DbProvider Provider => DbProvider.Sqlite;

        public ValueTask<ListJobsResponse> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        /// <summary>The job carries no retained events; whether it still exists is the status read.</summary>
        public ValueTask<ListEventsResponse> ListEventsAsync(ListEventsQuery query, CancellationToken ct = default) =>
            ValueTask.FromResult(new ListEventsResponse([], null, false, 50, null));

        public ValueTask<GetOverviewResponse> GetOverviewAsync(GetOverviewQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobHandle> EnqueueAsync<TInput>(
            TInput input,
            Action<JobEnqueueOptionsBuilder>? configure = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(
            JobCall<TInput> job,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobHandle> EnqueueAsync<TInput>(
            JobCall<TInput> job,
            Action<JobEnqueueOptionsBuilder>? configure = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobHandle<TResult>> EnqueueAsync<TInput, TResult>(
            JobCall<TInput, TResult> job,
            Action<JobEnqueueOptionsBuilder>? configure = null,
            CancellationToken ct = default
        )
            where TInput : notnull
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
            System.Data.Common.DbTransaction transaction,
            JobCall<TInput> job,
            Action<JobEnqueueOptionsBuilder>? configure = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
            System.Data.Common.DbTransaction transaction,
            JobCall<TInput, TResult> job,
            Action<JobEnqueueOptionsBuilder>? configure = null,
            CancellationToken ct = default
        )
            where TInput : notnull
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome> WaitAsync(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobOutcome<TResult>> WaitAsync<TResult>(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default)
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
            JobCall<TInput, TResult> job,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<EnqueueJobsResponse> EnqueueJobsAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<EnqueueJobResponse> EnqueueAsync(
            System.Data.Common.DbTransaction transaction,
            EnqueueJobRequest request,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
            System.Data.Common.DbTransaction transaction,
            TInput input,
            Action<JobEnqueueOptionsBuilder>? configure = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<EnqueueJobsResponse> EnqueueJobsAsync(
            System.Data.Common.DbTransaction transaction,
            IReadOnlyList<EnqueueJobRequest> requests,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<GetJobIdResponse?> GetIdAsync(JobLookup job, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<GetJobStatusResponse?> GetStatusAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult<GetJobStatusResponse?>(
                JobVanishedAfterResolve || job.Kind != JobLookupKind.JobId || job.JobId != KnownJobId
                    ? null
                    : new GetJobStatusResponse(JobStatusCode.Ready)
            );

        public ValueTask<GetJobInputResponse?> GetInputAsync(JobLookup job, CancellationToken ct = default) =>
            throw new NotSupportedException();

        /// <summary>The job produced no result; whether it still exists is the status read.</summary>
        public ValueTask<GetJobResultResponse?> GetResultAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult<GetJobResultResponse?>(null);

        public ValueTask<ListJobCheckpointsResponse> ListCheckpointsAsync(ListJobCheckpointsQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<ListJobStepsResponse> ListStepsAsync(ListJobStepsQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<CancelJobResponse> CancelAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actor = null,
            int? expectedVersion = null,
            CancellationToken ct = default
        )
        {
            Controls.Add(job);
            return ValueTask.FromResult(new CancelJobResponse(KnownJobId, ControlAction.Applied, JobStatusCode.Cancelled, 4));
        }

        public ValueTask<PauseJobResponse> PauseAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actor = null,
            int? expectedVersion = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<ResumeJobResponse> ResumeAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actor = null,
            int? expectedVersion = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<RestartJobResponse> RestartAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actor = null,
            int? expectedVersion = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<RescheduleJobResponse> RescheduleAsync(
            JobLookup job,
            DateTime? nextRunAtUtc = null,
            string? reasonMessage = null,
            string? actor = null,
            int? expectedVersion = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<ReprioritizeJobResponse> ReprioritizeAsync(
            JobLookup job,
            JobPriorityCode priority,
            string? reasonMessage = null,
            string? actor = null,
            int? expectedVersion = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<UpdateJobInputResponse> UpdateInputAsync(
            JobLookup job,
            JobPayload input,
            string? reasonMessage = null,
            string? actor = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<PurgeJobResponse> PurgeAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actor = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<RaiseJobSignalResponse> RaiseSignalAsync<T>(
            JobLookup job,
            string name,
            T value,
            string? actor = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        /// <summary>Recorded signal raises with the delivered payload format and bytes (null when presence-only).</summary>
        public List<(string Name, byte FormatId, byte[]? Value)> Signals { get; } = [];

        public ValueTask<RaiseJobSignalResponse> RaiseSignalAsync(
            JobLookup job,
            string name,
            JobPayload value,
            string? actor = null,
            CancellationToken ct = default
        )
        {
            Signals.Add((name, value.Format.Id, value.IsNone ? null : value.Data.ToArray()));
            return ValueTask.FromResult(new RaiseJobSignalResponse(1, ControlAction.Applied, JobStatusCode.Ready));
        }
    }
}
