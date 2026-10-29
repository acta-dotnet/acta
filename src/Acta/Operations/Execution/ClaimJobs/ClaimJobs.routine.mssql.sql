CREATE OR ALTER PROCEDURE {{schema}}.claim_jobs
    @p_namespace_id INT,
    @p_leased_by_worker_id INT,
    @p_claim_limit INT,
    @p_lease_ttl_seconds INT,
    @p_excluded_definition_ids NVARCHAR(MAX) = NULL,
    @p_claim_from_utc DATETIME2(3) = NULL
AS
BEGIN
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

        /* Rolling-deploy exclusion: the definitions this worker already bounced for want of a handler,
           passed as JSON array text. Expanded once here so neither the claim nor the horizon pays for
           OPENJSON per row, and counted so the healthy fleet's empty set short-circuits both. */
        DECLARE @excluded TABLE (definition_id INT NOT NULL PRIMARY KEY);
        IF @p_excluded_definition_ids IS NOT NULL
            INSERT INTO @excluded (definition_id)
            SELECT CAST(value AS INT) FROM OPENJSON(@p_excluded_definition_ids);
        DECLARE @excluded_count INT = (SELECT COUNT(*) FROM @excluded);

        DECLARE
            @claimed TABLE
            (
                job_id BIGINT NOT NULL PRIMARY KEY,
                job_ref UNIQUEIDENTIFIER NOT NULL,
                definition_id INT NOT NULL,
                tenant_id INT NULL,
                execution_number INT NOT NULL,
                deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
                job_kind_code TINYINT NOT NULL,
                correlation_key VARCHAR(64) NULL,
                concurrency_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
                input_format_id TINYINT NOT NULL,
                input VARBINARY(MAX) NULL,
                next_run_at_utc DATETIME2(3) NULL,
                created_at_utc DATETIME2(3) NOT NULL,
                failure_count INT NOT NULL,
                lane_id BIGINT NULL,
                parent_job_id BIGINT NULL,
                schedule_id INT NULL
            );

        /* Every JobPriorityCode, listed once: the claim seeks each band in turn and the horizon reads each band's head. */
        DECLARE @bands TABLE (job_priority_code TINYINT NOT NULL PRIMARY KEY);
        INSERT INTO @bands (job_priority_code)
        VALUES
            (100 /* JobPriorityCode.Realtime */),
            (85 /* JobPriorityCode.Critical */),
            (70 /* JobPriorityCode.High */),
            (50 /* JobPriorityCode.Normal */),
            (0 /* JobPriorityCode.Bulk */);

        DECLARE @candidates TABLE (job_id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @remaining INT = @p_claim_limit;
        DECLARE @band TINYINT = (SELECT MAX(job_priority_code) FROM @bands);

        /* One seek per band, highest band first, each stopping at the band's first row not yet due. The
           UPDLOCK holds every candidate to the end of the transaction, so the update below finds each one
           as this loop left it. */
        WHILE @band IS NOT NULL AND @remaining > 0
            BEGIN
                /* Pure claim-index seek on ix_jobs_state_claim_ready via the denormalized namespace; concurrency-key admission is
                   executor-owned (lock store) after the start CAS, and the one jobs lookup is the exclusion below. */
                INSERT INTO @candidates (job_id)
                SELECT TOP (@remaining) r.job_id
                FROM {{schema}}.jobs_state r WITH (READPAST, UPDLOCK, ROWLOCK, READCOMMITTEDLOCK)
                WHERE
                    r.namespace_id = @p_namespace_id
                    AND r.job_priority_code = @band
                    /* ix_jobs_state_claim_ready's filter restated whole: filtered-index matching takes top-level AND-terms
                       only, and without it the claim scans jobs_state and widens its UPDLOCK footprint to match. Ready always
                       carries its due instant (ck_jobs_state_ready_due); a Suspended NULL never comes due. */
                    AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                    AND r.next_run_at_utc IS NOT NULL
                    AND r.next_run_at_utc <= @due_now
                    /* The worker's watermark starts the seek past the ghost records earlier claims left at the band's
                       head (docs/sql-internals.md, "Claim watermark"); NULL seeks from the head. */
                    AND r.next_run_at_utc >= ISNULL(@p_claim_from_utc, CAST('0001-01-01' AS DATETIME2(3)))
                    /* Empty on a healthy fleet, so the count test settles it before the jobs lookup and
                       the seek above stays as it was. */
                    AND (
                        @excluded_count = 0
                        OR NOT EXISTS (
                            SELECT 1
                            FROM {{schema}}.jobs j
                            WHERE j.job_id = r.job_id AND j.definition_id IN (SELECT e.definition_id FROM @excluded e)
                        )
                    )
                ORDER BY
                    r.next_run_at_utc ASC,
                    r.job_id ASC;

                SET @remaining -= @@ROWCOUNT;
                SET @band = (SELECT MAX(job_priority_code) FROM @bands WHERE job_priority_code < @band);
            END

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
            INSERTED.job_id,
            j.job_ref,
            j.definition_id,
            j.tenant_id,
            INSERTED.execution_number,
            j.deduplication_key,
            j.job_kind_code,
            j.correlation_key,
            j.concurrency_key,
            j.input_format_id,
            j.input,
            INSERTED.next_run_at_utc,
            j.created_at_utc,
            INSERTED.failure_count,
            INSERTED.lane_id,
            j.parent_job_id,
            j.schedule_id
        INTO
            @claimed (
                job_id, job_ref, definition_id, tenant_id, execution_number,
                deduplication_key, job_kind_code, correlation_key, concurrency_key,
                input_format_id, input, next_run_at_utc, created_at_utc,
                failure_count, lane_id, parent_job_id, schedule_id
            )
        -- FORCESEEK keeps this update on a key seek instead of a lock-escalating scan of jobs_state. The seek above holds
        -- claim-index keys, so READPAST skips a row a verb holds instead of waiting for it, which would invert the lock
        -- order (docs/sql-internals.md); a skipped candidate is simply not claimed.
        FROM {{schema}}.jobs_state r WITH (INDEX (pk_jobs_state), FORCESEEK, ROWLOCK, READPAST)
        INNER JOIN @candidates c ON c.job_id = r.job_id
        INNER JOIN {{schema}}.jobs j WITH (FORCESEEK) ON j.job_id = r.job_id;

        IF EXISTS (SELECT 1 FROM @claimed)
            BEGIN
                SELECT
                    job_id,
                    definition_id,
                    execution_number,
                    deduplication_key,
                    job_kind_code,
                    correlation_key,
                    concurrency_key,
                    input_format_id,
                    input,
                    next_run_at_utc,
                    created_at_utc,
                    failure_count,
                    job_ref,
                    tenant_id,
                    CAST(NULL AS DATETIME2(7)) AS db_now,
                    CAST(NULL AS DATETIME2(3)) AS next_ready_at_utc,
                    lane_id,
                    parent_job_id,
                    schedule_id
                FROM @claimed
                ORDER BY job_id;
            END
        ELSE
            BEGIN
                SELECT
                    CAST(NULL AS BIGINT) AS job_id,
                    CAST(NULL AS INT) AS definition_id,
                    CAST(NULL AS INT) AS execution_number,
                    CAST(NULL AS VARCHAR(128)) AS deduplication_key,
                    CAST(NULL AS TINYINT) AS job_kind_code,
                    CAST(NULL AS VARCHAR(64)) AS correlation_key,
                    CAST(NULL AS VARCHAR(128)) AS concurrency_key,
                    CAST(NULL AS TINYINT) AS input_format_id,
                    CAST(NULL AS VARBINARY(MAX)) AS input,
                    CAST(NULL AS DATETIME2(3)) AS next_run_at_utc,
                    CAST(NULL AS DATETIME2(3)) AS created_at_utc,
                    CAST(NULL AS INT) AS failure_count,
                    CAST(NULL AS UNIQUEIDENTIFIER) AS job_ref,
                    CAST(NULL AS INT) AS tenant_id,
                    @now AS db_now,
                    /* The earliest head across the bands: each band's first index row is its earliest instant. */
                    (
                        SELECT MIN(head.next_run_at_utc)
                        FROM @bands b
                        CROSS APPLY (
                            SELECT TOP (1) r.next_run_at_utc
                            FROM {{schema}}.jobs_state r
                            WHERE
                                r.namespace_id = @p_namespace_id
                                AND r.job_priority_code = b.job_priority_code
                                AND r.job_status_code IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                                AND r.next_run_at_utc IS NOT NULL
                                /* Excluded rows are invisible to this worker's horizon too: a horizon at or
                                   before now is what tells the caller to retry at the anti-spin floor, so
                                   counting rows this worker will never claim would spin it. */
                                AND (
                                    @excluded_count = 0
                                    OR NOT EXISTS (
                                        SELECT 1
                                        FROM {{schema}}.jobs j
                                        WHERE j.job_id = r.job_id AND j.definition_id IN (SELECT e.definition_id FROM @excluded e)
                                    )
                                )
                            ORDER BY r.next_run_at_utc ASC
                        ) head
                    ) AS next_ready_at_utc,
                    CAST(NULL AS BIGINT) AS lane_id,
                    CAST(NULL AS BIGINT) AS parent_job_id,
                    CAST(NULL AS INT) AS schedule_id;
            END

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
