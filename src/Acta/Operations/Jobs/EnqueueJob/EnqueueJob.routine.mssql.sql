-- Only @p_namespace_name and @p_job_name are required; other scalars default (@p_job_ref is
-- server-generated when omitted; @p_input_format_id defaults json/none by input presence). The tag TVP
-- has no default (SQL Server TVPs cannot); pass an empty table variable for a tag-free enqueue.
CREATE OR ALTER PROCEDURE {{schema}}.enqueue_job
    @p_job_ref UNIQUEIDENTIFIER = NULL,
    @p_namespace_name VARCHAR(128) = NULL,
    @p_job_name VARCHAR(128) = NULL,
    @p_deduplication_key VARCHAR(128) = NULL,
    @p_correlation_key VARCHAR(64) = NULL,
    @p_job_priority_code_override TINYINT = NULL,
    @p_input_format_id TINYINT = NULL,
    @p_input VARBINARY(MAX) = NULL,
    @p_concurrency_key VARCHAR(128) = NULL,
    @p_next_run_at_utc DATETIME2(3) = NULL,
    @p_delay_seconds INT = NULL,
    @p_parent_job_id BIGINT = NULL,
    @p_tenant_name VARCHAR(128) = NULL,
    @p_tenant_override BIT = 0,
    @p_tags {{schema}}.enqueue_tags READONLY,
    @p_lane_name VARCHAR(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @ns_id INT, @ns_status TINYINT, @def_id INT, @def_priority SMALLINT;
        DECLARE @def_event_recording TINYINT, @def_status TINYINT, @def_tenant_req TINYINT;
        DECLARE @tenant_id INT, @tenant_status TINYINT, @lineage BIGINT, @parent_corr VARCHAR(64), @parent_tenant INT;
        DECLARE @existing_id BIGINT, @existing_ref UNIQUEIDENTIFIER, @job_id BIGINT;
        DECLARE @lane VARCHAR(128), @lane_id BIGINT, @ancestor_lane_hits INT;

        -- Own a local transaction only when invoked outside one. Inside a caller's transaction (direct
        -- transactional enqueue) the entry count is > 0: run the work but neither commit nor roll back it;
        -- on error rethrow and let the caller roll back the whole transaction.

        -- Server-generate the job ref when the caller omits it (a proc default cannot be NEWID()).
        SET @p_job_ref = COALESCE(@p_job_ref, NEWID());

        SELECT
            @ns_id = ns.namespace_id,
            @ns_status = ns.namespace_status_code,
            @def_id = jd.definition_id,
            @def_priority = jd.job_priority_code_effective,
            @def_event_recording = jd.event_recording_code_effective,
            @def_status = jd.definition_status_code,
            @def_tenant_req = jd.tenant_requirement_code,
            @lane = COALESCE(@p_lane_name, jd.lane_name)
        FROM {{schema}}.namespaces ns
        INNER JOIN {{schema}}.definitions jd
            ON
                jd.namespace_id = ns.namespace_id
                AND jd.definition_name = @p_job_name
        WHERE ns.namespace_name = @p_namespace_name;

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

        IF @def_status <> 10 /* DefinitionStatusCode.Active */
            BEGIN
                THROW 50003, 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.', 1;
            END;

        IF @p_tenant_name IS NOT NULL
            BEGIN
                SELECT
                    @tenant_id = t.tenant_id,
                    @tenant_status = t.tenant_status_code
                FROM {{schema}}.tenants t
                WHERE t.tenant_name = @p_tenant_name;

                IF @tenant_id IS NULL
                    BEGIN
                        THROW 50004, 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.', 1;
                    END;

                IF @tenant_status <> 10 /* TenantStatusCode.Active */
                    BEGIN
                        THROW 50006, 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.', 1;
                    END;
            END;

        IF @def_tenant_req = 20 /* DefinitionTenantRequirementCode.Forbidden */ AND @p_tenant_name IS NOT NULL
            BEGIN
                THROW 50008, 'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: the job definition forbids a tenant and the row names one.', 1;
            END;

        -- The lane row is the lane's mutex, held to commit (docs/sql-internals.md, "Lane
        -- lock order"). The name only resolves the id; the lock is taken by id, on the clustered key.
        -- Ready versus Blocked is read after the lane lock, which a SNAPSHOT transaction (level 5) never sees.
        IF @lane IS NOT NULL
            AND (SELECT s.transaction_isolation_level FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID) = 5
            THROW 50011, 'ACTA:ENQ_LANE_ISOLATION:Enqueue rejected: a laned enqueue cannot run in a SNAPSHOT caller transaction.', 1;

        DECLARE @lane_found BIGINT;
        WHILE @lane IS NOT NULL AND @lane_id IS NULL
            BEGIN
                SET @lane_found = NULL;
                SELECT @lane_found = l.lane_id
                FROM {{schema}}.lanes l
                WHERE
                    l.namespace_id = @ns_id
                    AND l.lane_name = @lane;

                IF @lane_found IS NOT NULL
                    SELECT @lane_id = l.lane_id
                    FROM {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK)
                    WHERE l.lane_id = @lane_found;

                IF @lane_found IS NULL
                    BEGIN
                        SET XACT_ABORT OFF;
                        BEGIN TRY
                            INSERT INTO {{schema}}.lanes (namespace_id, lane_name, created_at_utc)
                            VALUES (@ns_id, @lane, @now);
                        END TRY
                        BEGIN CATCH
                            SET XACT_ABORT ON;
                            IF ERROR_NUMBER() NOT IN (2601, 2627)
                                THROW;
                        END CATCH;
                        SET XACT_ABORT ON;
                    END;
            END;

        IF @p_deduplication_key IS NOT NULL
            BEGIN
                -- The key's own lock serializes enqueues of one key to commit (docs/sql-internals.md,
                -- "Deduplication lock"); the same name in enqueue_jobs.
                DECLARE @dedup_lock NVARCHAR(255) = CONCAT(
                    N'{{schema}}.dedup:',
                    CASE WHEN @p_parent_job_id IS NULL THEN CONCAT(N'n', @ns_id) ELSE CONCAT(N'p', @p_parent_job_id) END,
                    N':',
                    @p_deduplication_key
                );
                DECLARE @dedup_lock_result INT;
                -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
                EXEC @dedup_lock_result = sp_getapplock @dedup_lock, 'Exclusive', 'Transaction', -1;
                IF @dedup_lock_result < 0
                    THROW 51205, 'ACTA:DEDUP_LOCK:The deduplication key lock was not granted (deadlock victim or cancelled).', 1;

                -- Each probe seeks its dedup index, the index's filter restated: a scan under UPDLOCK locks every row it
                -- reads and escalates to a table lock on jobs, which a stale row estimate makes the cheaper plan.
                IF @p_parent_job_id IS NOT NULL
                    BEGIN
                        SELECT
                            @existing_id = j.job_id,
                            @existing_ref = j.job_ref
                        FROM {{schema}}.jobs j WITH (UPDLOCK, FORCESEEK, INDEX (ux_jobs_deduplication_key_child))
                        WHERE
                            j.parent_job_id = @p_parent_job_id
                            AND j.deduplication_key = @p_deduplication_key
                            AND j.parent_job_id IS NOT NULL
                            AND j.deduplication_key IS NOT NULL;
                    END
                ELSE
                    BEGIN
                        SELECT
                            @existing_id = j.job_id,
                            @existing_ref = j.job_ref
                        FROM {{schema}}.jobs j WITH (UPDLOCK, FORCESEEK, INDEX (ux_jobs_deduplication_key_root))
                        WHERE
                            j.namespace_id = @ns_id
                            AND j.deduplication_key = @p_deduplication_key
                            AND j.parent_job_id IS NULL
                            AND j.deduplication_key IS NOT NULL;
                    END;
            END;

        IF @p_parent_job_id IS NOT NULL
            BEGIN
                SELECT
                    @lineage = COALESCE(pj.lineage_root_job_id, pj.job_id),
                    @parent_corr = pj.correlation_key,
                    @parent_tenant = pj.tenant_id
                FROM {{schema}}.jobs pj WITH (UPDLOCK, ROWLOCK, FORCESEEK)
                INNER JOIN {{schema}}.jobs_state pr ON pr.job_id = pj.job_id
                WHERE
                    pj.job_id = @p_parent_job_id
                    AND pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

                IF @lineage IS NULL
                    BEGIN
                        THROW 50002, 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.', 1;
                    END;

                IF @lane_id IS NOT NULL
                    BEGIN
                        WITH ancestors AS (
                            SELECT a.job_id, a.parent_job_id
                            FROM {{schema}}.jobs a
                            WHERE a.job_id = @p_parent_job_id
                            UNION ALL
                            SELECT a.job_id, a.parent_job_id
                            FROM {{schema}}.jobs a
                            INNER JOIN ancestors c ON a.job_id = c.parent_job_id
                        )

                        SELECT @ancestor_lane_hits = COUNT(*)
                        FROM ancestors c
                        INNER JOIN {{schema}}.jobs_state ar ON ar.job_id = c.job_id
                        WHERE
                            ar.lane_id = @lane_id
                            AND ar.job_status_code IN (
                                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                            )
                        -- A lineage has no depth bound, so neither has the walk.
                        OPTION (MAXRECURSION 0);

                        IF @ancestor_lane_hits > 0
                            BEGIN
                                DECLARE
                                    @ancestor_msg NVARCHAR(2048) = 'ACTA:ENQ_ANCESTOR_LANE:Enqueue rejected: a child names the lane of an'
                                    + ' unfinished ancestor, so it would wait behind the ancestor that waits for it.';
                                THROW 50010, @ancestor_msg, 1;
                            END;
                    END;
            END;

        IF @def_tenant_req = 10 /* DefinitionTenantRequirementCode.Required */ AND @tenant_id IS NULL AND @parent_tenant IS NULL
            BEGIN
                THROW 50007, 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: the job definition requires a tenant and the row carries none.', 1;
            END;

        IF @tenant_id IS NOT NULL AND @parent_tenant IS NOT NULL AND @tenant_id <> @parent_tenant AND @p_tenant_override = 0
            BEGIN
                THROW 50009,
                'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: a child TenantName differs from the parent tenant without an explicit override.',
                1;
            END;

        IF @existing_id IS NULL
            BEGIN
                INSERT INTO {{schema}}.jobs (
                    job_ref, lineage_root_job_id, parent_job_id,
                    deduplication_key, correlation_key,
                    namespace_id, definition_id, tenant_id,
                    input_format_id, input,
                    concurrency_key, event_recording_code,
                    created_at_utc
                )
                VALUES (
                    @p_job_ref, @lineage, @p_parent_job_id,
                    @p_deduplication_key, COALESCE(@p_correlation_key, @parent_corr),
                    @ns_id, @def_id,
                    CASE
                        WHEN @def_tenant_req = 20 /* DefinitionTenantRequirementCode.Forbidden */ THEN NULL
                        ELSE COALESCE(@tenant_id, @parent_tenant)
                    END,
                    COALESCE(
                        @p_input_format_id,
                        CASE WHEN @p_input IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END
                    ),
                    @p_input,
                    @p_concurrency_key, @def_event_recording,
                    @now
                );

                SET @job_id = SCOPE_IDENTITY();

                -- A laned job enters Ready only when no member of its lane is unfinished; otherwise it waits
                -- as Blocked, keeping its due instant for when the settle ahead of it promotes it.
                DECLARE @status TINYINT = 10 /* JobStatusCode.Ready */;
                IF
                    @lane_id IS NOT NULL
                    AND EXISTS (
                        SELECT 1
                        FROM {{schema}}.jobs_state m WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                        WHERE
                            m.lane_id = @lane_id
                            AND m.lane_id IS NOT NULL
                            AND m.job_status_code IN (
                                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                            )
                    )
                    SET @status = 15 /* JobStatusCode.Blocked */;

                INSERT INTO {{schema}}.jobs_state (
                    job_id, namespace_id, lane_id, job_status_code, job_priority_code, next_run_at_utc,
                    execution_number, failure_count, retention_until_utc,
                    modified_at_utc, version
                )
                VALUES (
                    @job_id, @ns_id, @lane_id, @status,
                    COALESCE(@p_job_priority_code_override, @def_priority),
                    COALESCE(@p_next_run_at_utc, DATEADD(SECOND, COALESCE(@p_delay_seconds, 0), @now)),
                    0, 0, NULL,
                    @now, 0
                );

                INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
                SELECT
                    50 /* EntityCode.Job */,
                    @job_id,
                    t.tag_name,
                    t.tag_value,
                    t.tag_value_search
                FROM @p_tags t;
            END;

        SELECT
            0 AS ordinal,
            COALESCE(@job_id, @existing_id) AS job_id,
            CASE WHEN @job_id IS NOT NULL THEN @p_job_ref ELSE @existing_ref END AS job_ref,
            CASE
                WHEN @job_id IS NOT NULL
                    THEN 1 /* EnqueueJobAction.Inserted */
                ELSE 2 /* EnqueueJobAction.Deduplicated */
            END AS action;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
