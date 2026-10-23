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
        @lineage_root_job_id BIGINT, @parent_job_id BIGINT;

    /* Lock the jobs row too: child enqueue locks jobs (not jobs_state), and under RCSI the
       child guard below only serializes if we hold the same resource. */
    SELECT
        @from_status = r.job_status_code,
        @namespace_id = j.namespace_id,
        @definition_id = j.definition_id,
        @tenant_id = j.tenant_id,
        @lineage_root_job_id = j.lineage_root_job_id,
        @parent_job_id = j.parent_job_id
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, ROWLOCK) ON j.job_id = r.job_id
    WHERE r.job_id = @p_job_id;

    IF @from_status IS NULL
        BEGIN

            SELECT
                CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                CAST(NULL AS TINYINT) AS status;
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
                @from_status AS status;
            GOTO Finish;
        END;

    -- parent_job_id carries no DB FK/cascade; purging a job that has child jobs would orphan the child's
    -- lineage, so reject. A completed child of a live parent is kept too: the parent's replay
    -- dedupes onto this row and reads its result, so purging it would run the child again.
    IF
        EXISTS (
            SELECT 1 FROM {{schema}}.jobs c
            WHERE c.parent_job_id = @p_job_id
        )
        OR EXISTS (
            SELECT 1 FROM {{schema}}.jobs_state p
            WHERE p.job_id = @parent_job_id
                AND p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status;
            GOTO Finish;
        END;

    /* The alert projector still owes this job an event, whose raise reads the job row: an event it reads, of an
       alert-enabled definition, past the floor. The purge is refused. */
    IF
        EXISTS (
            SELECT 1
            FROM {{schema}}.events e
            INNER JOIN {{schema}}.definitions d ON d.definition_id = e.definition_id
            WHERE
                e.entity_code = 50 /* EntityCode.Job */
                AND e.entity_id = @p_job_id
                AND e.created_at_utc >= @p_alert_floor_utc
                AND (e.created_at_utc > @p_alert_floor_utc OR e.event_id > @p_alert_floor_event_id)
                AND (
                    e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                    OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
                )
                AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
        )
        THROW 50008, 'ACTA:PURGE_BLOCKED:purge_job: the alert projector has not processed this job''s events', 1;

    DECLARE @alert_ids TABLE (
        alert_id BIGINT PRIMARY KEY,
        namespace_id INT NOT NULL,
        deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL
    );

    DECLARE @schedules_locked INT = (
        SELECT COUNT(*) FROM {{schema}}.schedules WITH (UPDLOCK, ROWLOCK, FORCESEEK)
        WHERE job_id = @p_job_id
    );
    DECLARE @cursors_locked INT = (
        SELECT COUNT(*) FROM {{schema}}.schedules_state WITH (UPDLOCK, ROWLOCK, FORCESEEK)
        WHERE job_id = @p_job_id
    );
    -- The job's alerts are read through ix_alerts_job without locks, which the held job row makes safe, then
    -- each identity row is locked through its identity key before the row and the delete locks the rest
    -- (docs/sql-execution-policy.md, "Alert lock order").
    INSERT @alert_ids (alert_id, namespace_id, deduplication_key)
    SELECT alert_id, namespace_id, deduplication_key FROM {{schema}}.alerts WITH (FORCESEEK (ix_alerts_job (job_id)))
    WHERE job_id = @p_job_id;
    DECLARE @identity_locked INT = (
        SELECT COUNT(*)
        FROM @alert_ids i
        INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, FORCESEEK (ix_alerts_deduplication_key_identity (namespace_id, deduplication_key)))
            ON
                a.namespace_id = i.namespace_id
                AND a.deduplication_key = i.deduplication_key
                AND a.alert_id = i.alert_id
        WHERE i.deduplication_key IS NOT NULL
    );

    DELETE FROM {{schema}}.tags
    WHERE entity_code = 50 /* EntityCode.Job */ AND entity_id = @p_job_id;

    DELETE e FROM {{schema}}.events e WITH (FORCESEEK)
    WHERE e.entity_code = 50 /* EntityCode.Job */ AND e.entity_id = @p_job_id;
    DELETE a FROM {{schema}}.alerts a WITH (FORCESEEK) INNER JOIN @alert_ids i ON i.alert_id = a.alert_id
    WHERE a.job_id = @p_job_id;
    DELETE c FROM {{schema}}.checkpoints c WITH (FORCESEEK)
    WHERE c.job_id = @p_job_id;
    DELETE t FROM {{schema}}.steps t WITH (FORCESEEK)
    WHERE t.job_id = @p_job_id;
    DELETE s FROM {{schema}}.schedules s WITH (FORCESEEK)
    WHERE s.job_id = @p_job_id;
    DELETE st FROM {{schema}}.schedules_state st WITH (FORCESEEK)
    WHERE st.job_id = @p_job_id;
    DELETE FROM {{schema}}.jobs
    WHERE job_id = @p_job_id;
    DELETE FROM {{schema}}.jobs_state
    WHERE job_id = @p_job_id;

    -- The tombstone keeps the deleted job's id as its subject; its details snapshot what no surviving event carries.
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        event_message, details_format_id, details
    )
    VALUES (
        75 /* EventCode.JobPurged */, @now, @namespace_id,
        50 /* EntityCode.Job */, @p_job_id,
        @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
        @p_event_message, @p_details_format_id, @p_details
    );

    SELECT
        CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
        CAST(NULL AS TINYINT) AS status;

Finish:

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
