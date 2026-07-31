using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Anchors the JobRef storage rule: job_ref is the source of truth on the jobs table (a uniquely
/// indexed uuid with no database default, allocated in C# and passed into the inserting routine)
/// and is denormalized onto the audit tables (events, alerts) so the public ref survives job
/// purge; CASCADE child/substrate tables reference jobs by numeric id only.
/// </summary>
public sealed class JobRefSchemaShapeTests
{
    [Fact]
    public void Job_ref_lives_on_the_job_and_audit_tables_only()
    {
        var carriers = ActaSchema
            .Entities.Where(e => e.Columns.Any(c => c.Name == "job_ref"))
            .Select(e => e.TableName)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal(["alerts", "events", "jobs"], carriers);
    }

    [Fact]
    public void Job_ref_is_a_client_allocated_unique_uuid()
    {
        var job = ActaSchema.Entities.Single(e => e.TableName == "jobs");
        var column = job.Columns.Single(c => c.Name == "job_ref");

        Assert.Equal(DbKind.Guid, column.Kind);
        Assert.False(column.IsNullable);
        Assert.Equal(DbDefault.None, column.Default);

        var index = job.Indexes.Single(i => i.Name == "ux_jobs_ref");
        Assert.True(index.IsUnique);
        Assert.Equal(["job_ref"], index.Columns);
    }
}
