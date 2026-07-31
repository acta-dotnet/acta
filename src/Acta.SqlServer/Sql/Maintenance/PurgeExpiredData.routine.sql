CREATE OR ALTER PROCEDURE {{schema}}.purge_expired_data
    @p_namespace_id         SMALLINT,
    @p_events_retention_days    INT,
    @p_alert_retention_days     INT,
    @p_worker_retention_seconds INT,
    @p_batch_size               INT,
    @p_max_iterations           INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @jobs_deleted INT = 0, @events_deleted INT = 0, @alerts_deleted INT = 0, @workers_deleted INT = 0, @locks_deleted INT = 0;
    DECLARE @rows INT, @iter INT;
    DECLARE @del TABLE (id BIGINT NOT NULL);
    DECLARE @schedule_del TABLE (id BIGINT NOT NULL);

    SET @rows = 1;
    SET @iter = 0;
    WHILE @rows > 0 AND @iter < @p_max_iterations
    BEGIN
        DELETE @del;
        BEGIN TRANSACTION;
        INSERT INTO @del (id)
        SELECT TOP (@p_batch_size) j.id
          FROM {{schema}}.runtimes r WITH (UPDLOCK, READPAST)
          INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, READPAST) ON j.id = r.job_id
         WHERE r.namespace_id   = @p_namespace_id
           AND r.status_code        IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
           AND r.retention_until_utc IS NOT NULL
           AND r.retention_until_utc <= @now
         ORDER BY r.retention_until_utc, r.job_id;

        DELETE @schedule_del;
        INSERT INTO @schedule_del(id)
        SELECT s.id FROM {{schema}}.schedules s WITH (UPDLOCK)
         WHERE s.job_id IN (SELECT id FROM @del);

        DELETE FROM {{schema}}.tags
         WHERE (scope_code = 50 /* TagScopeCode.Job */ AND scope_id IN (SELECT id FROM @del))
            OR (scope_code = 60 /* TagScopeCode.Schedule */ AND scope_id IN (SELECT id FROM @schedule_del));

        DELETE FROM {{schema}}.checkpoints WHERE job_id IN (SELECT id FROM @del);
        DELETE FROM {{schema}}.runtimes    WHERE job_id IN (SELECT id FROM @del);
        DELETE FROM {{schema}}.steps       WHERE job_id IN (SELECT id FROM @del);
        DELETE FROM {{schema}}.results     WHERE job_id IN (SELECT id FROM @del);
        DELETE FROM {{schema}}.jobs WHERE id IN (SELECT id FROM @del);
        SET @rows = (SELECT COUNT(*) FROM @del);
        COMMIT TRANSACTION;
        SET @jobs_deleted = @jobs_deleted + @rows;
        SET @iter = @iter + 1;
    END;

    DECLARE @events_cutoff DATETIME2(7) = DATEADD(DAY, -@p_events_retention_days, @now);
    SET @rows = 1;
    SET @iter = 0;
    WHILE @rows > 0 AND @iter < @p_max_iterations
    BEGIN
        DELETE @del;
        BEGIN TRANSACTION;
        INSERT INTO @del(id)
            SELECT TOP (@p_batch_size) id
              FROM {{schema}}.events WITH (UPDLOCK, READPAST)
             WHERE namespace_id = @p_namespace_id
               AND created_at_utc   <= @events_cutoff
             ORDER BY created_at_utc, id;
        DELETE FROM {{schema}}.tags WHERE scope_code = 90 /* TagScopeCode.Event */ AND scope_id IN (SELECT id FROM @del);
        DELETE e FROM {{schema}}.events e INNER JOIN @del d ON d.id = e.id;
        SET @rows = (SELECT COUNT(*) FROM @del);
        COMMIT TRANSACTION;
        SET @events_deleted = @events_deleted + @rows;
        SET @iter = @iter + 1;
    END;

    DECLARE @alerts_cutoff DATETIME2(7) = DATEADD(DAY, -@p_alert_retention_days, @now);
    SET @rows = 1;
    SET @iter = 0;
    WHILE @rows > 0 AND @iter < @p_max_iterations
    BEGIN
        DELETE @del;
        BEGIN TRANSACTION;
        INSERT INTO @del(id)
            SELECT TOP (@p_batch_size) id
              FROM {{schema}}.alerts WITH (UPDLOCK, READPAST)
             WHERE namespace_id    = @p_namespace_id
               AND created_at_utc       <= @alerts_cutoff
               AND delivery_status_code IN (30 /* AlertDeliveryStatusCode.Suppressed */, 100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
             ORDER BY created_at_utc, id;
        DELETE FROM {{schema}}.tags WHERE scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM @del);
        DELETE a FROM {{schema}}.alerts a INNER JOIN @del d ON d.id = a.id;
        SET @rows = (SELECT COUNT(*) FROM @del);
        COMMIT TRANSACTION;
        SET @alerts_deleted = @alerts_deleted + @rows;
        SET @iter = @iter + 1;
    END;

    DECLARE @worker_cutoff DATETIME2(7) = DATEADD(SECOND, -@p_worker_retention_seconds, @now);
    SET @rows = 1;
    SET @iter = 0;
    WHILE @rows > 0 AND @iter < @p_max_iterations
    BEGIN
        DELETE @del;
        BEGIN TRANSACTION;
        INSERT INTO @del(id)
            SELECT TOP (@p_batch_size) id
              FROM {{schema}}.workers WITH (UPDLOCK, READPAST)
             WHERE namespace_id = @p_namespace_id
               AND status_code      = 200 /* WorkerStatusCode.Dead */
               AND last_seen_at_utc <= @worker_cutoff
             ORDER BY last_seen_at_utc, id;
        DELETE FROM {{schema}}.tags WHERE scope_code = 70 /* TagScopeCode.Worker */ AND scope_id IN (SELECT id FROM @del);
        DELETE w FROM {{schema}}.workers w INNER JOIN @del d ON d.id = w.id;
        SET @rows = (SELECT COUNT(*) FROM @del);
        COMMIT TRANSACTION;
        SET @workers_deleted = @workers_deleted + @rows;
        SET @iter = @iter + 1;
    END;

    SET @rows = 1;
    SET @iter = 0;
    WHILE @rows > 0 AND @iter < @p_max_iterations
    BEGIN
        WITH expired AS (
            SELECT TOP (@p_batch_size) *
              FROM {{schema}}.leases WITH (ROWLOCK, READPAST)
             WHERE kind_code = 10 /* LeaseKindCode.Lock */
               AND expires_at_utc <= @now
             ORDER BY expires_at_utc
        )
        DELETE FROM expired;
        SET @rows = @@ROWCOUNT;
        SET @locks_deleted = @locks_deleted + @rows;
        SET @iter = @iter + 1;
    END;

    SELECT @jobs_deleted AS jobs_deleted, @events_deleted AS events_deleted, @alerts_deleted AS alerts_deleted, @workers_deleted AS workers_deleted, @locks_deleted AS locks_deleted;
END;
GO
