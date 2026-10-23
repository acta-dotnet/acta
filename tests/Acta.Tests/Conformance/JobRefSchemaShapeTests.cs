using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Anchors the JobRef storage rule: job_ref is the source of truth on the jobs table (a uniquely
/// indexed uuid with no database default, allocated in C# and passed into the inserting routine)
/// and is denormalized onto alerts so an incident keeps the public ref past a job purge; events name
/// their subject by numeric id, and an explicit purge's tombstone snapshots the ref in its details.
/// </summary>
public sealed class JobRefSchemaShapeTests
{
    [Fact]
    public void Job_ref_lives_on_the_job_and_alert_tables_only()
    {
        var carriers = ActaSchema
            .Entities.Where(e => e.Columns.Any(c => c.Name == "job_ref"))
            .Select(e => e.TableName)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal(["alerts", "jobs"], carriers);
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
