SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id INT,
        @definition_id INT, @tenant_id INT,
        @job_ref UNIQUEIDENTIFIER, @job_name VARCHAR(128), @parent_id BIGINT;

    /* Lock the jobs row too: child enqueue locks jobs (not runtimes), and under RCSI the
       child guard below only serializes if we hold the same resource. */
    SELECT
        @from_status = r.status_code,
        @namespace_id = j.namespace_id,
        @definition_id = j.definition_id,
        @tenant_id = j.tenant_id,
        @job_ref = j.job_ref,
        @job_name = d.name,
        @parent_id = j.parent_id
    FROM {{schema}}.runtimes r WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, ROWLOCK) ON j.id = r.job_id
    INNER JOIN {{schema}}.definitions d ON d.id = j.definition_id
    WHERE r.job_id = @p_id;

    IF @from_status IS NULL
        BEGIN

            SELECT
                CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                CAST(NULL AS TINYINT) AS status_code;
            GOTO Finish;
        END;

    IF
        @from_status NOT IN (
            100 /* JobStatusCode.Succeeded */,
            200 /* JobStatusCode.Failed */,
            220 /* JobStatusCode.Cancelled */
        )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status_code;
            GOTO Finish;
        END;

    -- parent_id carries no DB FK/cascade; purging a job that has child jobs would orphan the child's
    -- lineage, so reject. A completed child of a live parent is kept too: the parent's replay
    -- dedupes onto this row and reads its result, so purging it would run the child again.
    IF
        EXISTS (
            SELECT 1 FROM {{schema}}.jobs c
            WHERE c.parent_id = @p_id
        )
        OR EXISTS (
            SELECT 1 FROM {{schema}}.runtimes p
            WHERE p.job_id = @parent_id
                AND p.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status_code;
            GOTO Finish;
        END;

    DECLARE @schedule_ids TABLE (id BIGINT PRIMARY KEY);
    DECLARE @alert_ids TABLE (
        id BIGINT PRIMARY KEY,
        namespace_id INT NOT NULL,
        deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL
    );

    INSERT @schedule_ids SELECT id FROM {{schema}}.schedules WITH (UPDLOCK, ROWLOCK, FORCESEEK)
    WHERE job_id = @p_id;
    -- The job's alerts are read through ix_alerts_job without locks, which the held job row makes safe, then
    -- each identity row is locked through its identity key before the row and the delete locks the rest
    -- (docs/internals/sql-execution-policy.md, "Alert lock order").
    INSERT @alert_ids (id, namespace_id, deduplication_key)
    SELECT id, namespace_id, deduplication_key FROM {{schema}}.alerts WITH (FORCESEEK (ix_alerts_job (job_id)))
    WHERE job_id = @p_id;
    DECLARE @identity_locked INT = (
        SELECT COUNT(*)
        FROM @alert_ids i
        INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, FORCESEEK (ix_alerts_deduplication_key_identity (namespace_id, deduplication_key)))
            ON
                a.namespace_id = i.namespace_id
                AND a.deduplication_key = i.deduplication_key
                AND a.id = i.id
        WHERE i.deduplication_key IS NOT NULL
    );

    DELETE FROM {{schema}}.tags
    WHERE scope_code = 50 /* TagScopeCode.Job */ AND scope_id = @p_id;

    DELETE e FROM {{schema}}.events e WITH (FORCESEEK)
    WHERE e.job_id = @p_id;
    DELETE a FROM {{schema}}.alerts a WITH (FORCESEEK) INNER JOIN @alert_ids i ON i.id = a.id
    WHERE a.job_id = @p_id;
    -- The schedules go by key first: the jobs delete's cascade runs serializable and, unhinted, walks
    -- pk_schedules under range locks.
    DELETE s FROM {{schema}}.schedules s WITH (FORCESEEK) INNER JOIN @schedule_ids i ON i.id = s.id;
    DELETE FROM {{schema}}.jobs
    WHERE id = @p_id
    OPTION (LOOP JOIN);
    DELETE FROM {{schema}}.runtimes
    WHERE job_id = @p_id;

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        actor_code, actor,
        job_id, job_ref, execution_number,
        lineage_root_id, definition_id, tenant_id,
        worker_id,
        from_status_code, to_status_code,
        execution_status_code, duration_ms,
        reason_code, reason_message
    )
    VALUES (
        75 /* EventCode.JobPurged */, @now, @namespace_id,
        @p_actor_code, @p_actor,
        NULL, NULL, NULL,
        NULL, @definition_id, @tenant_id,
        NULL,
        @from_status, NULL,
        NULL, NULL,
        @p_reason_code, CONCAT('purged ', LOWER(CONVERT(VARCHAR(36), @job_ref)), ' (', @job_name, ')')
    );

    SELECT
        CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
        CAST(NULL AS TINYINT) AS status_code;

Finish:

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
