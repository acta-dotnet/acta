-- Lock order: the batch's lanes in id order, then job rows (docs/internals/sql-execution-policy.md,
-- "Lane lock order").
CREATE OR ALTER PROCEDURE {{schema}}.enqueue_batch
    @p_batch {{schema}}.job_enqueue_batch READONLY,
    @p_tag_batch {{schema}}.job_enqueue_tag_batch READONLY
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
            def_audit_level TINYINT NOT NULL,
            def_status TINYINT NOT NULL,
            def_tenant_req TINYINT NOT NULL,
            tenant_id INT NULL,
            lane VARCHAR(128) NULL,
            lane_id BIGINT NULL
        );

        INSERT INTO @resolved (ordinal, ns_id, ns_status, def_id, def_priority, def_audit_level, def_status, def_tenant_req, lane)
        SELECT
            b.ordinal,
            ns.id,
            ns.status_code,
            jd.id,
            jd.priority_code_effective,
            jd.audit_level_code_effective,
            jd.status_code,
            jd.tenant_requirement_code,
            COALESCE(b.lane, jd.lane)
        FROM @p_batch b
        INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name
        INNER JOIN {{schema}}.definitions jd
            ON
                jd.namespace_id = ns.id
                AND jd.name = b.job_name;

        IF (SELECT COUNT(*) FROM @resolved) < (SELECT COUNT(*) FROM @p_batch)
            BEGIN
                DECLARE
                    @route_msg NVARCHAR(2048) = 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown'
                    + ' namespace or job. Has the owning worker run InitializeAsync yet?';
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
                WHERE def_status <> 10 /* JobDefinitionStatusCode.Active */
            )
            BEGIN
                THROW 50003, 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.', 1;
            END;

        IF
            EXISTS (
                SELECT 1 FROM @p_batch
                WHERE tenant_key IS NOT NULL
            )
            BEGIN
                UPDATE r
                SET tenant_id = t.id
                FROM @resolved r
                INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                INNER JOIN {{schema}}.tenants t
                    ON
                        t.tenant_key = b.tenant_key
                        AND t.status_code = 10 /* TenantStatusCode.Active */
                WHERE b.tenant_key IS NOT NULL;

                IF
                    EXISTS (
                        SELECT 1 FROM @p_batch b
                        WHERE
                            b.tenant_key IS NOT NULL
                            AND NOT EXISTS (
                                SELECT 1 FROM {{schema}}.tenants t
                                WHERE t.tenant_key = b.tenant_key
                            )
                    )
                    BEGIN
                        THROW 50004, 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.', 1;
                    END;
                IF
                    EXISTS (
                        SELECT 1 FROM @p_batch b JOIN {{schema}}.tenants t ON t.tenant_key = b.tenant_key
                        WHERE b.tenant_key IS NOT NULL AND t.status_code <> 10 /* TenantStatusCode.Active */
                    )
                    BEGIN
                        THROW 50006, 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.', 1;
                    END;
            END;

        -- The lane rows are the lanes' mutexes, taken before any job row: missing names are inserted in
        -- name order without a range lock, then every lane is locked one row at a time in id order. A
        -- concurrent insert of the same name, or retention deleting a lane before its lock, sends the
        -- loop round again.
        DECLARE @lanes TABLE (
            ns_id INT NOT NULL,
            name VARCHAR(128) NOT NULL,
            id BIGINT NULL,
            PRIMARY KEY (ns_id, name)
        );

        INSERT INTO @lanes (ns_id, name)
        SELECT DISTINCT r.ns_id, r.lane
        FROM @resolved r
        WHERE r.lane IS NOT NULL;

        DECLARE @lanes_pending BIT = CASE WHEN EXISTS (SELECT 1 FROM @lanes) THEN 1 ELSE 0 END;
        DECLARE @lane_cursor BIGINT, @lane_next BIGINT, @lane_locked BIGINT;

        WHILE @lanes_pending = 1
            BEGIN
                SET XACT_ABORT OFF;
                BEGIN TRY
                    INSERT INTO {{schema}}.lanes (namespace_id, name, created_at_utc)
                    SELECT e.ns_id, e.name, @now
                    FROM @lanes e
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM {{schema}}.lanes l
                        WHERE
                            l.namespace_id = e.ns_id
                            AND l.name = e.name
                    )
                    ORDER BY e.name;
                END TRY
                BEGIN CATCH
                    SET XACT_ABORT ON;
                    IF ERROR_NUMBER() NOT IN (2601, 2627)
                        THROW;
                END CATCH;
                SET XACT_ABORT ON;

                UPDATE e
                SET id = l.id
                FROM @lanes e
                LEFT JOIN {{schema}}.lanes l
                    ON
                        l.namespace_id = e.ns_id
                        AND l.name = e.name;

                SET @lanes_pending = CASE WHEN EXISTS (SELECT 1 FROM @lanes WHERE id IS NULL) THEN 1 ELSE 0 END;
                SET @lane_cursor = 0;
                WHILE @lanes_pending = 0
                    BEGIN
                        SET @lane_next = NULL;
                        SELECT TOP (1) @lane_next = e.id
                        FROM @lanes e
                        WHERE e.id > @lane_cursor
                        ORDER BY e.id;

                        IF @lane_next IS NULL
                            BREAK;

                        SET @lane_locked = NULL;
                        SELECT @lane_locked = l.id
                        FROM {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK)
                        WHERE l.id = @lane_next;

                        IF @lane_locked IS NULL
                            SET @lanes_pending = 1;
                        SET @lane_cursor = @lane_next;
                    END;
            END;

        UPDATE r
        SET lane_id = e.id
        FROM @resolved r
        INNER JOIN @lanes e
            ON
                e.ns_id = r.ns_id
                AND e.name = r.lane;

        DECLARE @existing TABLE (
            ordinal INT PRIMARY KEY,
            id BIGINT NOT NULL,
            job_ref UNIQUEIDENTIFIER NOT NULL
        );

        -- Skip when no row has a key: the dedup probes' HOLDLOCK range locks (held to commit) would
        -- otherwise serialize every concurrent keyless enqueue on the namespace.
        IF
            EXISTS (
                SELECT 1 FROM @p_batch
                WHERE deduplication_key IS NOT NULL
            )
            BEGIN
                INSERT INTO @existing (ordinal, id, job_ref)
                SELECT
                    b.ordinal,
                    j.id,
                    j.job_ref
                FROM @p_batch b
                INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, HOLDLOCK)
                    ON
                        j.parent_id = b.parent_id
                        AND j.deduplication_key = b.deduplication_key
                WHERE
                    b.deduplication_key IS NOT NULL
                    AND b.parent_id IS NOT NULL;

                INSERT INTO @existing (ordinal, id, job_ref)
                SELECT
                    b.ordinal,
                    j.id,
                    j.job_ref
                FROM @p_batch b
                INNER JOIN @resolved r ON r.ordinal = b.ordinal
                INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, HOLDLOCK)
                    ON
                        j.namespace_id = r.ns_id
                        AND j.deduplication_key = b.deduplication_key
                        AND j.parent_id IS NULL
                WHERE
                    b.deduplication_key IS NOT NULL
                    AND b.parent_id IS NULL;
            END;

        DECLARE @parents TABLE (
            ordinal INT PRIMARY KEY,
            lineage_root_id BIGINT NOT NULL,
            correlation_key VARCHAR(64) NULL,
            tenant_id INT NULL
        );

        INSERT INTO @parents (ordinal, lineage_root_id, correlation_key, tenant_id)
        SELECT
            b.ordinal,
            COALESCE(pj.lineage_root_id, pj.id),
            pj.correlation_key,
            pj.tenant_id
        FROM @p_batch b
        INNER JOIN {{schema}}.jobs pj WITH (UPDLOCK, ROWLOCK) ON pj.id = b.parent_id
        INNER JOIN {{schema}}.runtimes pr ON pr.job_id = pj.id
        WHERE
            b.parent_id IS NOT NULL
            AND pr.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ORDER BY pj.id DESC;

        IF (
            SELECT COUNT(*) FROM @parents) < (
            SELECT COUNT(*) FROM @p_batch
            WHERE parent_id IS NOT NULL
        )
            BEGIN
                THROW 50002, 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.', 1;
            END;

        IF
            EXISTS (
                SELECT 1
                FROM @resolved r
                INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                LEFT JOIN @parents p ON p.ordinal = b.ordinal
                WHERE
                    r.def_tenant_req = 10 /* JobTenantRequirementCode.Required */
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
                    r.def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */
                    AND b.tenant_key IS NOT NULL
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
                    + ' TenantKey that differs from the parent tenant without an explicit override.';
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
                    AND b.parent_id IS NOT NULL
            )
            BEGIN
                WITH ancestors AS (
                    SELECT r.lane_id, a.id, a.parent_id
                    FROM @resolved r
                    INNER JOIN @p_batch b ON b.ordinal = r.ordinal
                    INNER JOIN {{schema}}.jobs a ON a.id = b.parent_id
                    WHERE r.lane_id IS NOT NULL
                    UNION ALL
                    SELECT c.lane_id, a.id, a.parent_id
                    FROM {{schema}}.jobs a
                    INNER JOIN ancestors c ON a.id = c.parent_id
                )

                SELECT @ancestor_lane_hits = COUNT(*)
                FROM ancestors c
                INNER JOIN {{schema}}.runtimes ar ON ar.job_id = c.id
                WHERE
                    ar.lane_id = c.lane_id
                    AND ar.status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                    );
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
            id BIGINT NOT NULL
        );

        INSERT INTO {{schema}}.jobs (
            job_ref, lineage_root_id, parent_id,
            deduplication_key, correlation_key,
            namespace_id, definition_id, tenant_id,
            input_format_id, input,
            concurrency_key, audit_level_code,
            created_at_utc
        )
        OUTPUT INSERTED.job_ref, INSERTED.id INTO @map (job_ref, id)
        SELECT
            b.job_ref,
            p.lineage_root_id,
            b.parent_id,
            b.deduplication_key,
            COALESCE(b.correlation_key, p.correlation_key),
            r.ns_id,
            r.def_id,
            CASE
                WHEN r.def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */ THEN NULL
                ELSE COALESCE(r.tenant_id, p.tenant_id)
            END,
            b.input_format_id,
            b.input,
            b.concurrency_key,
            r.def_audit_level,
            @now
        FROM @p_batch b
        INNER JOIN @resolved r ON r.ordinal = b.ordinal
        LEFT JOIN @parents p ON p.ordinal = b.ordinal
        LEFT JOIN @existing e ON e.ordinal = b.ordinal
        WHERE e.ordinal IS NULL
        -- Identity values follow this order, so job-id order equals the caller's batch order, which is
        -- the order a lane runs in.
        ORDER BY b.ordinal;

        -- A laned row enters Ready only as the first inserted row of its lane with no unfinished member
        -- already there; every later one waits as Blocked. The existence probe is spooled ahead of the
        -- insert, so this batch's own rows are ranked by the window instead.
        INSERT INTO {{schema}}.runtimes (
            job_id, namespace_id, lane_id, status_code, priority_code, next_run_at_utc,
            execution_number, failure_count, retention_until_utc,
            modified_at_utc, version
        )
        SELECT
            m.id,
            r.ns_id,
            r.lane_id,
            CASE
                WHEN
                    r.lane_id IS NOT NULL
                    AND (
                        ROW_NUMBER() OVER (PARTITION BY r.lane_id ORDER BY m.id) > 1
                        OR EXISTS (
                            SELECT 1
                            FROM {{schema}}.runtimes x
                            WHERE
                                x.lane_id = r.lane_id
                                AND x.lane_id IS NOT NULL
                                AND x.status_code IN (
                                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                                    30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                                )
                        )
                    )
                    THEN 15 /* JobStatusCode.Blocked */
                ELSE 10 /* JobStatusCode.Ready */
            END,
            COALESCE(b.priority_override, r.def_priority),
            COALESCE(b.next_run_at_utc, DATEADD(SECOND, COALESCE(b.delay_seconds, 0), @now)),
            0,
            0,
            NULL,
            @now,
            0
        FROM @map m
        INNER JOIN @p_batch b ON b.job_ref = m.job_ref
        INNER JOIN @resolved r ON r.ordinal = b.ordinal;

        INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
        SELECT
            50 /* TagScopeCode.Job */,
            m.id,
            r.ns_id,
            t.name,
            t.value,
            t.value_search
        FROM @p_tag_batch t
        INNER JOIN @p_batch b ON b.ordinal = t.ordinal
        INNER JOIN @map m ON m.job_ref = b.job_ref
        INNER JOIN @resolved r ON r.ordinal = t.ordinal;

        SELECT
            b.ordinal,
            COALESCE(m.id, e.id) AS job_id,
            COALESCE(m.job_ref, e.job_ref) AS job_ref,
            CASE
                WHEN m.id IS NOT NULL
                    THEN 1 /* JobEnqueueAction.Inserted */
                ELSE 2 /* JobEnqueueAction.Deduplicated */
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
