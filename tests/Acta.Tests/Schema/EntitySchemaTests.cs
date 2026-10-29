using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// Unit tests for the schema-aware <see cref="DbEntitySpec"/> + <see cref="ActaSchema"/> layer that
/// the provider operations, the migration emitters, and future drift checks all bind through. The
/// entity model is the source of truth - if these break, the guarantee that the schema descriptor
/// stays aligned with the entity model is broken.
/// </summary>
public class EntitySchemaTests
{
    [Fact]
    public void JobSchema_Has_AllExpectedColumns()
    {
        var s = ActaSchema.For<JobEntity>();

        Assert.Equal("jobs", s.TableName);
        Assert.Equal("pk_jobs", s.PrimaryKey.Name);
        Assert.Equal(["job_id"], s.PrimaryKey.Columns);
        Assert.Contains(s.Columns, c => c.Name == "job_ref");
        Assert.Contains(s.Columns, c => c.Name == "deduplication_key");
        Assert.Contains(s.Columns, c => c.Name == "input");
        Assert.Contains(s.Columns, c => c.Name == "parent_job_id");
        Assert.Contains(s.Columns, c => c.Name == "lineage_root_job_id");
        Assert.Equal("event-recording", s.Column("event_recording_code").CodeKind);
    }

    [Fact]
    public void Catalog_And_Satellite_Tables_Name_Their_Own_Identity_And_Columns()
    {
        Assert.Equal(["definition_id"], ActaSchema.For<DefinitionEntity>().PrimaryKey.Columns);
        Assert.Equal(["lane_id"], ActaSchema.For<LaneEntity>().PrimaryKey.Columns);
        Assert.Equal(["namespace_id"], ActaSchema.For<NamespaceEntity>().PrimaryKey.Columns);
        Assert.Equal(["tenant_id"], ActaSchema.For<TenantEntity>().PrimaryKey.Columns);
        Assert.Equal(["worker_id"], ActaSchema.For<WorkerEntity>().PrimaryKey.Columns);
        Assert.Equal(["alert_id"], ActaSchema.For<AlertEntity>().PrimaryKey.Columns);
        Assert.Equal(["job_id", "step_name"], ActaSchema.For<StepEntity>().PrimaryKey.Columns);
        Assert.Equal(["job_id", "checkpoint_kind_code", "checkpoint_name"], ActaSchema.For<CheckpointEntity>().PrimaryKey.Columns);

        var definition = ActaSchema.For<DefinitionEntity>();
        Assert.Equal("COALESCE(job_priority_code_override, job_priority_code)", definition.Column("job_priority_code_effective").Generated);
        Assert.Equal(
            "COALESCE(event_recording_code_override, event_recording_code)",
            definition.Column("event_recording_code_effective").Generated
        );
        Assert.True(definition.Column("lane_name").IsNullable);

        var worker = ActaSchema.For<WorkerEntity>();
        Assert.Contains(worker.Columns, c => c.Name == "host_name");
        Assert.Contains(worker.Checks, c => c.Name == "ck_workers_max_concurrent_executors" && c.Sql == "max_concurrent_executors > 0");

        var step = ActaSchema.For<StepEntity>();
        Assert.Equal("step-failure", step.Column("failure_code").CodeKind);
        Assert.Contains(step.Checks, c => c.Name == "ck_steps_failure_pair");

        var alertColumns = ActaSchema.For<AlertEntity>().Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(
            new HashSet<string> { "alert_origin_code", "alert_severity_code", "alert_kind_code", "alert_message" },
            alertColumns
        );
    }

    [Fact]
    public void JobStateSchema_Owns_MutableState()
    {
        var s = ActaSchema.For<JobStateEntity>();

        Assert.Equal("jobs_state", s.TableName);
        Assert.Equal("pk_jobs_state", s.PrimaryKey.Name);
        Assert.Equal(["job_id"], s.PrimaryKey.Columns);
        Assert.Contains(s.Columns, c => c.Name == "job_status_code");
        Assert.Contains(s.Columns, c => c.Name == "job_priority_code");
        Assert.Contains(s.Columns, c => c.Name == "version");
    }

