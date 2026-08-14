using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schema;

[ConformanceSpec(
    "schema.operator-views",
    "Schema bootstrap installs curated operator views",
    Area = "Schema",
    Contract = "Schema bootstrap installs curated plural _view surfaces while jobs_view decodes status plus tenant key and tags_view decodes exact target scope.",
    Arrange = "A provider schema is bootstrapped, a retry-probe job is driven to terminal Failed, and one job is enqueued for a registered tenant.",
    Act = "The provider catalog is queried for views, every view is smoke-queried, and jobs_view is filtered by status = 'failed' and by job id.",
    Assert = "Only curated views exist, all are queryable, jobs decode failed status and resolve tenant keys, and tags decode job scope beside raw codes."
)]
public abstract partial class OperatorViewSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private static readonly string[] OperatorViews =
    [
        "alerts_view",
        "checkpoints_view",
        "definitions_view",
        "events_view",
        "jobs_view",
        "schedules_view",
        "steps_view",
        "tags_view",
        "workers_view",
    ];

    [Fact(DisplayName = "Schema install creates exactly the curated operator views")]
    public async Task Schema_install_creates_exactly_curated_operator_views()
    {
        var expected = OperatorViews.OrderBy(static n => n, StringComparer.Ordinal).ToList();
        var actual = (await Fixture.ListViewsAsync(Schema.SchemaName)).OrderBy(static n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "Every curated operator view can be queried")]
    public async Task Every_curated_operator_view_can_be_queried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await Db.GetConnectionAsync(ct);

        foreach (var view in OperatorViews)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {Db.Schema}.{view} WHERE 1 = 0";
            await using var _ = await cmd.ExecuteReaderAsync(ct);
        }
    }

    [Fact(DisplayName = "Every literal Engineering Lab SELECT compiles against this provider")]
    public async Task Every_literal_engineering_lab_select_compiles_against_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = await Db.GetConnectionAsync(ct);
        var failures = new List<string>();

        foreach (var (relativePath, literalSql) in EngineeringLabQueries())
        {
            var sql = AdaptConceptLabSql(literalSql);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                AddValidationParameters(cmd, sql);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) { }
            }
            catch (Exception ex)
            {
                failures.Add($"{relativePath}: {ex.Message}\n{sql}");
            }
        }

        Assert.True(failures.Count == 0, $"Engineering Lab SQL drift for {Db.Provider}:\n\n" + string.Join("\n\n", failures));
    }

    [Fact(DisplayName = "jobs_view supports friendly failed-status filtering with raw status_code beside it")]
    public async Task Jobs_view_filters_by_failed_status_name()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        await Runtime.RunOnceAsync(enqueued, ct);
        await Runtime.RunOnceAsync(enqueued, ct);
        await Runtime.RunOnceAsync(enqueued, ct);

        await using var conn = await Db.GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT status, status_code FROM {Db.Schema}.jobs_view WHERE status = 'failed' AND job_id = @p_job_id";
        Add(cmd, "@p_job_id", enqueued.JobId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("failed", reader.GetString(0));
        Assert.Equal((int)JobStatusCode.Failed, Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
        Assert.False(await reader.ReadAsync(ct));
    }

    [Fact(DisplayName = "jobs_view resolves the tenant key beside the raw tenant id")]
    public async Task Jobs_view_resolves_the_tenant_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantKey = TestKey("view-tenant");
        var tenantId = await Operations.Tenants.RegisterAsync(tenantKey, null, null, ct);
        var scoped = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None, TenantKey: tenantKey),
            ct
        );
        var unscoped = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        await using var conn = await Db.GetConnectionAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT tenant_id, tenant_key FROM {Db.Schema}.jobs_view WHERE job_id = @p_job_id";
            Add(cmd, "@p_job_id", scoped.JobId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(tenantId, Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            Assert.Equal(tenantKey, reader.GetString(1));
        }

        // An untenanted job keeps both columns NULL: the join is outer, so the row still projects.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT tenant_id, tenant_key FROM {Db.Schema}.jobs_view WHERE job_id = @p_job_id";
            Add(cmd, "@p_job_id", unscoped.JobId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.True(await reader.IsDBNullAsync(0, ct));
            Assert.True(await reader.IsDBNullAsync(1, ct));
        }
    }

    [Fact(DisplayName = "tags_view decodes job scope beside exact target and tag values")]
    public async Task Tags_view_decodes_exact_job_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        await Operations.Tags.UpsertAsync(TagTarget.ForJob(enqueued), new TagInput("operator-view", "visible"), ct: ct);

        await using var conn = await Db.GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT scope, scope_code, scope_id, namespace, tag_name, tag_value FROM {Db.Schema}.tags_view WHERE scope_id = @p_scope_id AND scope_code = @p_scope_code";
        Add(cmd, "@p_scope_id", enqueued.JobId);
        Add(cmd, "@p_scope_code", (byte)TagScopeCode.Job);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("job", reader.GetString(0));
        Assert.Equal((int)TagScopeCode.Job, Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
        Assert.Equal(enqueued.JobId, Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));
        Assert.Equal(TestNamespace, reader.GetString(3));
        Assert.Equal("operator-view", reader.GetString(4));
        Assert.Equal("visible", reader.GetString(5));
        Assert.False(await reader.ReadAsync(ct));
    }

    [Fact(DisplayName = "events_view and checkpoints_view expose displayable payload text")]
    public async Task Payload_views_expose_displayable_payload_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        await using var conn = await Db.GetConnectionAsync(ct);
        await InsertCheckpointPayload(conn, enqueued.JobId, "operator-view.payload", "checkpoint text", ct);
        await InsertEventDetail(conn, enqueued.JobId, "event text", ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT value_format, value_text FROM {Db.Schema}.checkpoints_view WHERE job_id = @p_job_id AND checkpoint_name = @p_name";
            Add(cmd, "@p_job_id", enqueued.JobId);
            Add(cmd, "@p_name", "operator-view.payload");

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal("text", reader.GetString(0));
            Assert.Equal("checkpoint text", reader.GetString(1));
            Assert.False(await reader.ReadAsync(ct));
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT detail_format, detail_text FROM {Db.Schema}.events_view WHERE job_id = @p_job_id AND event = 'job.state-reset' ORDER BY event_id DESC";
            Add(cmd, "@p_job_id", enqueued.JobId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal("text", reader.GetString(0));
            Assert.Equal("event text", reader.GetString(1));
        }
    }

    private async Task InsertCheckpointPayload(DbConnection conn, long jobId, string name, string value, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {Db.Schema}.checkpoints (job_id, kind_code, name, value_format_id, value) VALUES (@p_job_id, @p_kind_code, @p_name, @p_value_format_id, @p_value)";
        Add(cmd, "@p_job_id", jobId);
        Add(cmd, "@p_kind_code", (byte)JobCheckpointKindCode.Variable);
        Add(cmd, "@p_name", name);
        Add(cmd, "@p_value_format_id", JobPayloadFormat.Text.Id);
        Add(cmd, "@p_value", Encoding.UTF8.GetBytes(value));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertEventDetail(DbConnection conn, long jobId, string detail, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Db.Schema}.events (
                namespace_id, event_code, actor_code, job_id, job_ref, detail_format_id, detail)
            SELECT namespace_id, @p_event_code, @p_actor_code, id, job_ref, @p_detail_format_id, @p_detail
              FROM {Db.Schema}.jobs
             WHERE id = @p_job_id
            """;
        Add(cmd, "@p_event_code", (short)EventCode.JobStateReset);
        Add(cmd, "@p_actor_code", (byte)ActorCode.Operator);
        Add(cmd, "@p_detail_format_id", JobPayloadFormat.Text.Id);
        Add(cmd, "@p_detail", Encoding.UTF8.GetBytes(detail));
        Add(cmd, "@p_job_id", jobId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static void AddValidationParameters(DbCommand command, string sql)
    {
        foreach (var name in MyRegex().Matches(sql).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = IsJobIdParameter(name) ? 0L : "__concept_sql_validation__";
            command.Parameters.Add(parameter);
        }

        static bool IsJobIdParameter(string name) =>
            name.EndsWith("JobId", StringComparison.OrdinalIgnoreCase)
            || name is "@successful" or "@failed" or "@retrying" or "@waiting" or "@parent";
    }

    private string AdaptConceptLabSql(string sql)
    {
        sql = Regex.Replace(
            sql,
            @"\{\{bytes:(?<expression>[A-Za-z_][A-Za-z0-9_.]*)\}\}",
            match =>
                Db.Provider switch
                {
                    DbProvider.Sqlite => $"length({match.Groups["expression"].Value})",
                    DbProvider.SqlServer => $"DATALENGTH({match.Groups["expression"].Value})",
                    _ => $"octet_length({match.Groups["expression"].Value})",
                },
            RegexOptions.CultureInvariant
        );

        if (Db.Provider == DbProvider.Sqlite)
        {
            return sql.Replace("{{schema}}.", "", StringComparison.Ordinal).Replace("{{schema}}", "main", StringComparison.Ordinal);
        }

        var schema = Db.Provider == DbProvider.SqlServer ? $"[{Db.Schema}]" : $"\"{Db.Schema}\"";
        sql = sql.Replace("{{schema}}.", schema + ".", StringComparison.Ordinal).Replace("{{schema}}", schema, StringComparison.Ordinal);
        foreach (var view in OperatorViews)
        {
            var qualifiedView = Db.Provider == DbProvider.SqlServer ? $"{schema}.[{view}]" : $"{schema}.\"{view}\"";
            sql = Regex.Replace(
                sql,
                $@"(?<![\w.]){Regex.Escape(view)}(?!\w)",
                qualifiedView,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
        }
        return sql;
    }

    private static IReadOnlyList<(string RelativePath, string Sql)> EngineeringLabQueries()
    {
        var repoRoot = ResolveRepoRoot();
        var concepts = Path.Combine(repoRoot, "concepts");
        var queries = new List<(string RelativePath, string Sql)>();
        foreach (var readme in Directory.EnumerateFiles(concepts, "README.md", SearchOption.AllDirectories))
        {
            if (!File.ReadAllText(readme).Contains("<!-- engineering-lab", StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(readme)!;
            foreach (var sourcePath in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(sourcePath);
                foreach (
                    Match match in Regex.Matches(
                        source,
                        "\\\"\\\"\\\"(?<sql>\\s*SELECT\\b.*?)\\\"\\\"\\\"",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
                    )
                )
                {
                    queries.Add((Path.GetRelativePath(repoRoot, sourcePath).Replace('\\', '/'), match.Groups["sql"].Value.Trim()));
                }
            }
        }
        return queries;
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Acta.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"Could not locate Acta.slnx from {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
