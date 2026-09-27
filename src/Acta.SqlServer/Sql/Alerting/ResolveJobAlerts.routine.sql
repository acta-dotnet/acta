CREATE OR ALTER PROCEDURE {{schema}}.resolve_job_alerts
    @p_namespace_id INT,
    @p_job_id BIGINT,
    @p_source_event_id BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        -- The job row first, then its alert rows (docs/internals/sql-execution-policy.md, "Alert lock order").
        -- alerts has no job_id index, so the candidates are read without locks and each is then locked by
        -- primary key and re-checked; the held job row keeps a raise from adding one meanwhile.
        DECLARE @job_locked BIGINT;
        SELECT @job_locked = j.id
        FROM {{schema}}.jobs j WITH (UPDLOCK, ROWLOCK)
        WHERE j.id = @p_job_id;

        DECLARE @candidates TABLE (id BIGINT NOT NULL PRIMARY KEY);
        INSERT INTO @candidates (id)
        SELECT a.id
        FROM {{schema}}.alerts a
        WHERE
            a.namespace_id = @p_namespace_id
            AND a.job_id = @p_job_id
            AND a.resolved_at_utc IS NULL;

        UPDATE a
        SET
            resolved_at_utc = SYSUTCDATETIME(),
            last_projected_event_id = @p_source_event_id,
            /* Closing the incident settles its delivery too: a notification queued for a condition that has
               cleared is cancelled rather than sent, which is what Suppressed already means. An already-settled
               row keeps its status: it records what actually happened to the send, and a resolve does not edit it. */
            delivery_status_code = CASE
                WHEN a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
                    THEN 30 /* AlertDeliveryStatusCode.Suppressed */
                ELSE a.delivery_status_code
            END,
            retry_after_utc = NULL,
            modified_at_utc = SYSUTCDATETIME(),
            version = a.version + 1
        FROM {{schema}}.alerts a WITH (FORCESEEK)
        INNER JOIN @candidates c ON c.id = a.id
        WHERE
            a.namespace_id = @p_namespace_id
            AND a.job_id = @p_job_id
            AND a.origin_code = 10 /* AlertOriginCode.Automatic */
            AND a.kind_code IN (
                10 /* AlertKindCode.FirstFailure */, 20 /* AlertKindCode.ThresholdReached */, 30 /* AlertKindCode.FinalFailure */
            )
            AND a.resolved_at_utc IS NULL
            AND (a.last_projected_event_id IS NULL OR a.last_projected_event_id < @p_source_event_id);

        SELECT @@ROWCOUNT AS resolved_count;

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
