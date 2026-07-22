using Acta.Relational.Entities;
using Acta.Relational.Schema;
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
        var s = ActaSchema.For<Job>();

        Assert.Equal("jobs", s.TableName);
        Assert.Equal("pk_jobs", s.PrimaryKey.Name);
        Assert.Equal(["id"], s.PrimaryKey.Columns);
        Assert.Contains(s.Columns, c => c.Name == "job_ref");
        Assert.Contains(s.Columns, c => c.Name == "deduplication_key");
        Assert.Contains(s.Columns, c => c.Name == "input");
    }

    [Fact]
    public void JobRuntimeSchema_Owns_MutableState()
    {
        var s = ActaSchema.For<JobRuntime>();

        Assert.Equal("runtimes", s.TableName);
        Assert.Equal("pk_runtimes", s.PrimaryKey.Name);
        Assert.Equal(["job_id"], s.PrimaryKey.Columns);
        Assert.Contains(s.Columns, c => c.Name == "status_code");
        Assert.Contains(s.Columns, c => c.Name == "priority_code");
        Assert.Contains(s.Columns, c => c.Name == "version");
    }

    [Fact]
    public void JobScheduleSchema_Does_Not_Carry_ActorColumns()
    {
        var columns = ActaSchema.For<JobSchedule>().Columns.Select(static c => c.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("actor_code", columns);
        Assert.DoesNotContain("actor_key", columns);
    }

    [Fact]
    public void Job_ClrType_Is_Set()
    {
        Assert.Equal(typeof(Job), ActaSchema.For<Job>().ClrType);
    }

    // ---------- Column-level metadata ----------

    [Fact]
    public void StatusCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<JobRuntime>().Column("status_code");

        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
        Assert.False(c.IsNullable);
    }

    [Fact]
    public void PriorityCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<JobRuntime>().Column("priority_code");

        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
    }

    [Fact]
    public void DeduplicationKey_Is_NullableAscii128()
    {
        var c = ActaSchema.For<Job>().Column("deduplication_key");

        Assert.Equal(DbKind.AsciiString, c.Kind);
        Assert.Equal(128, c.Size);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Input_Is_NullablePayload()
    {
        var c = ActaSchema.For<Job>().Column("input");

        Assert.Equal(DbKind.BinaryPayload, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void EventCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<JobEvent>().Column("event_code");

        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
    }

    // ---------- Defaults, sequences, concurrency tokens, identity ----------

    [Fact]
    public void JobRuntime_Version_Is_ConcurrencyToken()
    {
        var c = ActaSchema.For<JobRuntime>().Column("version");

        Assert.True(c.IsConcurrencyToken);
        Assert.Equal(DbKind.Int32, c.Kind);
    }

    [Fact]
    public void Job_Id_Is_DbAssignedIdentity()
    {
        // Job.Id is a single-column integer PK with no Manual=true, so it triggers the IDENTITY
        // emission convention (provider-native identity assigns the id at INSERT).
        var c = ActaSchema.For<Job>().Column("id");

        Assert.True(c.IsPrimaryKey);
        Assert.True(c.IsSolePrimaryKey);
        Assert.False(c.IsManualPrimaryKey);
    }

    [Fact]
    public void JobNamespace_Id_Is_DbAssignedIdentity()
    {
        // JobNamespace.Id is another "not manual" single-column integer PK that triggers the
        // IDENTITY emission convention.
        var c = ActaSchema.For<JobNamespace>().Column("id");

        Assert.True(c.IsSolePrimaryKey);
        Assert.False(c.IsManualPrimaryKey);
    }

    [Fact]
    public void JobEvent_CreatedAtUtc_Has_UtcNowDefault()
    {
        var c = ActaSchema.For<JobEvent>().Column("created_at_utc");

        Assert.Equal(DbDefault.UtcNow, c.Default);
        Assert.True(c.HasServerDefault);
    }

    [Fact]
    public void Job_CreatedAtUtc_Has_UtcNowDefault()
    {
        var c = ActaSchema.For<Job>().Column("created_at_utc");

        Assert.Equal(DbDefault.UtcNow, c.Default);
        Assert.True(c.HasServerDefault);
    }

    [Fact]
    public void Schedule_TimeZoneId_Has_No_ServerDefault()
    {
        // The registration routine always supplies time_zone_id (defaulting to 'UTC' in code), so the
        // column carries no server DEFAULT: the value is caller-owned, not DB-owned.
        var c = ActaSchema.For<JobSchedule>().Column("time_zone_id");

        Assert.Equal(DbDefault.None, c.Default);
        Assert.False(c.HasServerDefault);
    }

    // ---------- Composite primary key ----------

    [Fact]
    public void JobResult_Has_CompositePk()
    {
        var pk = ActaSchema.For<JobResult>().PrimaryKey;

        Assert.Equal("pk_results", pk.Name);
        Assert.Equal(["job_id", "execution_number"], pk.Columns);
        Assert.False(pk.Manual);
    }

    [Fact]
    public void JobResult_Columns_Both_Marked_IsPrimaryKey()
    {
        var jobId = ActaSchema.For<JobResult>().Column("job_id");
        var en = ActaSchema.For<JobResult>().Column("execution_number");

        Assert.True(jobId.IsPrimaryKey);
        Assert.True(en.IsPrimaryKey);
        Assert.False(jobId.IsSolePrimaryKey); // composite - no IDENTITY convention fires
        Assert.False(en.IsSolePrimaryKey);
    }

    // ---------- Indexes / checks / foreign keys ----------

    [Fact]
    public void JobRuntime_Indexes_Include_Ready()
    {
        var ix = ActaSchema.For<JobRuntime>().Indexes;

        Assert.Contains(ix, i => i.Name == "ix_runtimes_claim_ready" && !i.IsUnique);
    }

    [Fact]
    public void Job_Checks_Include_InputPair()
    {
        var ck = ActaSchema.For<Job>().Checks;

        Assert.Contains(ck, c => c.Name == "ck_jobs_input_pair");
    }

    [Fact]
    public void JobResult_HasForeignKey_To_Job_Cascade()
    {
        var fks = ActaSchema.For<JobResult>().ForeignKeys;
        var fk = Assert.Single(fks, f => f.Name == "fk_results_jobs");

        Assert.Equal("job_id", fk.Column);
        Assert.Equal(typeof(Job), fk.Target);
        Assert.Equal("id", fk.TargetColumn);
        Assert.Equal(DbForeignKeyAction.Cascade, fk.OnDelete);
    }

    [Fact]
    public void JobEvent_Has_NoForeignKeys()
    {
        // Audit table: retention runs independently of references.
        Assert.Empty(ActaSchema.For<JobEvent>().ForeignKeys);
    }

    // ---------- Enum-type capture ----------

    [Fact]
    public void StatusCode_Captures_EnumTypeName()
    {
        var c = ActaSchema.For<JobRuntime>().Column("status_code");
        Assert.Equal("JobStatusCode", c.EnumTypeName);
    }

    [Fact]
    public void StatusCode_Captures_CodeKind_FromGeneratedCompanion()
    {
        // The source generator emits CodeManifestEntry instances on each [Code] enum's
        // companion class; DbEntitySpec reads the kebab CodeKind off the first entry.
        // If this assertion breaks, the generator's emission shape changed.
        var c = ActaSchema.For<JobRuntime>().Column("status_code");
        Assert.NotNull(c.CodeKind);
        Assert.False(string.IsNullOrEmpty(c.CodeKind));
    }

    // ---------- Lookup invariants ----------

    [Fact]
    public void Column_Lookup_Is_CaseSensitive()
    {
        var s = ActaSchema.For<JobRuntime>();
        Assert.Throws<InvalidOperationException>(() => s.Column("Status_Code"));
        Assert.Throws<InvalidOperationException>(() => s.Column("STATUS_CODE"));
    }

    [Fact]
    public void Column_Lookup_Throws_OnMissing()
    {
        var s = ActaSchema.For<Job>();
        Assert.Throws<InvalidOperationException>(() => s.Column("does_not_exist"));
    }

    // ---------- ActaSchema vocabulary ----------

    [Fact]
    public void ActaSchema_Surfaces_Same_Column_Instances()
    {
        var byActa = ActaSchema.JobRuntime.StatusCode;
        var bySchema = ActaSchema.For<JobRuntime>().Column("status_code");

        Assert.Equal(bySchema.Name, byActa.Name);
        Assert.Equal(bySchema.Kind, byActa.Kind);
        Assert.Equal(bySchema.IsCoded, byActa.IsCoded);
        Assert.Equal(bySchema.Default, byActa.Default);
        Assert.Equal(bySchema.IsConcurrencyToken, byActa.IsConcurrencyToken);
    }

    [Fact]
    public void ActaSchema_JobEvent_Table_Name()
    {
        Assert.Equal("events", ActaSchema.JobEvent.Table);
    }

    // ---------- Assembly-wide manifest ----------

    [Fact]
    public void Entities_Manifest_Covers_AllKnownEntities()
    {
        var names = ActaSchema.Entities.Select(e => e.TableName).ToList();

        // Spot-check several entities from each category.
        Assert.Contains("jobs", names);
        Assert.Contains("events", names);
        Assert.Contains("definitions", names);
        Assert.Contains("namespaces", names);
        Assert.Contains("results", names);
        Assert.Contains("workers", names);
    }

    // ---------- Tenant catalog + tenant_id scope ----------

    [Fact]
    public void JobTenant_Catalog_Has_TenantKey_Unique_And_StatusCode()
    {
        var s = ActaSchema.For<Tenant>();

        Assert.Equal("tenants", s.TableName);
        Assert.Equal("pk_tenants", s.PrimaryKey.Name);
        Assert.Contains(s.Indexes, i => i.Name == "ux_tenants_key" && i.IsUnique && i.Columns.SequenceEqual(["tenant_key"]));

        var key = s.Column("tenant_key");
        Assert.Equal(DbKind.AsciiString, key.Kind);
        Assert.Equal(128, key.Size);
        Assert.False(key.IsNullable);

        var status = s.Column("status_code");
        Assert.True(status.IsCoded);
        Assert.False(status.IsNullable);
    }

    [Fact]
    public void JobTenant_Id_Is_DbAssignedIdentity()
    {
        var c = ActaSchema.For<Tenant>().Column("id");

        Assert.True(c.IsSolePrimaryKey);
        Assert.False(c.IsManualPrimaryKey);
        Assert.Equal(DbKind.Int32, c.Kind);
    }

    [Fact]
    public void Job_TenantId_Is_NullableInt()
    {
        var c = ActaSchema.For<Job>().Column("tenant_id");

        Assert.Equal(DbKind.Int32, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void JobEvent_TenantId_Is_NullableInt()
    {
        var c = ActaSchema.For<JobEvent>().Column("tenant_id");

        Assert.Equal(DbKind.Int32, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Entities_Manifest_Includes_JobTenant()
    {
        Assert.Contains("tenants", ActaSchema.Entities.Select(e => e.TableName));
    }

    [Fact]
    public void Tenant_Status_Immediately_Follows_TenantKey_In_Column_Order()
    {
        var columns = ActaSchema.For<Tenant>().Columns.Select(c => c.Name).ToList();

        Assert.Equal(["id", "tenant_key", "status_code"], columns.Take(3));
    }

    // ---------- Central settings table ----------

    [Fact]
    public void Settings_Schema_Has_ScopedIdentity_And_ValuePair()
    {
        var s = ActaSchema.For<Setting>();

        Assert.Equal("settings", s.TableName);
        Assert.Equal("pk_settings", s.PrimaryKey.Name);
        Assert.Contains(
            s.Indexes,
            i => i.Name == "ux_settings_scope_name" && i.IsUnique && i.Columns.SequenceEqual(["scope_code", "scope_id", "name"])
        );
        Assert.Contains(
            s.Indexes,
            i => i.Name == "ux_settings_global_name" && i.IsUnique && i.Columns.SequenceEqual(["scope_code", "name"])
        );
        Assert.Contains(s.Checks, c => c.Name == "ck_settings_value_pair");

        var scope = s.Column("scope_code");
        Assert.True(scope.IsCoded);
        Assert.False(scope.IsNullable);
        Assert.True(s.Column("scope_id").IsNullable);
    }

    [Fact]
    public void Entities_Manifest_Includes_Settings()
    {
        Assert.Contains("settings", ActaSchema.Entities.Select(e => e.TableName));
    }

    // ---------- Operator display / description / ack / namespace status columns ----------

    [Fact]
    public void Definition_DisplayName_Triplet_Follows_RunbookUrl_Pattern()
    {
        var s = ActaSchema.For<JobDefinition>();
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
        var s = ActaSchema.For<JobDefinition>();
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
        var c = ActaSchema.For<JobSchedule>().Column("description");
        Assert.Equal(DbKind.UnicodeString, c.Kind);
        Assert.Equal(512, c.Size);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Tenant_DisplayName_Is_NullableUnicode128()
    {
        var c = ActaSchema.For<Tenant>().Column("display_name");
        Assert.Equal(DbKind.UnicodeString, c.Kind);
        Assert.Equal(128, c.Size);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Alert_AcknowledgedAt_Is_NullableInstant()
    {
        var c = ActaSchema.For<JobAlert>().Column("acknowledged_at_utc");
        Assert.Equal(DbKind.UtcInstant, c.Kind);
        Assert.True(c.IsNullable);
    }

    [Fact]
    public void Namespace_StatusCode_Is_ByteBackedCode()
    {
        var c = ActaSchema.For<JobNamespace>().Column("status_code");
        Assert.Equal(DbKind.Byte, c.Kind);
        Assert.True(c.IsCoded);
        Assert.False(c.IsNullable);
    }
}