    [Fact]
    public void ScheduleSchema_Does_Not_Carry_ActorColumns()
    {
        var columns = ActaSchema.For<ScheduleEntity>().Columns.Select(static c => c.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("actor_code", columns);
        Assert.DoesNotContain("actor", columns);
    }

    [Fact]
    public void ScheduleSchema_Leaves_The_Cursor_To_Its_State_Row()
    {
        var config = ActaSchema.For<ScheduleEntity>();
        var state = ActaSchema.For<ScheduleStateEntity>();

        Assert.Equal(["schedule_id"], config.PrimaryKey.Columns);
        Assert.Equal("schedules_state", state.TableName);
        Assert.Equal("pk_schedules_state", state.PrimaryKey.Name);
        Assert.Equal(["schedule_id"], state.PrimaryKey.Columns);
        Assert.DoesNotContain(config.Columns, c => c.Name.Contains("occurrence", StringComparison.Ordinal));
        Assert.Equal(
            [
                "schedule_id",
                "namespace_id",
                "next_occurrence_at_utc",
                "next_evaluation_at_utc",
                "evaluation_status_code",
                "outstanding_count",
                "version",
                "modified_at_utc",
            ],
            state.Columns.Select(c => c.Name)
        );

        var index = Assert.Single(state.Indexes, i => i.Name == "ix_schedules_state_namespace_next");
        Assert.Equal(["namespace_id", "next_occurrence_at_utc", "schedule_id"], index.Columns);
        Assert.Equal("next_occurrence_at_utc IS NOT NULL", index.Filter);
        var due = Assert.Single(state.Indexes, i => i.Name == "ix_schedules_state_due");
        Assert.Equal(["namespace_id", "next_evaluation_at_utc", "schedule_id"], due.Columns);
        Assert.Equal("next_evaluation_at_utc IS NOT NULL", due.Filter);
    }

    [Fact]
    public void Job_ClrType_Is_Set()
    {
        Assert.Equal(typeof(JobEntity), ActaSchema.For<JobEntity>().ClrType);
    }

    [Fact]
    public void StatusCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<JobStateEntity>().Column("job_status_code");

        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
        Assert.False(c.IsNullable);
    }

