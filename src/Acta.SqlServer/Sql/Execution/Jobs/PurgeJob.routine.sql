CREATE OR ALTER PROCEDURE {{schema}}.purge_job
    @p_id BIGINT,
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_code TINYINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id SMALLINT,
        @definition_id INT, @tenant_id INT,
        @job_ref UNIQUEIDENTIFIER, @job_name VARCHAR(128);

    BEGIN TRY
        BEGIN TRANSACTION;

        /* Lock the jobs row too: child enqueue locks jobs (not runtimes), and under RCSI the
           child guard below only serializes if we hold the same resource. */
        SELECT
            @from_status = r.status_code,
            @namespace_id = j.namespace_id,
            @definition_id = j.definition_id,
            @tenant_id = j.tenant_id,
            @job_ref = j.job_ref,
            @job_name = d.name
        FROM {{schema}}.runtimes r WITH (UPDLOCK, ROWLOCK)
        INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, ROWLOCK) ON j.id = r.job_id
        INNER JOIN {{schema}}.definitions d ON d.id = j.definition_id
        WHERE r.job_id = @p_id;

        IF @from_status IS NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(2 /* JobControlAction.NotFound */ AS TINYINT) AS action,
                    CAST(NULL AS TINYINT) AS status_code;
                RETURN;
            END;

        IF
            @from_status NOT IN (
                100 /* JobStatusCode.Succeeded */,
                200 /* JobStatusCode.Failed */,
                220 /* JobStatusCode.Cancelled */
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(3 /* JobControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status_code;
                RETURN;
            END;

        -- parent_id carries no DB FK/cascade; purging a job that has child jobs would orphan the child's
        -- lineage, so reject.
        IF
            EXISTS (
                SELECT 1 FROM {{schema}}.jobs c
                WHERE c.parent_id = @p_id
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(3 /* JobControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status_code;
                RETURN;
            END;

        DECLARE @schedule_ids TABLE (id BIGINT PRIMARY KEY);
        DECLARE @alert_ids TABLE (id BIGINT PRIMARY KEY);
        DECLARE @event_ids TABLE (id BIGINT PRIMARY KEY);

        INSERT @schedule_ids SELECT id FROM {{schema}}.schedules WITH (UPDLOCK, ROWLOCK)
        WHERE job_id = @p_id;
        INSERT @alert_ids SELECT id FROM {{schema}}.alerts WITH (UPDLOCK, ROWLOCK)
        WHERE job_id = @p_id;
        INSERT @event_ids SELECT id FROM {{schema}}.events WITH (UPDLOCK, ROWLOCK)
        WHERE job_id = @p_id;

        DELETE FROM {{schema}}.tags
        WHERE
            (scope_code = 50 /* TagScopeCode.Job */ AND scope_id = @p_id)
            OR (scope_code = 60 /* TagScopeCode.Schedule */ AND scope_id IN (SELECT id FROM @schedule_ids))
            OR (scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM @alert_ids))
            OR (scope_code = 90 /* TagScopeCode.Event */ AND scope_id IN (SELECT id FROM @event_ids));

        DELETE FROM {{schema}}.events
        WHERE job_id = @p_id;
        DELETE FROM {{schema}}.alerts
        WHERE job_id = @p_id;
        DELETE FROM {{schema}}.jobs
        WHERE id = @p_id;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message
        )
        VALUES (
            75 /* JobEventCode.JobPurged */, @now, @namespace_id,
            @p_actor_code, @p_actor_key,
            NULL, NULL, NULL,
            NULL, @definition_id, @tenant_id,
            NULL,
            @from_status, NULL,
            NULL, NULL,
            @p_reason_code, CONCAT('purged ', LOWER(CONVERT(VARCHAR(36), @job_ref)), ' (', @job_name, ')')
        );

        COMMIT TRANSACTION;
        SELECT
            CAST(1 /* JobControlAction.Applied */ AS TINYINT) AS action,
            CAST(NULL AS TINYINT) AS status_code;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
            BEGIN
                ROLLBACK TRANSACTION;
            END;

        THROW;
    END CATCH;
END;
GO
