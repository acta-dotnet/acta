using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Jobs;

/// <summary>
/// The published SQL door, <c>enqueue_job</c>, called as the operator guide's recipe calls it: every optional parameter
/// stores what the API field of its name reads back, and the routine stores a key as given, so the engine and the reads
/// take a stored key as stored.
/// </summary>
[Deploys("add-numbers", "concurrency-limit-probe")]
public sealed class SqlServerDirectSqlEnqueueTests
    : ActaRuntimeTestBase<SqlServerConformanceFixture, JobsOf<SqlServerDirectSqlEnqueueTests>>
{
    private const string Input = "@p_input = 0x7B226C656674223A312C227269676874223A327D";

    [Fact(DisplayName = "Documented direct SQL enqueue defaults produce a visible and executable job")]
    public async Task Sql_enqueue_is_visible_and_claimable_without_an_application_wake()
    {
        var ct = TestContext.Current.CancellationToken;

        var (jobId, jobRef, action) = await EnqueueAsync(Input, ct);

        Assert.Equal(1, action);
        var read = await ReadAsync(jobId, ct);
        Assert.Equal((jobRef, (byte)1, JobPriorityCode.Normal), (read.JobRef.Value, read.InputFormatId, read.Priority));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, jobId, ct));
    }

    [Fact(DisplayName = "A job enqueued from SQL with keys the API refuses is claimed, run, listed and read")]
    public async Task Keys_the_api_refuses_are_taken_as_stored()
    {
        var ct = TestContext.Current.CancellationToken;

        // No key the API takes holds a space; the routine stores what it is given, and the definition limits concurrency,
        // so the run takes a slot under the stored key.
        var (jobId, _, _) = await EnqueueAsync(
            "@p_deduplication_key = 'order 42', @p_correlation_key = 'trace 7', @p_concurrency_key = 'tenant a'",
            ct,
            "concurrency-limit-probe"
        );

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, jobId, ct));
        var read = await ReadAsync(jobId, ct);
        Assert.Equal(("order 42", "trace 7", "tenant a"), (read.DeduplicationKey, read.CorrelationKey, read.ConcurrencyKey));
        var listed = (await Operations.Ledger.ListJobsAsync(new ListJobsQuery(TestNamespace, PageSize: 100), ct)).Items;
        Assert.Equal("order 42", Assert.Single(listed, j => j.JobId == jobId).DeduplicationKey);
    }

    [Fact(DisplayName = "A priority that is not a band is refused")]
    public async Task A_priority_outside_the_bands_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        // The claim reads band by band, so a job stored between two bands would never be claimed.
        var refused = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            EnqueueAsync("@p_job_priority_code_override = 60", ct)
        );

        Assert.Contains("not a priority band", refused.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "@p_job_ref is the ref the job carries")]
    public async Task A_given_ref_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var given = Guid.NewGuid();

        var (jobId, jobRef, _) = await EnqueueAsync($"@p_job_ref = '{given}'", ct);

        Assert.Equal((given, given), (jobRef, (await ReadAsync(jobId, ct)).JobRef.Value));
    }

    [Fact(DisplayName = "@p_deduplication_key answers the first job to a second call")]
    public async Task A_repeated_key_answers_the_first_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("sql-key");

        var first = await EnqueueAsync($"@p_deduplication_key = '{key}'", ct);
        var second = await EnqueueAsync($"@p_deduplication_key = '{key}'", ct);

        Assert.Equal((first.JobId, first.JobRef, 2), second);
    }

    [Fact(DisplayName = "@p_job_priority_code_override, @p_correlation_key and @p_lane_name are read back by the API")]
    public async Task Priority_correlation_and_lane_are_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var (lane, trace) = (Unique("sql-lane"), Unique("sql-trace"));

        var (jobId, _, _) = await EnqueueAsync(
            $"@p_job_priority_code_override = 70, @p_correlation_key = '{trace}', @p_lane_name = '{lane}'",
            ct
        );

        var read = await ReadAsync(jobId, ct);
        Assert.Equal((JobPriorityCode.High, trace, lane), (read.Priority, read.CorrelationKey, read.Lane));
    }

    [Theory(DisplayName = "@p_next_run_at_utc and @p_delay_seconds hold the job until then")]
    [InlineData("DECLARE @at DATETIME2(3) = DATEADD(HOUR, 1, SYSUTCDATETIME());", "@p_next_run_at_utc = @at")]
    [InlineData("", "@p_delay_seconds = 3600")]
    public async Task A_future_run_is_stored(string declarations, string argument)
    {
        var ct = TestContext.Current.CancellationToken;

        var (jobId, _, _) = await EnqueueAsync(argument, ct, declarations: declarations);

        var read = await ReadAsync(jobId, ct);
        Assert.True(read.NextRunAtUtc > read.CreatedAtUtc.AddMinutes(59), $"next run {read.NextRunAtUtc:O} is not an hour out");
    }

    [Theory(DisplayName = "@p_input_format_id is stored, and without it the input's presence decides the format")]
    [InlineData("", 0)]
    [InlineData(Input, 1)]
    [InlineData("@p_input_format_id = 2, " + Input, 2)]
    public async Task The_input_format_is_stored(string arguments, byte format)
    {
        var ct = TestContext.Current.CancellationToken;

        var (jobId, _, _) = await EnqueueAsync(arguments, ct);

        Assert.Equal(format, (await ReadAsync(jobId, ct)).InputFormatId);
    }

    [Fact(DisplayName = "@p_tags stores a tag the API reads and filters by")]
    public async Task Tags_are_stored()
    {
        var ct = TestContext.Current.CancellationToken;

        var (jobId, _, _) = await EnqueueAsync(
            "",
            ct,
            declarations: "INSERT INTO @tags (ordinal, tag_name, tag_value, tag_value_search) VALUES (0, 'region', N'EU-West', N'EU-WEST');"
        );

        var tags = (await Operations.Tags.ListAsync(new ListTagsQuery(TagTarget.ForJob(JobLookup.ById(jobId))), ct)).Items;
        Assert.Equal(new ListTagsItem("region", "EU-West"), Assert.Single(tags));
        // The search value is the value trimmed and upper-cased, as the API writes it, so the API's filter finds the job.
        var filter = new ListJobsQuery(TestNamespace, PageSize: 100, Tags: [new TagFilter("region", "eu-west")]);
        Assert.Contains((await Operations.Ledger.ListJobsAsync(filter, ct)).Items, j => j.JobId == jobId);
    }

    [Theory(DisplayName = "A key that is empty or only spaces is refused, since every later enqueue would share it")]
    [InlineData("@p_deduplication_key", "")]
    [InlineData("@p_correlation_key", "")]
    [InlineData("@p_concurrency_key", "")]
    [InlineData("@p_deduplication_key", "  ")]
    [InlineData("@p_correlation_key", "  ")]
    [InlineData("@p_concurrency_key", "  ")]
    public async Task A_blank_key_is_refused(string parameter, string key)
    {
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => EnqueueAsync($"{parameter} = '{key}'", ct));

        Assert.Contains("a key is blank", refused.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "@p_parent_job_id, @p_tenant_name and @p_tenant_override place a child under its parent and tenant")]
    public async Task Parent_and_tenant_are_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parentTenant, childTenant) = (Unique("sql-tenant-a"), Unique("sql-tenant-b"));
        await Operations.Tenants.RegisterAsync(parentTenant, ct: ct);
        await Operations.Tenants.RegisterAsync(childTenant, ct: ct);
        var parent = await EnqueueAsync($"@p_tenant_name = '{parentTenant}'", ct);

        // A child inherits its parent's tenant and may name another only with the override.
        var inherited = await EnqueueAsync($"@p_parent_job_id = {parent.JobId}", ct);
        var refused = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            EnqueueAsync($"@p_parent_job_id = {parent.JobId}, @p_tenant_name = '{childTenant}'", ct)
        );
        var moved = await EnqueueAsync($"@p_parent_job_id = {parent.JobId}, @p_tenant_name = '{childTenant}', @p_tenant_override = 1", ct);

        Assert.Contains("ENQ_TENANT_MISMATCH", refused.Message, StringComparison.Ordinal);
        var (first, second) = (await ReadAsync(inherited.JobId, ct), await ReadAsync(moved.JobId, ct));
        Assert.Equal((parent.JobId, parentTenant), (first.ParentJobId, first.TenantName));
        Assert.Equal((parent.JobId, childTenant), (second.ParentJobId, second.TenantName));
    }

    private async Task<GetJobResponse> ReadAsync(long jobId, CancellationToken ct) =>
        await Jobs.GetAsync(JobLookup.ById(jobId), ct) ?? throw new InvalidOperationException($"job {jobId} is not readable");

    // The recipe's call with more named arguments, and the row the routine answers. The tag table has no default, so the
    // recipe declares an empty one; a fact that tags fills it in its declarations.
    private async Task<(long JobId, Guid JobRef, int Action)> EnqueueAsync(
        string arguments,
        CancellationToken ct,
        string jobName = "add-numbers",
        string declarations = ""
    )
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(Schema.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DECLARE @tags {Schema.SchemaName}.enqueue_tags;
            {declarations}
            EXEC {Schema.SchemaName}.enqueue_job
                @p_namespace_name = @namespace,
                @p_job_name = @job,
                @p_tags = @tags{(arguments.Length == 0 ? "" : ", " + arguments)};
            """;
        command.Parameters.Add(
            new Microsoft.Data.SqlClient.SqlParameter("@namespace", System.Data.SqlDbType.VarChar, 128) { Value = TestNamespace }
        );
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@job", System.Data.SqlDbType.VarChar, 128) { Value = jobName });
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(0, reader.GetInt32(0));
        var row = (
            reader.GetInt64(1),
            reader.GetGuid(2),
            Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)
        );
        Assert.False(await reader.ReadAsync(ct));
        return row;
    }
}
