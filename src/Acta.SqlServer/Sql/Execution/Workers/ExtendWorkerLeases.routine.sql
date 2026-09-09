CREATE OR ALTER PROCEDURE {{schema}}.extend_worker_leases
    @p_leased_by_worker_id INT,
    @p_lease_ttl_seconds INT,
    @p_draining BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

        UPDATE {{schema}}.workers
        SET
            last_seen_at_utc = @now,
            status_code = CASE
                WHEN @p_draining = 1 AND status_code = 10 /* WorkerStatusCode.Active */
                    THEN 80 /* WorkerStatusCode.Draining */
                ELSE status_code
            END,
            modified_at_utc = @now,
            version = version + 1
        WHERE id = @p_leased_by_worker_id;

        /* Push every in-flight execution lease forward. Deliberately no version bump: a lease refresh
           is not a claim-generation change, so a buffered claim still passes the start CAS. */
        DECLARE @inflight TABLE (job_id BIGINT NOT NULL PRIMARY KEY);

        -- Read the ids first so the update locks base rows only, never an index key; start_execution
        -- and complete_execution lock in the other order. See docs/internals/sql-execution-policy.md.
        -- The read is lock-free under RCSI, so taking it through the heartbeat index is safe.
        INSERT INTO @inflight (job_id)
        SELECT job_id
        FROM {{schema}}.runtimes
        WHERE
            leased_by_worker_id = @p_leased_by_worker_id
            AND status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

        UPDATE r
        SET lease_expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, @now)
        OUTPUT INSERTED.job_id
        FROM {{schema}}.runtimes r WITH (INDEX(pk_runtimes), FORCESEEK, ROWLOCK)
        INNER JOIN @inflight i ON i.job_id = r.job_id
        WHERE
            r.leased_by_worker_id = @p_leased_by_worker_id
            AND r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);

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
