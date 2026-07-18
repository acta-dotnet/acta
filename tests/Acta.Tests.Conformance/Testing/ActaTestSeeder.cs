using Acta.Relational.Entities;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Semantic seeding helper over the test ORM. Provides default test values and reads back DB-assigned
/// ids; tests call seeder methods rather than building raw SQL. Writes go through the same
/// <see cref="IDbSession"/> seam as reads.
/// </summary>
internal sealed class ActaTestSeeder(IDbSession db)
{
    /// <summary>
    /// Insert a <c>namespaces</c> row and return the DB-assigned id. Defaults
    /// <paramref name="ownerTeam"/> to <c>"test"</c> when omitted; stamps the audit timestamps (these
    /// columns have no server default).
    /// </summary>
    public async Task<short> SeedJobNamespaceAsync(string name, string? ownerTeam = "test", CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.From<JobNamespace>()
            .InsertAsync<short>(
                new JobNamespace
                {
                    Name = name,
                    OwnerTeam = ownerTeam,
                    Status = JobNamespaceStatusCode.Active,
                    CreatedAtUtc = now,
                    ModifiedAtUtc = now,
                },
                ct
            );
    }

    /// <summary>
    /// Insert a minimal <c>definitions</c> row and return the DB-assigned id. Every code-owned policy
    /// column is set to a schema-valid value so the definition integrity checks pass.
    /// </summary>
    public async Task<int> SeedJobDefinitionAsync(short namespaceId, string name = "test-def", CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.From<JobDefinition>()
            .InsertAsync<int>(
                new JobDefinition
                {
                    NamespaceId = namespaceId,
                    Name = name,
                    Status = JobDefinitionStatusCode.Active,
                    DefinitionHash = "test-hash",
                    ManifestGenerationAtUtc = now,
                    InputTypeName = "TestInput",
                    InputFormatId = 0,
                    InputFormatName = "json",
                    OutputFormatId = 0,
                    OutputFormatName = "json",
                    Priority = JobPriorityCode.Normal,
                    MaxAttempts = 1,
                    Backoff = "1m..8h",
                    ExecutionTimeoutSeconds = 30,
                    DeadlineSeconds = 0,
                    DeadlineBehavior = DeadlineBehaviorCode.Advisory,
                    JobRetentionSeconds = 3600,
                    AuditLevel = JobAuditLevelCode.Off,
                    AlertProfile = JobAlertProfileCode.None,
                    CreatedAtUtc = now,
                    ModifiedAtUtc = now,
                },
                ct
            );
    }

    /// <summary>
    /// Insert a <c>jobs</c> row (satisfying the namespace/definition FKs, and the tenant FK when
    /// <paramref name="tenantId"/> is supplied) and return its DB-assigned id plus the generated public
    /// <c>job_ref</c>. Seeds a definition first when one is not supplied.
    /// </summary>
    public async Task<(long JobId, Guid JobRef)> SeedJobAsync(
        short namespaceId,
        int? definitionId = null,
        int? tenantId = null,
        CancellationToken ct = default
    )
    {
        var defId = definitionId ?? await SeedJobDefinitionAsync(namespaceId, ct: ct);
        var jobRef = Guid.NewGuid();
        var jobId = await db.From<Job>()
            .InsertAsync<long>(
                new Job
                {
                    JobRef = jobRef,
                    NamespaceId = namespaceId,
                    DefinitionId = defId,
                    TenantId = tenantId,
                    // Non-zero format + non-null input satisfies ck_jobs_input_pair (and avoids a
                    // null varbinary parameter, which SQL Server cannot implicitly type).
                    InputFormatId = 1,
                    Input = [0],
                    AuditLevel = JobAuditLevelCode.Off,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                ct
            );
        return (jobId, jobRef);
    }
}
