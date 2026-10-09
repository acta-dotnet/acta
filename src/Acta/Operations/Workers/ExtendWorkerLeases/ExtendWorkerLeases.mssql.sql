CREATE OR ALTER PROCEDURE {{schema}}.extend_worker_leases
    @p_leased_by_worker_id INT,
    @p_lease_ttl_seconds INT,
    @p_draining BIT,
    @p_executions NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @new_expiry DATETIME2(7) = DATEADD(SECOND, @p_lease_ttl_seconds, @now);

        UPDATE {{schema}}.workers
        SET
            last_seen_at_utc = @now,
            -- A worker marked Dead through a database outage longer than its dead-after window is alive again the
            -- moment it heartbeats; Stopped stays Stopped.
            status_code = CASE
                WHEN @p_draining = 1 AND status_code IN (10 /* WorkerStatusCode.Active */, 200 /* WorkerStatusCode.Dead */)
                    THEN 80 /* WorkerStatusCode.Draining */
                WHEN status_code = 200 /* WorkerStatusCode.Dead */ THEN 10 /* WorkerStatusCode.Active */
                ELSE status_code
            END,
            modified_at_utc = @now,
            version = version + 1
        WHERE id = @p_leased_by_worker_id;

        /* The held executions arrive as JSON array text, expanded once into a keyed table. A process can hold two
           executions of one job: an attempt still unwinding after the job was claimed again. */
        DECLARE @held TABLE (job_id BIGINT NOT NULL, execution_number INT NOT NULL, PRIMARY KEY (job_id, execution_number));
        INSERT INTO @held (job_id, execution_number)
        SELECT job_id, execution_number
        FROM OPENJSON(@p_executions) WITH (job_id BIGINT '$.job_id', execution_number INT '$.execution_number');

        /* Push the held executions' leases forward. Only a row this worker still leases at the execution the
           process holds is answered, so a row the process lost lapses and recovery takes it. Deliberately no
           version bump: a lease refresh is not a claim-generation change. */
        -- Read the matched ids first so the update locks base rows only, never an index key; start_execution
        -- and complete_execution lock in the other order. See docs/internals/sql-execution-policy.md.
        DECLARE @matched TABLE (job_id BIGINT NOT NULL PRIMARY KEY, execution_number INT NOT NULL);
        INSERT INTO @matched (job_id, execution_number)
        SELECT r.job_id, r.execution_number
        FROM {{schema}}.runtimes r
        INNER JOIN @held h ON h.job_id = r.job_id AND h.execution_number = r.execution_number
        WHERE
            r.leased_by_worker_id = @p_leased_by_worker_id
            AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

        -- A row another transaction holds is skipped and reported unrenewed, so the renewal never waits
        -- (docs/internals/sql-execution-policy.md, "Explicit exceptions and maintenance").
        DECLARE @renewed TABLE (job_id BIGINT NOT NULL PRIMARY KEY);
        UPDATE r
        SET lease_expires_at_utc = @new_expiry
        OUTPUT INSERTED.job_id INTO @renewed (job_id)
        FROM {{schema}}.runtimes r WITH (INDEX(pk_runtimes), FORCESEEK, ROWLOCK, READPAST)
        INNER JOIN @matched m ON m.job_id = r.job_id AND m.execution_number = r.execution_number
        WHERE
            r.leased_by_worker_id = @p_leased_by_worker_id
            AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

        SELECT
            m.job_id,
            m.execution_number,
            CAST(CASE WHEN n.job_id IS NULL THEN 0 ELSE 1 END AS BIT) AS renewed
        FROM @matched m
        LEFT JOIN @renewed n ON n.job_id = m.job_id;

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
