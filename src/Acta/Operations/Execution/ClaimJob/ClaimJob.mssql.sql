SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @lease_expires DATETIME2(3) = DATEADD(SECOND, @p_lease_ttl_seconds, @now);
    /* Dueness compares at the column's DATETIME2(3) precision: enqueue rounds next_run_at_utc up to
       0.5 ms into the future, so a full-precision @now can transiently see a just-enqueued row as
       not yet due. Rounding is monotonic, so the same-precision comparison never can. */
    DECLARE @due_now DATETIME2(3) = @now;

    DECLARE
        @claimed TABLE
        (
            job_id BIGINT NOT NULL PRIMARY KEY,
            job_ref UNIQUEIDENTIFIER NOT NULL,
            definition_id INT NOT NULL,
            tenant_id INT NULL,
            execution_number INT NOT NULL,
            deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
            correlation_key VARCHAR(64) NULL,
            concurrency_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
            input_format_id TINYINT NOT NULL,
            input VARBINARY(MAX) NULL,
            next_run_at_utc DATETIME2(3) NULL,
            created_at_utc DATETIME2(3) NOT NULL,
            failure_count INT NOT NULL,
            lane_id BIGINT NULL,
            parent_job_id BIGINT NULL
        );

    WITH candidates AS (
        /* claim_jobs' due test: a Ready row always carries its due instant (ck_jobs_state_ready_due), and a
           Suspended row with a NULL instant is an unbounded wait that never comes due. */
        SELECT r.job_id
        FROM {{schema}}.jobs_state r WITH (READPAST, UPDLOCK, ROWLOCK, READCOMMITTEDLOCK)
        WHERE
            r.job_id = @p_job_id
            AND r.namespace_id = @p_namespace_id
            AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
            AND r.next_run_at_utc IS NOT NULL
            AND r.next_run_at_utc <= @due_now
    )

    UPDATE r
    SET
        job_status_code = 50 /* JobStatusCode.Executing */,
        execution_number = r.execution_number + 1,
        leased_by_worker_id = @p_leased_by_worker_id,
        lease_expires_at_utc = @lease_expires,
        result_format_id = 0 /* JobPayloadFormat.None */,
        result = NULL,
        modified_at_utc = @now,
        version = r.version + 1
    OUTPUT
        INSERTED.job_id, j.job_ref, j.definition_id, j.tenant_id,
        INSERTED.execution_number, j.deduplication_key, j.correlation_key, j.concurrency_key,
        j.input_format_id, j.input, INSERTED.next_run_at_utc, j.created_at_utc,
        INSERTED.failure_count, INSERTED.lane_id, j.parent_job_id
    INTO
        @claimed (
            job_id, job_ref, definition_id, tenant_id, execution_number,
            deduplication_key, correlation_key, concurrency_key,
            input_format_id, input, next_run_at_utc, created_at_utc,
            failure_count, lane_id, parent_job_id
        )
    -- FORCESEEK keeps this update on a key seek instead of a lock-escalating scan of jobs_state. The seek above holds
    -- claim-index keys, so READPAST skips a row a verb holds instead of waiting for it, which would invert the lock
    -- order (docs/sql-internals.md); a skipped candidate is simply not claimed.
    FROM {{schema}}.jobs_state r WITH (INDEX (pk_jobs_state), FORCESEEK, ROWLOCK, READPAST)
    INNER JOIN candidates c ON c.job_id = r.job_id
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id;

    SELECT
        job_id,
        definition_id,
        execution_number,
        deduplication_key,
        correlation_key,
        concurrency_key,
        input_format_id,
        input,
        next_run_at_utc,
        created_at_utc,
        failure_count,
        job_ref,
        tenant_id,
        lane_id,
        parent_job_id
    FROM @claimed;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
