SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @gone TABLE (worker_id INT NOT NULL PRIMARY KEY);
    INSERT INTO @gone (worker_id)
    SELECT DISTINCT CAST(j.value AS INT) FROM OPENJSON (@p_worker_ids) j;

    -- A released worker still Active or Draining turns Dead, as the dead-worker sweep would turn it.
    DECLARE @dead TABLE (worker_id INT NOT NULL PRIMARY KEY, namespace_id INT NOT NULL);

    UPDATE w
    SET
        worker_status_code = 200 /* WorkerStatusCode.Dead */,
        modified_at_utc = SYSUTCDATETIME(),
        version = w.version + 1
    OUTPUT INSERTED.worker_id, INSERTED.namespace_id INTO @dead (worker_id, namespace_id)
    FROM {{schema}}.workers w WITH (ROWLOCK)
    INNER JOIN @gone g ON g.worker_id = w.worker_id
    WHERE w.worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        worker_id,
        entity_code,
        entity_id)
    SELECT
        46 /* EventCode.WorkerDied */,
        SYSUTCDATETIME(),
        d.namespace_id,
        d.worker_id,
        7 /* EntityCode.Worker */,
        d.worker_id
    FROM @dead d;

    -- The holds its executing jobs took, a RunWithLockAsync lock or a concurrency slot, free now; a rate meter's rows
    -- hold nothing and stay.
    UPDATE k
    SET expires_at_utc = SYSUTCDATETIME()
    FROM {{schema}}.locks k WITH (ROWLOCK)
    INNER JOIN {{schema}}.jobs_state r ON r.job_id = k.job_id
    INNER JOIN @gone g ON g.worker_id = r.leased_by_worker_id
    WHERE
        r.job_status_code = 50 /* JobStatusCode.Executing */
        AND k.expires_at_utc > SYSUTCDATETIME()
        AND (
            k.lock_key LIKE CAST(r.namespace_id AS VARCHAR(12)) + '.lock.%'
            OR k.lock_key LIKE CAST(r.namespace_id AS VARCHAR(12)) + '.sem.%'
        );

    -- Its leases lapse now, so the recovery pass that follows reclaims the jobs. A lease write bumps no version.
    UPDATE r
    SET lease_expires_at_utc = DATEADD(MILLISECOND, -1, SYSUTCDATETIME())
    FROM {{schema}}.jobs_state r WITH (ROWLOCK)
    INNER JOIN @gone g ON g.worker_id = r.leased_by_worker_id
    WHERE
        r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.lease_expires_at_utc >= SYSUTCDATETIME();

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
