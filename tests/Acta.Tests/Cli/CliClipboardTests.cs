using Acta.Cli;
using Acta.Configuration;
using Acta.Payloads;
using Acta.Querying;
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
/// a usage error when the clipboard yields nothing usable. The fake IJobs resolves nothing, so a
/// not-found exit proves the clipboard value reached the lookup.
/// </summary>
public class CliRunnerClipboardTests
{
    private static async Task<(int ExitCode, string Error)> RunInfoAsync(Func<string?> clipboard)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliCommandRunner(new NothingJobs(), [], ["shop"], output, error, clipboard);

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
        var runner = new CliCommandRunner(
            new NothingJobs(),
            [],
            ["shop"],
            output,
            error,
            () => throw new InvalidOperationException("clipboard must not be read")
        );

        var exitCode = await runner.RunAsync(
            new CliCommand(CliVerb.Info, Target: "42", null, null, null, null, Json: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Signal_without_value_raises_presence_only()
    {
        var jobs = new NothingJobs();
        var runner = new CliCommandRunner(jobs, [], ["shop"], new StringWriter(), new StringWriter(), () => null);

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
        var runner = new CliCommandRunner(jobs, [], ["shop"], new StringWriter(), new StringWriter(), () => null);
        const string json = "{\"approved\":true}";

        var exit = await runner.RunAsync(
            new CliCommand(CliVerb.Signal, Target: "42", SignalName: "approval", SignalValue: json, null, null, Json: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, exit);
        var call = Assert.Single(jobs.Signals);
        Assert.Equal((byte)1, call.FormatId);
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(call.Value!));
    }

    private sealed class NothingJobs : IJobs
    {
        public ValueTask<JobSnapshot?> GetAsync(JobLookup lookup, CancellationToken ct = default) =>
            ValueTask.FromResult<JobSnapshot?>(null);

        public ValueTask<JobExplanation?> ExplainAsync(JobLookup lookup, CancellationToken ct = default) =>
            ValueTask.FromResult<JobExplanation?>(null);

        public ValueTask<JobLineageMap?> GetLineageMapAsync(
            JobLookup lookup,
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
        public DbProvider Provider => DbProvider.Sqlite;

        public ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<PagedResult<JobEventListItem>> ListJobEventsAsync(ListJobEventsQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<PagedResult<string>> ListNamespacesAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome> ExecuteAndWaitAsync<TInput>(
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome<TResult>> ExecuteAndWaitAsync<TInput, TResult>(
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

        public ValueTask<JobOutcome<TResult>> ExecuteAndWaitAsync<TInput, TResult>(
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

        public ValueTask<long?> ResolveJobIdAsync(JobLookup lookup, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<JobStatusCode?> GetStatusAsync(JobLookup lookup, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobPayload?> GetResultAsync(JobLookup lookup, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<TResult?> GetResultAsync<TResult>(JobLookup lookup, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobControlResult> CancelAsync(
            JobLookup lookup,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> PauseAsync(
            JobLookup lookup,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> ResumeAsync(
            JobLookup lookup,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> RestartAsync(
            JobLookup lookup,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> RescheduleAsync(
            JobLookup lookup,
            DateTime nextRunAtUtc,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> ReprioritizeAsync(
            JobLookup lookup,
            JobPriorityCode priority,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> PurgeAsync(JobLookup lookup, string? actorKey = null, CancellationToken ct = default) =>
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
            return ValueTask.FromResult(new JobControlResult(1, JobControlAction.Applied, JobStatusCode.Ready));
        }
    }
}