    [Fact]
    public void PriorityCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<JobStateEntity>().Column("job_priority_code");

        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
    }

    [Fact]
    public void EventMessage_Is_NullableUnicode512()
    {
        var c = ActaSchema.For<EventEntity>().Column("event_message");

        Assert.Equal(DbKind.UnicodeString, c.Kind);
        Assert.Equal(512, c.Size);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void DeduplicationKey_Is_NullableAscii128()
    {
        var c = ActaSchema.For<JobEntity>().Column("deduplication_key");

        Assert.Equal(DbKind.AsciiString, c.Kind);
        Assert.Equal(128, c.Size);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void CaseExact_Marks_Only_The_Compared_Keys()
    {
        // The actor is display text, never compared, and names fold on input: neither needs it.
        var caseExact = ActaSchema
            .Entities.SelectMany(e => e.Columns.Where(c => c.CaseExact).Select(c => $"{e.TableName}.{c.Name}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "alerts.deduplication_key",
                "jobs.concurrency_key",
                "jobs.correlation_key",
                "jobs.deduplication_key",
                "locks.lock_key",
                "tags.tag_value_search",
            ],
            caseExact
        );
    }

    [Fact]
    public void Input_Is_NullablePayload()
    {
        var c = ActaSchema.For<JobEntity>().Column("input");

        Assert.Equal(DbKind.BinaryPayload, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void EventCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<EventEntity>().Column("event_code");

        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
    }

    [Fact]
    public void JobState_Version_Is_ConcurrencyToken()
    {
        var c = ActaSchema.For<JobStateEntity>().Column("version");

        Assert.True(c.IsConcurrencyToken);
        Assert.Equal(DbKind.Int32, c.Kind);
    }

    [Fact]
    public void Job_Id_Is_DbAssignedIdentity()
    {
        // Job.Id is a single-column integer PK with no Manual=true, so it triggers the IDENTITY
        // emission convention (provider-native identity assigns the id at INSERT).
        var c = ActaSchema.For<JobEntity>().Column("job_id");

        Assert.True(c.IsPrimaryKey);
        Assert.True(c.IsIdentity);
    }

    [Fact]
    public void JobNamespace_Id_Is_DbAssignedIdentity()
    {
        // JobNamespace.Id is another "not manual" single-column integer PK that triggers the
        // IDENTITY emission convention.
        var c = ActaSchema.For<NamespaceEntity>().Column("namespace_id");

        Assert.True(c.IsIdentity);
    }

    [Fact]
    public void JobState_JobId_Is_ManualKey()
    {
        // Manual = true: the enqueue writes the job's id, so no identity is emitted.
        var c = ActaSchema.For<JobStateEntity>().Column("job_id");

        Assert.True(c.IsPrimaryKey);
        Assert.False(c.IsIdentity);
    }

    [Fact]
    public void JobEvent_Key_Is_WorkerThenGlobalIdentity()
    {
        // Clustered by worker so concurrent writers insert into separate key ranges; event_id keeps the one
        // global identity, and a worker-less event takes worker_id 0 from the default.
        var s = ActaSchema.For<EventEntity>();

        Assert.Equal("pk_events", s.PrimaryKey.Name);
        Assert.Equal(["worker_id", "event_id"], s.PrimaryKey.Columns);
        Assert.Equal("event_id", s.PrimaryKey.Identity);
        Assert.True(s.PrimaryKey.OptimizeForSequentialKey);
        Assert.True(s.Column("event_id").IsIdentity);

        var worker = s.Column("worker_id");
        Assert.True(worker.IsPrimaryKey);
        Assert.False(worker.IsIdentity);
        Assert.False(worker.IsNullable);
        Assert.Equal(DbDefault.Zero, worker.Default);
    }

    [Fact]
    public void JobEvent_CreatedAtUtc_Has_UtcNowDefault()
    {
        var c = ActaSchema.For<EventEntity>().Column("created_at_utc");

        Assert.Equal(DbDefault.UtcNow, c.Default);
        Assert.True(c.HasServerDefault);
    }

    [Fact]
    public void Job_CreatedAtUtc_Has_UtcNowDefault()
    {
        var c = ActaSchema.For<JobEntity>().Column("created_at_utc");

        Assert.Equal(DbDefault.UtcNow, c.Default);
        Assert.True(c.HasServerDefault);
    }

    [Fact]
    public void Schedule_TimeZoneId_Has_No_ServerDefault()
    {
        // The registration routine always supplies time_zone_id (defaulting to 'UTC' in code), so the
        // column carries no server DEFAULT: the value is caller-owned, not DB-owned.
        var c = ActaSchema.For<ScheduleEntity>().Column("time_zone_id");

        Assert.Equal(DbDefault.None, c.Default);
        Assert.False(c.HasServerDefault);
    }

    [Fact]
    public void JobState_Indexes_Include_Ready()
    {
        var ix = ActaSchema.For<JobStateEntity>().Indexes;

        Assert.Contains(ix, i => i.Name == "ix_jobs_state_claim_ready" && !i.IsUnique);
    }

    [Fact]
    public void Job_Checks_Include_InputPair()
    {
        var ck = ActaSchema.For<JobEntity>().Checks;

        Assert.Contains(ck, c => c.Name == "ck_jobs_input_pair");
    }

    [Fact]
    public void JobEvent_Has_NoForeignKeys()
    {
        // Audit table: retention runs independently of references.
        Assert.Empty(ActaSchema.For<EventEntity>().ForeignKeys);
    }

    [Fact]
    public void StatusCode_Captures_EnumTypeName()
    {
        var c = ActaSchema.For<JobStateEntity>().Column("job_status_code");
        Assert.Equal("JobStatusCode", c.EnumTypeName);
    }

    [Fact]
    public void StatusCode_Captures_CodeKind_FromGeneratedCompanion()
    {
        // The source generator emits CodeManifestEntry instances on each [Code] enum's
        // companion class; DbEntitySpec reads the kebab CodeKind off the first entry.
        // If this assertion breaks, the generator's emission shape changed.
        var c = ActaSchema.For<JobStateEntity>().Column("job_status_code");
        Assert.NotNull(c.CodeKind);
        Assert.False(string.IsNullOrEmpty(c.CodeKind));
    }

    [Fact]
    public void Column_Lookup_Is_CaseSensitive()
    {
        var s = ActaSchema.For<JobStateEntity>();
        Assert.Throws<InvalidOperationException>(() => s.Column("Job_Status_Code"));
        Assert.Throws<InvalidOperationException>(() => s.Column("JOB_STATUS_CODE"));
    }

    [Fact]
    public void Column_Lookup_Throws_OnMissing()
    {
        var s = ActaSchema.For<JobEntity>();
        Assert.Throws<InvalidOperationException>(() => s.Column("does_not_exist"));
    }

    [Fact]
    public void ActaSchema_Surfaces_Same_Column_Instances()
    {
        var byActa = ActaSchema.JobState.JobStatusCode;
        var bySchema = ActaSchema.For<JobStateEntity>().Column("job_status_code");

        Assert.Equal(bySchema, byActa.Untyped);
    }

    [Fact]
    public void ActaSchema_JobEvent_Table_Name()
    {
        Assert.Equal("events", ActaSchema.Event.Entity.TableName);
    }

    [Fact]
    public void Entities_Manifest_Covers_AllKnownEntities()
    {
        var names = ActaSchema.Entities.Select(e => e.TableName).ToList();

        // Spot-check several entities from each category.
        Assert.Contains("jobs", names);
        Assert.Contains("events", names);
        Assert.Contains("definitions", names);
        Assert.Contains("namespaces", names);
        Assert.Contains("jobs_state", names);
        Assert.Contains("schedules_state", names);
        Assert.Contains("workers", names);
    }

    [Fact]
    public void JobTenant_Catalog_Has_TenantName_Unique_And_StatusCode()
    {
        var s = ActaSchema.For<TenantEntity>();

        Assert.Equal("tenants", s.TableName);
        Assert.Equal("pk_tenants", s.PrimaryKey.Name);
        Assert.Contains(s.Indexes, i => i.Name == "ux_tenants_name" && i.IsUnique && i.Columns.SequenceEqual(["tenant_name"]));

        var key = s.Column("tenant_name");
        Assert.Equal(DbKind.AsciiString, key.Kind);
        Assert.Equal(128, key.Size);
        Assert.False(key.IsNullable);

        var status = s.Column("tenant_status_code");
        Assert.True(status.IsCoded);
        Assert.False(status.IsNullable);
    }

    [Fact]
    public void JobTenant_Id_Is_DbAssignedIdentity()
    {
        var c = ActaSchema.For<TenantEntity>().Column("tenant_id");

        Assert.True(c.IsIdentity);
        Assert.Equal(DbKind.Int32, c.Kind);
    }

    [Fact]
    public void Job_TenantId_Is_NullableInt()
    {
        var c = ActaSchema.For<JobEntity>().Column("tenant_id");

        Assert.Equal(DbKind.Int32, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void JobEvent_TenantId_Is_NullableInt()
    {
        var c = ActaSchema.For<EventEntity>().Column("tenant_id");

        Assert.Equal(DbKind.Int32, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Entities_Manifest_Includes_JobTenant()
    {
        Assert.Contains("tenants", ActaSchema.Entities.Select(e => e.TableName));
    }

    [Fact]
    public void Tenant_Status_Immediately_Follows_TenantName_In_Column_Order()
    {
        var columns = ActaSchema.For<TenantEntity>().Columns.Select(c => c.Name).ToList();

        Assert.Equal(["tenant_id", "tenant_name", "tenant_status_code"], columns.Take(3));
    }

    [Fact]
    public void Definition_DisplayName_Triplet_Follows_RunbookUrl_Pattern()
    {
        var s = ActaSchema.For<DefinitionEntity>();
        var bare = s.Column("display_name");
        Assert.Equal(DbKind.UnicodeString, bare.Kind);
        Assert.Equal(128, bare.Size);
        Assert.True(bare.IsNullable);
        Assert.True(s.Column("display_name_override").IsNullable);
        Assert.Equal("COALESCE(display_name_override, display_name)", s.Column("display_name_effective").Generated);
        var desc = s.Column("description");
        Assert.Equal(512, desc.Size);
        Assert.Equal("COALESCE(description_override, description)", s.Column("description_effective").Generated);
    }

    [Fact]
    public void Definition_Backoff_Triplet_Is_UnicodeString64()
    {
        var s = ActaSchema.For<DefinitionEntity>();
        var bare = s.Column("backoff");
        Assert.Equal(DbKind.UnicodeString, bare.Kind);
        Assert.Equal(64, bare.Size);
        Assert.False(bare.IsNullable);
        Assert.True(s.Column("backoff_override").IsNullable);
        Assert.Equal("COALESCE(backoff_override, backoff)", s.Column("backoff_effective").Generated);
    }

    [Fact]
    public void Schedule_Description_Is_NullableUnicode512()
    {
        var c = ActaSchema.For<ScheduleEntity>().Column("description");
        Assert.Equal(DbKind.UnicodeString, c.Kind);
        Assert.Equal(512, c.Size);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Tenant_DisplayName_Is_NullableUnicode128()
    {
        var c = ActaSchema.For<TenantEntity>().Column("display_name");
        Assert.Equal(DbKind.UnicodeString, c.Kind);
        Assert.Equal(128, c.Size);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Alert_AcknowledgedAt_Is_NullableInstant()
    {
        var c = ActaSchema.For<AlertEntity>().Column("acknowledged_at_utc");
        Assert.Equal(DbKind.UtcInstant, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Namespace_StatusCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<NamespaceEntity>().Column("namespace_status_code");
        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
        Assert.False(c.IsNullable);
    }
}
