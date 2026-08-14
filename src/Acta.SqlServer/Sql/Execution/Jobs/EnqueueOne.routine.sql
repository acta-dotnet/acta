-- Only @p_namespace_name and @p_job_name are required; other scalars default (@p_job_ref is
-- server-generated when omitted; @p_input_format_id defaults json/none by input presence). The tag TVP
-- has no default (SQL Server TVPs cannot); pass an empty table variable for a tag-free enqueue.
CREATE OR ALTER PROCEDURE {{schema}}.enqueue_one
    @p_job_ref UNIQUEIDENTIFIER = NULL,
    @p_namespace_name VARCHAR(128) = NULL,
    @p_job_name VARCHAR(128) = NULL,
    @p_deduplication_key VARCHAR(128) = NULL,
    @p_correlation_key VARCHAR(64) = NULL,
    @p_priority_override TINYINT = NULL,
    @p_input_format_id TINYINT = NULL,
    @p_input VARBINARY(MAX) = NULL,
    @p_exclusive_key VARCHAR(128) = NULL,
    @p_next_run_at_utc DATETIME2(3) = NULL,
    @p_delay_seconds INT = NULL,
    @p_parent_id BIGINT = NULL,
    @p_tenant_key VARCHAR(128) = NULL,
    @p_tenant_override BIT = 0,
    @p_tag_batch {{schema}}.job_enqueue_tag_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ns_id SMALLINT, @ns_status TINYINT, @def_id INT, @def_priority SMALLINT;
    DECLARE @def_audit TINYINT, @def_status TINYINT, @def_tenant_req TINYINT;
    DECLARE @tenant_id INT, @tenant_status TINYINT, @lineage BIGINT, @parent_corr VARCHAR(64), @parent_tenant INT;
    DECLARE @existing_id BIGINT, @existing_ref UNIQUEIDENTIFIER, @job_id BIGINT;

    -- Own a local transaction only when invoked outside one. Inside a caller's transaction (direct
    -- transactional enqueue) the entry count is > 0: run the work but neither commit nor roll back it;
    -- on error rethrow and let the caller roll back the whole transaction.
    DECLARE @entry_trancount INT = @@TRANCOUNT;

    -- Server-generate the job ref when the caller omits it (a proc default cannot be NEWID()).
    SET @p_job_ref = COALESCE(@p_job_ref, NEWID());

    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        SELECT
            @ns_id = ns.id,
            @ns_status = ns.status_code,
            @def_id = jd.id,
            @def_priority = jd.priority_code_effective,
            @def_audit = jd.audit_level_code_effective,
            @def_status = jd.status_code,
            @def_tenant_req = jd.tenant_requirement_code
        FROM {{schema}}.namespaces ns
        INNER JOIN {{schema}}.definitions jd
            ON
                jd.namespace_id = ns.id
                AND jd.name = @p_job_name
        WHERE ns.name = @p_namespace_name;

        IF @def_id IS NULL
            BEGIN
                DECLARE
                    @route_msg NVARCHAR(2048) = 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown'
                    + ' namespace or job. Has the owning worker run InitializeAsync yet?';
                THROW 50001, @route_msg, 1;
            END;

        IF @ns_status <> 10 /* NamespaceStatusCode.Active */
            BEGIN
                THROW 50005, 'ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.', 1;
            END;

        IF @def_status <> 10 /* JobDefinitionStatusCode.Active */
            BEGIN
                THROW 50003, 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.', 1;
            END;

        IF @p_tenant_key IS NOT NULL
            BEGIN
                SELECT
                    @tenant_id = t.id,
                    @tenant_status = t.status_code
                FROM {{schema}}.tenants t
                WHERE t.tenant_key = @p_tenant_key;

                IF @tenant_id IS NULL
                    BEGIN
                        THROW 50004, 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.', 1;
                    END;

                IF @tenant_status <> 10 /* TenantStatusCode.Active */
                    BEGIN
                        THROW 50006, 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.', 1;
                    END;
            END;

        IF @def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */ AND @p_tenant_key IS NOT NULL
            BEGIN
                THROW 50008, 'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: the job definition forbids a tenant and the row names one.', 1;
            END;

        IF @p_deduplication_key IS NOT NULL
            BEGIN
                IF @p_parent_id IS NOT NULL
                    BEGIN
                        SELECT
                            @existing_id = j.id,
                            @existing_ref = j.job_ref
                        FROM {{schema}}.jobs j WITH (UPDLOCK, HOLDLOCK)
                        WHERE
                            j.parent_id = @p_parent_id
                            AND j.deduplication_key = @p_deduplication_key;
                    END
                ELSE
                    BEGIN
                        SELECT
                            @existing_id = j.id,
                            @existing_ref = j.job_ref
                        FROM {{schema}}.jobs j WITH (UPDLOCK, HOLDLOCK)
                        WHERE
                            j.namespace_id = @ns_id
                            AND j.deduplication_key = @p_deduplication_key
                            AND j.parent_id IS NULL;
                    END;
            END;

        IF @p_parent_id IS NOT NULL
            BEGIN
                SELECT
                    @lineage = COALESCE(pj.lineage_root_id, pj.id),
                    @parent_corr = pj.correlation_key,
                    @parent_tenant = pj.tenant_id
                FROM {{schema}}.jobs pj WITH (UPDLOCK, ROWLOCK)
                INNER JOIN {{schema}}.runtimes pr ON pr.job_id = pj.id
                WHERE
                    pj.id = @p_parent_id
                    AND pr.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

                IF @lineage IS NULL
                    BEGIN
                        THROW 50002, 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.', 1;
                    END;
            END;

        IF @def_tenant_req = 10 /* JobTenantRequirementCode.Required */ AND @tenant_id IS NULL AND @parent_tenant IS NULL
            BEGIN
                THROW 50007, 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: the job definition requires a tenant and the row carries none.', 1;
            END;

        IF @tenant_id IS NOT NULL AND @parent_tenant IS NOT NULL AND @tenant_id <> @parent_tenant AND @p_tenant_override = 0
            BEGIN
                THROW 50009,
                'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: a child TenantKey differs from the parent tenant without an explicit override.',
                1;
            END;

        IF @existing_id IS NULL
            BEGIN
                INSERT INTO {{schema}}.jobs (
                    job_ref, lineage_root_id, parent_id,
                    deduplication_key, correlation_key,
                    namespace_id, definition_id, tenant_id,
                    input_format_id, input,
                    exclusive_key, audit_level_code,
                    created_at_utc
                )
                VALUES (
                    @p_job_ref, @lineage, @p_parent_id,
                    @p_deduplication_key, COALESCE(@p_correlation_key, @parent_corr),
                    @ns_id, @def_id,
                    CASE
                        WHEN @def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */ THEN NULL
                        ELSE COALESCE(@tenant_id, @parent_tenant)
                    END,
                    COALESCE(
                        @p_input_format_id,
                        CASE WHEN @p_input IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END
                    ),
                    @p_input,
                    @p_exclusive_key, @def_audit,
                    @now
                );

                SET @job_id = SCOPE_IDENTITY();

                INSERT INTO {{schema}}.runtimes (
                    job_id, namespace_id, status_code, priority_code, next_run_at_utc,
                    execution_number, failure_count, retention_until_utc,
                    modified_at_utc, version
                )
                VALUES (
                    @job_id, @ns_id, 10 /* JobStatusCode.Ready */,
                    COALESCE(@p_priority_override, @def_priority),
                    COALESCE(@p_next_run_at_utc, DATEADD(SECOND, COALESCE(@p_delay_seconds, 0), @now)),
                    0, 0, NULL,
                    @now, 0
                );

                INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
                SELECT
                    50 /* TagScopeCode.Job */,
                    @job_id,
                    @ns_id,
                    t.name,
                    t.value,
                    t.value_search
                FROM @p_tag_batch t;
            END;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            BEGIN
                ROLLBACK TRANSACTION;
            END;

        THROW;
    END CATCH;

    SELECT
        0 AS ordinal,
        COALESCE(@job_id, @existing_id) AS job_id,
        CASE WHEN @job_id IS NOT NULL THEN @p_job_ref ELSE @existing_ref END AS job_ref,
        CASE
            WHEN @job_id IS NOT NULL
                THEN 1 /* JobEnqueueAction.Inserted */
            ELSE 2 /* JobEnqueueAction.Deduplicated */
        END AS action;
END;
GO
