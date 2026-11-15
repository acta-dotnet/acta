SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO


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
    @p_lane_name VARCHAR(128) = NULL,
    @p_parent_execution_number BIGINT = NULL,
    @p_parent_worker_id INT = NULL
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
        DECLARE @existing_id BIGINT, @existing_ref UNIQUEIDENTIFIER, @existing_tenant INT, @job_id BIGINT;
        DECLARE @lane VARCHAR(128), @lane_id BIGINT, @ancestor_lane_hits INT;

        -- Own a local transaction only when invoked outside one. Inside a caller's transaction (direct
        -- transactional enqueue) the entry count is > 0: run the work but neither commit nor roll back it;
        -- on error rethrow and let the caller roll back the whole transaction.

        -- Server-generate the job ref when the caller omits it (a proc default cannot be NEWID()).
        SET @p_job_ref = COALESCE(@p_job_ref, NEWID());

        -- The claim reads band by band, so a priority between two bands would be stored and never claimed.
        IF @p_job_priority_code_override NOT IN (0 /* JobPriorityCode.Bulk */, 50 /* JobPriorityCode.Normal */, 70 /* JobPriorityCode.High */, 85 /* JobPriorityCode.Critical */, 100 /* JobPriorityCode.Realtime */)
            THROW 50014, 'Enqueue rejected: the job priority is not a priority band (0, 50, 70, 85, 100).', 1;

        -- A blank key is a key: every later enqueue with it would deduplicate onto the first job, or share its slot.
        -- Strings compare padded here, so a key of spaces is refused with the empty one, as PostgreSQL's trim does.
        IF @p_deduplication_key = '' OR @p_correlation_key = '' OR @p_concurrency_key = ''
            THROW 50015, 'Enqueue rejected: a key is blank. Pass NULL for no key.', 1;

        SELECT
            @ns_id = ns.namespace_id,
            @ns_status = ns.namespace_status_code,
            @def_id = jd.definition_id,
            @def_priority = jd.job_priority_code_effective,
            @def_event_recording = jd.event_recording_code_effective,
            @def_status = jd.definition_status_code,
            @def_tenant_req = jd.tenant_requirement_code,
            @lane = COALESCE(@p_lane_name, jd.lane_name)
        -- Seeks its own namespace and definition: a caller's transaction at repeatable read or serializable keeps every
        -- row a read touches locked to commit, so a scan would hold every namespace's row (docs/sql-internals.md,
        -- "Locking reads seek").
        FROM {{schema}}.namespaces ns WITH (FORCESEEK (ux_namespaces_name (namespace_name)))
        INNER JOIN {{schema}}.definitions jd WITH (FORCESEEK (ux_definitions_namespace_name (namespace_id, definition_name)))
            ON
                jd.namespace_id = ns.namespace_id
                AND jd.definition_name = @p_job_name
        WHERE ns.namespace_name = @p_namespace_name;

        IF @def_id IS NULL
            BEGIN
                DECLARE
                    @route_msg NVARCHAR(2048) = 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown'
                    + ' namespace or job. Have the jobs been deployed there (jobs deploy)?';
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
                            @existing_ref = j.job_ref,
                            @existing_tenant = j.tenant_id
                        FROM {{schema}}.jobs j WITH (UPDLOCK, FORCESEEK, INDEX (ux_jobs_deduplication_key_root))
                        WHERE
                            j.namespace_id = @ns_id
                            AND j.deduplication_key = @p_deduplication_key
                            AND j.parent_job_id IS NULL
                            AND j.deduplication_key IS NOT NULL;
                    END;

                -- A key another tenant's job holds is refused, naming neither: the caller learns only that it is taken.
                IF @existing_tenant IS NOT NULL AND @tenant_id IS NOT NULL AND @existing_tenant <> @tenant_id
                    THROW 50009, 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: the deduplication key belongs to another tenant''s job.', 1;
            END;

        IF @p_parent_job_id IS NOT NULL
            BEGIN
                SELECT
                    @lineage = COALESCE(pj.lineage_root_job_id, pj.job_id),
                    @parent_corr = pj.correlation_key,
                    @parent_tenant = pj.tenant_id
                FROM {{schema}}.jobs pj WITH (UPDLOCK, ROWLOCK, FORCESEEK)
                INNER JOIN {{schema}}.jobs_state pr WITH (FORCESEEK) ON pr.job_id = pj.job_id
                WHERE
                    pj.job_id = @p_parent_job_id
                    AND pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

                -- A child its parent's handler starts lands only while that attempt holds the parent's row, update-locked
                -- to commit after the parent's jobs row (docs/sql-internals.md, "Execution fence").
                IF @p_parent_execution_number IS NOT NULL
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1
                            FROM {{schema}}.jobs_state fr WITH (UPDLOCK, ROWLOCK)
                            WHERE
                                fr.job_id = @p_parent_job_id
                                AND fr.job_status_code = 50 /* JobStatusCode.Executing */
                                AND fr.execution_number = @p_parent_execution_number
                                AND fr.leased_by_worker_id = @p_parent_worker_id
                        )
                            THROW 50012, 'ACTA:EXECUTION_FENCED:enqueue_job: the parent job is no longer executing this attempt.', 1;
                    END;

                IF @lineage IS NULL
                    BEGIN
                        THROW 50002, 'ACTA:ENQ_PARENT_UNAVAILABLE:Enqueue rejected: one or more child rows reference a missing or terminal parent job.', 1;
                    END;

                IF @lane_id IS NOT NULL
                    BEGIN
                        WITH ancestors AS (
                            SELECT a.job_id, a.parent_job_id
                            FROM {{schema}}.jobs a WITH (FORCESEEK)
                            WHERE a.job_id = @p_parent_job_id
                            UNION ALL
                            SELECT a.job_id, a.parent_job_id
                            FROM {{schema}}.jobs a WITH (FORCESEEK)
                            INNER JOIN ancestors c ON a.job_id = c.parent_job_id
                        )

                        SELECT @ancestor_lane_hits = COUNT(*)
                        FROM ancestors c
                        INNER JOIN {{schema}}.jobs_state ar WITH (FORCESEEK) ON ar.job_id = c.job_id
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
                    job_kind_code, job_ref, lineage_root_job_id, parent_job_id,
                    deduplication_key, correlation_key,
                    namespace_id, definition_id, tenant_id,
                    input_format_id, input,
                    concurrency_key, event_recording_code,
                    created_at_utc
                )
                VALUES (
                    10 /* JobKindCode.Ordinary */, @p_job_ref, @lineage, @p_parent_job_id,
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
GO

-- Lock order: the batch's lanes in id order, then job rows (docs/sql-internals.md,
-- "Lane lock order").
CREATE OR ALTER PROCEDURE {{schema}}.enqueue_jobs
    @p_batch {{schema}}.enqueue_jobs READONLY,
    @p_tags {{schema}}.enqueue_tags READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

        -- Own a local transaction only when invoked outside one. Inside a caller's transaction (direct
        -- transactional enqueue) the entry count is > 0: run the work but neither commit nor roll back it;
        -- on error rethrow and let the caller roll back the whole transaction.

        DECLARE @resolved TABLE (
            ordinal INT PRIMARY KEY,
            ns_id INT NOT NULL,
            ns_status TINYINT NOT NULL,
            def_id INT NOT NULL,
            def_priority SMALLINT NOT NULL,
            def_event_recording TINYINT NOT NULL,
            def_status TINYINT NOT NULL,
            def_tenant_req TINYINT NOT NULL,
            tenant_id INT NULL,
            lane VARCHAR(128) NULL,
            lane_id BIGINT NULL,
            lane_head BIT NOT NULL DEFAULT 0
        );

        INSERT INTO @resolved (ordinal, ns_id, ns_status, def_id, def_priority, def_event_recording, def_status, def_tenant_req, lane)
        SELECT
            b.ordinal,
            ns.namespace_id,
            ns.namespace_status_code,
            jd.definition_id,
            jd.job_priority_code_effective,
            jd.event_recording_code_effective,
            jd.definition_status_code,
            jd.tenant_requirement_code,
            COALESCE(b.lane_name, jd.lane_name)
        -- Seeks each row's own namespace and definition, as enqueue_job does.
        FROM @p_batch b
        INNER JOIN {{schema}}.namespaces ns WITH (FORCESEEK (ux_namespaces_name (namespace_name))) ON ns.namespace_name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd WITH (FORCESEEK (ux_definitions_namespace_name (namespace_id, definition_name)))
            ON
                jd.namespace_id = ns.namespace_id
                AND jd.definition_name = b.job_name;

        IF (SELECT COUNT(*) FROM @resolved) < (SELECT COUNT(*) FROM @p_batch)
            BEGIN
                DECLARE
                    @route_msg NVARCHAR(2048) = 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown'
                    + ' namespace or job. Have the jobs been deployed there (jobs deploy)?';
                THROW 50001, @route_msg, 1;
            END;

        IF
            EXISTS (
                SELECT 1 FROM @resolved
                WHERE ns_status <> 10 /* NamespaceStatusCode.Active */
            )
            BEGIN
                THROW 50005, 'ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.', 1;
            END;

        IF
            EXISTS (
                SELECT 1 FROM @resolved
                WHERE def_status <> 10 /* DefinitionStatusCode.Active */
            )
            BEGIN
                THROW 50003, 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.', 1;
            END;

        IF
            EXISTS (
                SELECT 1 FROM @p_batch
                WHERE tenant_name IS NOT NULL
            )
            BEGIN
                -- A named tenant resolves by name alone, and the checks below judge the id resolved here, so a tenant
                -- registered, suspended, or resumed in between can never leave the stored job without it.
                UPDATE r
                SET tenant_id = t.tenant_id
                FROM @resolved r
                INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                INNER JOIN {{schema}}.tenants t ON t.tenant_name = b.tenant_name
                WHERE b.tenant_name IS NOT NULL;

                IF
                    EXISTS (
                        SELECT 1
                        FROM @p_batch b
                        INNER JOIN @resolved r ON r.ordinal = b.ordinal
                        WHERE b.tenant_name IS NOT NULL AND r.tenant_id IS NULL
                    )
                    BEGIN
                        THROW 50004, 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.', 1;
                    END;
                IF
                    EXISTS (
                        SELECT 1 FROM @resolved r JOIN {{schema}}.tenants t ON t.tenant_id = r.tenant_id
                        WHERE t.tenant_status_code <> 10 /* TenantStatusCode.Active */
                    )
                    BEGIN
                        THROW 50006, 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.', 1;
                    END;
            END;

        -- Missing lanes are inserted in namespace and name order without a range lock, then every lane is locked one row
        -- at a time in id order (docs/sql-internals.md, "Lane lock order").
        DECLARE @lanes TABLE (
            ns_id INT NOT NULL,
            lane_name VARCHAR(128) NOT NULL,
            lane_id BIGINT NULL,
            PRIMARY KEY (ns_id, lane_name)
        );

        INSERT INTO @lanes (ns_id, lane_name)
        SELECT DISTINCT r.ns_id, r.lane
        FROM @resolved r
        WHERE r.lane IS NOT NULL;

        DECLARE @laned BIT = CASE WHEN EXISTS (SELECT 1 FROM @lanes) THEN 1 ELSE 0 END;
        DECLARE @lanes_pending BIT = @laned;
        DECLARE @lane_cursor BIGINT, @lane_next BIGINT, @lane_locked BIGINT;

        -- Ready versus Blocked is read after the lane locks, which a SNAPSHOT transaction (level 5) never sees.
        IF @lanes_pending = 1
            AND (SELECT s.transaction_isolation_level FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID) = 5
            THROW 50011, 'ACTA:ENQ_LANE_ISOLATION:Enqueue rejected: a laned enqueue cannot run in a SNAPSHOT caller transaction.', 1;

        WHILE @lanes_pending = 1
            BEGIN
                SET XACT_ABORT OFF;
                BEGIN TRY
                    INSERT INTO {{schema}}.lanes (namespace_id, lane_name, created_at_utc)
                    SELECT e.ns_id, e.lane_name, @now
                    FROM @lanes e
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM {{schema}}.lanes l WITH (FORCESEEK)
                        WHERE
                            l.namespace_id = e.ns_id
                            AND l.lane_name = e.lane_name
                    )
                    ORDER BY e.ns_id, e.lane_name;
                END TRY
                BEGIN CATCH
                    SET XACT_ABORT ON;
                    IF ERROR_NUMBER() NOT IN (2601, 2627)
                        THROW;
                END CATCH;
                SET XACT_ABORT ON;

                UPDATE e
                SET lane_id = l.lane_id
                -- Both lane reads seek their own names, so a caller's transaction never waits on another lane's insert.
                FROM @lanes e
                LEFT JOIN {{schema}}.lanes l WITH (FORCESEEK)
                    ON
                        l.namespace_id = e.ns_id
                        AND l.lane_name = e.lane_name;

                SET @lanes_pending = CASE WHEN EXISTS (SELECT 1 FROM @lanes WHERE lane_id IS NULL) THEN 1 ELSE 0 END;
                SET @lane_cursor = 0;
                WHILE @lanes_pending = 0
                    BEGIN
                        SET @lane_next = NULL;
                        SELECT TOP (1) @lane_next = e.lane_id
                        FROM @lanes e
                        WHERE e.lane_id > @lane_cursor
                        ORDER BY e.lane_id;

                        IF @lane_next IS NULL
                            BREAK;

                        SET @lane_locked = NULL;
                        SELECT @lane_locked = l.lane_id
                        FROM {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK)
                        WHERE l.lane_id = @lane_next;

                        IF @lane_locked IS NULL
                            SET @lanes_pending = 1;
                        SET @lane_cursor = @lane_next;
                    END;
            END;

        UPDATE r
        SET lane_id = e.lane_id
        FROM @resolved r
        INNER JOIN @lanes e
            ON
                e.ns_id = r.ns_id
                AND e.lane_name = r.lane;

        DECLARE @existing TABLE (
            ordinal INT PRIMARY KEY,
            job_id BIGINT NOT NULL,
            job_ref UNIQUEIDENTIFIER NOT NULL
        );

        IF
            EXISTS (
                SELECT 1 FROM @p_batch
                WHERE deduplication_key IS NOT NULL
            )
            BEGIN
                -- Each key's own lock, named as enqueue_job names it, taken in name order so two batches sharing keys
                -- never deadlock (docs/sql-internals.md, "Deduplication lock").
                DECLARE @dedup_locks TABLE (name NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY);
                INSERT INTO @dedup_locks (name)
                SELECT DISTINCT
                    CONCAT(
                        N'{{schema}}.dedup:',
                        CASE WHEN b.parent_job_id IS NULL THEN CONCAT(N'n', r.ns_id) ELSE CONCAT(N'p', b.parent_job_id) END,
                        N':',
                        b.deduplication_key
                    )
                FROM @p_batch b
                LEFT JOIN @resolved r ON r.ordinal = b.ordinal
                WHERE
                    b.deduplication_key IS NOT NULL
                    AND (b.parent_job_id IS NOT NULL OR r.ns_id IS NOT NULL);

                DECLARE @dedup_lock NVARCHAR(255) = (SELECT MIN(name) FROM @dedup_locks), @dedup_lock_result INT;
                WHILE @dedup_lock IS NOT NULL
                    BEGIN
                        -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
                        EXEC @dedup_lock_result = sp_getapplock @dedup_lock, 'Exclusive', 'Transaction', -1;
                        IF @dedup_lock_result < 0
                            THROW 51205, 'ACTA:DEDUP_LOCK:The deduplication key lock was not granted (deadlock victim or cancelled).', 1;
                        SET @dedup_lock = (SELECT MIN(name) FROM @dedup_locks WHERE name > @dedup_lock);
                    END;

                -- Each probe seeks its dedup index on both key columns, the index's filter restated: a scan under UPDLOCK
                -- locks every row it reads and escalates to a table lock on jobs, which a stale row estimate makes the
                -- cheaper plan, and a seek on the leading column alone reads every key under it once per batch row.
                INSERT INTO @existing (ordinal, job_id, job_ref)
                SELECT
                    b.ordinal,
                    j.job_id,
                    j.job_ref
                FROM @p_batch b
                INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, FORCESEEK (ux_jobs_deduplication_key_child (parent_job_id, deduplication_key)))
                    ON
                        j.parent_job_id = b.parent_job_id
                        AND j.deduplication_key = b.deduplication_key
                        AND j.parent_job_id IS NOT NULL
                        AND j.deduplication_key IS NOT NULL
                WHERE
                    b.deduplication_key IS NOT NULL
                    AND b.parent_job_id IS NOT NULL;

                INSERT INTO @existing (ordinal, job_id, job_ref)
                SELECT
                    b.ordinal,
                    j.job_id,
                    j.job_ref
                FROM @p_batch b
                INNER JOIN @resolved r ON r.ordinal = b.ordinal
                INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, FORCESEEK (ux_jobs_deduplication_key_root (namespace_id, deduplication_key)))
                    ON
                        j.namespace_id = r.ns_id
                        AND j.deduplication_key = b.deduplication_key
                        AND j.parent_job_id IS NULL
                        AND j.deduplication_key IS NOT NULL
                WHERE
                    b.deduplication_key IS NOT NULL
                    AND b.parent_job_id IS NULL;

                -- A key another tenant's job holds refuses the whole batch, naming neither: the caller learns only that it
                -- is taken.
                IF
                    EXISTS (
                        SELECT 1
                        FROM @existing x
                        INNER JOIN @resolved r ON r.ordinal = x.ordinal
                        INNER JOIN {{schema}}.jobs j ON j.job_id = x.job_id
                        WHERE j.parent_job_id IS NULL AND j.tenant_id <> r.tenant_id
                    )
                    THROW 50009, 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: one or more rows name a deduplication key another tenant''s job holds.', 1;
            END;

        DECLARE @parents TABLE (
            ordinal INT PRIMARY KEY,
            lineage_root_job_id BIGINT NOT NULL,
            correlation_key VARCHAR(64) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
            tenant_id INT NULL
        );

        INSERT INTO @parents (ordinal, lineage_root_job_id, correlation_key, tenant_id)
        SELECT
            b.ordinal,
            COALESCE(pj.lineage_root_job_id, pj.job_id),
            pj.correlation_key,
            pj.tenant_id
        FROM @p_batch b
        INNER JOIN {{schema}}.jobs pj WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON pj.job_id = b.parent_job_id
        INNER JOIN {{schema}}.jobs_state pr WITH (FORCESEEK) ON pr.job_id = pj.job_id
        WHERE
            b.parent_job_id IS NOT NULL
            AND pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ORDER BY pj.job_id DESC;

        IF (
            SELECT COUNT(*) FROM @parents) < (
            SELECT COUNT(*) FROM @p_batch
            WHERE parent_job_id IS NOT NULL
        )
            BEGIN
                THROW 50002, 'ACTA:ENQ_PARENT_UNAVAILABLE:Enqueue rejected: one or more child rows reference a missing or terminal parent job.', 1;
            END;

        IF
            EXISTS (
                SELECT 1
                FROM @resolved r
                INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                LEFT JOIN @parents p ON p.ordinal = b.ordinal
                WHERE
                    r.def_tenant_req = 10 /* DefinitionTenantRequirementCode.Required */
                    AND r.tenant_id IS NULL
                    AND p.tenant_id IS NULL
            )
            BEGIN
                THROW 50007,
                'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: one or more rows target a definition that requires a tenant and carry none.',
                1;
            END;

        IF
            EXISTS (
                SELECT 1
                FROM @resolved r
                INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                WHERE
                    r.def_tenant_req = 20 /* DefinitionTenantRequirementCode.Forbidden */
                    AND b.tenant_name IS NOT NULL
            )
            BEGIN
                THROW 50008,
                'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: one or more rows target a definition that forbids a tenant and name one.',
                1;
            END;

        IF
            EXISTS (
                SELECT 1
                FROM @resolved r
                INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                INNER JOIN @parents p ON p.ordinal = b.ordinal
                WHERE
                    r.tenant_id IS NOT NULL
                    AND p.tenant_id IS NOT NULL
                    AND r.tenant_id <> p.tenant_id
                    AND b.tenant_override = 0
            )
            BEGIN
                DECLARE
                    @tenant_msg NVARCHAR(2048) = 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: one or more child rows name a'
                    + ' TenantName that differs from the parent tenant without an explicit override.';
                THROW 50009, @tenant_msg, 1;
            END;

        -- A child may not wait behind an unfinished ancestor in its own lane: the ancestor waits for it.
        DECLARE @ancestor_lane_hits INT = 0;
        IF
            EXISTS (
                SELECT 1
                FROM @resolved r
                INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                WHERE
                    r.lane_id IS NOT NULL
                    AND b.parent_job_id IS NOT NULL
            )
            BEGIN
                WITH ancestors AS (
                    SELECT r.lane_id, a.job_id, a.parent_job_id
                    FROM @resolved r
                    INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                    INNER JOIN {{schema}}.jobs a WITH (FORCESEEK) ON a.job_id = b.parent_job_id
                    WHERE r.lane_id IS NOT NULL
                    UNION ALL
                    SELECT c.lane_id, a.job_id, a.parent_job_id
                    FROM {{schema}}.jobs a WITH (FORCESEEK)
                    INNER JOIN ancestors c ON a.job_id = c.parent_job_id
                )

                SELECT @ancestor_lane_hits = COUNT(*)
                FROM ancestors c
                INNER JOIN {{schema}}.jobs_state ar WITH (FORCESEEK) ON ar.job_id = c.job_id
                WHERE
                    ar.lane_id = c.lane_id
                    AND ar.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                    )
                -- A lineage has no depth bound, so neither has the walk.
                OPTION (MAXRECURSION 0);
            END;

        IF @ancestor_lane_hits > 0
            BEGIN
                DECLARE
                    @ancestor_msg NVARCHAR(2048) = 'ACTA:ENQ_ANCESTOR_LANE:Enqueue rejected: one or more child rows name the lane of'
                    + ' an unfinished ancestor, so they would wait behind the ancestor that waits for them.';
                THROW 50010, @ancestor_msg, 1;
            END;

        DECLARE @map TABLE (
            job_ref UNIQUEIDENTIFIER PRIMARY KEY,
            job_id BIGINT NOT NULL
        );

        -- An unlaned batch runs the plain inserts; only a laned one pays for batch order and lane heads.
        -- Both jobs_state inserts force nested loops so the foreign-key checks seek their parent rows
        -- (docs/sql-internals.md, "Foreign keys in set-based writes").
        IF @laned = 0
            BEGIN
                INSERT INTO {{schema}}.jobs (
                    job_kind_code, job_ref, lineage_root_job_id, parent_job_id,
                    deduplication_key, correlation_key,
                    namespace_id, definition_id, tenant_id,
                    input_format_id, input,
                    concurrency_key, event_recording_code,
                    created_at_utc
                )
                OUTPUT INSERTED.job_ref, INSERTED.job_id INTO @map (job_ref, job_id)
                SELECT
                    10 /* JobKindCode.Ordinary */,
                    b.job_ref,
                    p.lineage_root_job_id,
                    b.parent_job_id,
                    b.deduplication_key,
                    COALESCE(b.correlation_key, p.correlation_key),
                    r.ns_id,
                    r.def_id,
                    CASE
                        WHEN r.def_tenant_req = 20 /* DefinitionTenantRequirementCode.Forbidden */ THEN NULL
                        ELSE COALESCE(r.tenant_id, p.tenant_id)
                    END,
                    b.input_format_id,
                    b.input,
                    b.concurrency_key,
                    r.def_event_recording,
                    @now
                FROM @p_batch b
                INNER JOIN @resolved r ON r.ordinal = b.ordinal
                LEFT JOIN @parents p ON p.ordinal = b.ordinal
                LEFT JOIN @existing e ON e.ordinal = b.ordinal
                WHERE e.ordinal IS NULL;

                INSERT INTO {{schema}}.jobs_state (
                    job_id, namespace_id, job_status_code, job_priority_code, next_run_at_utc,
                    execution_number, failure_count, retention_until_utc,
                    modified_at_utc, version
                )
                SELECT
                    m.job_id,
                    r.ns_id,
                    10 /* JobStatusCode.Ready */,
                    COALESCE(b.job_priority_code_override, r.def_priority),
                    COALESCE(b.next_run_at_utc, DATEADD(SECOND, COALESCE(b.delay_seconds, 0), @now)),
                    0,
                    0,
                    NULL,
                    @now,
                    0
                FROM @map m
                INNER JOIN @p_batch b ON b.job_ref = m.job_ref
                INNER JOIN @resolved r ON r.ordinal = b.ordinal
                OPTION (LOOP JOIN);
            END
        ELSE
            BEGIN
                INSERT INTO {{schema}}.jobs (
                    job_kind_code, job_ref, lineage_root_job_id, parent_job_id,
                    deduplication_key, correlation_key,
                    namespace_id, definition_id, tenant_id,
                    input_format_id, input,
                    concurrency_key, event_recording_code,
                    created_at_utc
                )
                OUTPUT INSERTED.job_ref, INSERTED.job_id INTO @map (job_ref, job_id)
                SELECT
                    10 /* JobKindCode.Ordinary */,
                    b.job_ref,
                    p.lineage_root_job_id,
                    b.parent_job_id,
                    b.deduplication_key,
                    COALESCE(b.correlation_key, p.correlation_key),
                    r.ns_id,
                    r.def_id,
                    CASE
                        WHEN r.def_tenant_req = 20 /* DefinitionTenantRequirementCode.Forbidden */ THEN NULL
                        ELSE COALESCE(r.tenant_id, p.tenant_id)
                    END,
                    b.input_format_id,
                    b.input,
                    b.concurrency_key,
                    r.def_event_recording,
                    @now
                FROM @p_batch b
                INNER JOIN @resolved r ON r.ordinal = b.ordinal
                LEFT JOIN @parents p ON p.ordinal = b.ordinal
                LEFT JOIN @existing e ON e.ordinal = b.ordinal
                WHERE e.ordinal IS NULL
                -- Identity values follow this order, so job-id order equals the caller's batch order, which
                -- is the order a lane runs in.
                ORDER BY b.ordinal;

                -- A lane's head is its first inserted row, the lowest ordinal the dedup probe kept, and only
                -- when no unfinished member is already there; every other laned row waits as Blocked. The
                -- probe runs under the lane locks, before this batch has any jobs_state row.
                UPDATE r
                SET lane_head = 1
                FROM @resolved r
                INNER JOIN (
                    SELECT MIN(h.ordinal) AS ordinal
                    FROM @resolved h
                    LEFT JOIN @existing x ON x.ordinal = h.ordinal
                    WHERE
                        h.lane_id IS NOT NULL
                        AND x.ordinal IS NULL
                    GROUP BY h.lane_id
                ) f ON f.ordinal = r.ordinal
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.jobs_state x WITH (FORCESEEK)
                    WHERE
                        x.lane_id = r.lane_id
                        AND x.lane_id IS NOT NULL
                        AND x.job_status_code IN (
                            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                            30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                        )
                );

                INSERT INTO {{schema}}.jobs_state (
                    job_id, namespace_id, lane_id, job_status_code, job_priority_code, next_run_at_utc,
                    execution_number, failure_count, retention_until_utc,
                    modified_at_utc, version
                )
                SELECT
                    m.job_id,
                    r.ns_id,
                    r.lane_id,
                    CASE
                        WHEN r.lane_id IS NULL OR r.lane_head = 1 THEN 10 /* JobStatusCode.Ready */
                        ELSE 15 /* JobStatusCode.Blocked */
                    END,
                    COALESCE(b.job_priority_code_override, r.def_priority),
                    COALESCE(b.next_run_at_utc, DATEADD(SECOND, COALESCE(b.delay_seconds, 0), @now)),
                    0,
                    0,
                    NULL,
                    @now,
                    0
                FROM @map m
                INNER JOIN @p_batch b ON b.job_ref = m.job_ref
                INNER JOIN @resolved r ON r.ordinal = b.ordinal
                OPTION (LOOP JOIN);
            END;

        INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
        SELECT
            50 /* EntityCode.Job */,
            m.job_id,
            t.tag_name,
            t.tag_value,
            t.tag_value_search
        FROM @p_tags t
        INNER JOIN @p_batch b ON b.ordinal = t.ordinal
        INNER JOIN @map m ON m.job_ref = b.job_ref;

        SELECT
            b.ordinal,
            COALESCE(m.job_id, e.job_id) AS job_id,
            COALESCE(m.job_ref, e.job_ref) AS job_ref,
            CASE
                WHEN m.job_id IS NOT NULL
                    THEN 1 /* EnqueueJobAction.Inserted */
                ELSE 2 /* EnqueueJobAction.Deduplicated */
            END AS action
        FROM @p_batch b
        LEFT JOIN @map m ON m.job_ref = b.job_ref
        LEFT JOIN @existing e ON e.ordinal = b.ordinal
        ORDER BY b.ordinal;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO


IF NOT EXISTS (SELECT 1 FROM {{schema}}.migrations WHERE name = 'M20261115084424_refuse_cross_tenant_dedup')
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115084424_refuse_cross_tenant_dedup');
GO
