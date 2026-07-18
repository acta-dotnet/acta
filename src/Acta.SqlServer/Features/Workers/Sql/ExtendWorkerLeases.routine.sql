CREATE OR ALTER PROCEDURE {{schema}}.extend_worker_leases
    @p_leased_by_worker_id INT,
    @p_lease_ttl_seconds   INT,
    @p_draining            BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE {{schema}}.workers
       SET last_seen_at_utc = @now,
           status_code      = CASE WHEN @p_draining = 1 AND status_code = 10 /* WorkerStatusCode.Active */
                                   THEN 80 /* WorkerStatusCode.Draining */ ELSE status_code END,
           modified_at_utc  = @now,
           version          = version + 1
     WHERE id = @p_leased_by_worker_id;

    /* Push every in-flight execution lease forward. Deliberately no version bump: a lease refresh
       is not a claim-generation change, so a buffered claim still passes the start CAS. */
    UPDATE {{schema}}.runtimes
       SET lease_expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, @now)
    OUTPUT inserted.job_id
     WHERE leased_by_worker_id = @p_leased_by_worker_id
       AND status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */);
END;
GO
