CREATE OR ALTER PROCEDURE {{schema}}.extend_worker_leases
    @p_leased_by_worker_id INT,
    @p_lease_ttl_seconds INT,
    @p_draining BIT,
    @p_executions NVARCHAR(MAX),
    @p_locks NVARCHAR(MAX),
    @p_recovery_interval_seconds INT = NULL
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
        DECLARE @namespace_id INT = (SELECT w.namespace_id FROM {{schema}}.workers w WHERE w.worker_id = @p_leased_by_worker_id);

        UPDATE {{schema}}.workers
        SET
            last_seen_at_utc = @now,
            -- A worker marked Dead through a database outage longer than its dead-after window is alive again the
            -- moment it heartbeats; Stopped stays Stopped.
            worker_status_code = CASE
                WHEN @p_draining = 1 AND worker_status_code IN (10 /* WorkerStatusCode.Active */, 200 /* WorkerStatusCode.Dead */)
                    THEN 80 /* WorkerStatusCode.Draining */
                WHEN worker_status_code = 200 /* WorkerStatusCode.Dead */ THEN 10 /* WorkerStatusCode.Active */
                ELSE worker_status_code
            END,
            modified_at_utc = @now,
            version = version + 1
        WHERE worker_id = @p_leased_by_worker_id;

        /* The held executions and lock holds arrive as JSON array text, expanded once into keyed tables. A process
           can hold two executions of one job, or two holds on one key, while a stale attempt unwinds. */
        DECLARE @held TABLE (job_id BIGINT NOT NULL, execution_number INT NOT NULL, PRIMARY KEY (job_id, execution_number));
        INSERT INTO @held (job_id, execution_number)
        SELECT job_id, execution_number
        FROM OPENJSON(@p_executions) WITH (job_id BIGINT '$.job_id', execution_number INT '$.execution_number');

        DECLARE @lock_held TABLE (
            lock_key VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
            hold_token UNIQUEIDENTIFIER NOT NULL,
            PRIMARY KEY (lock_key, hold_token)
        );
        INSERT INTO @lock_held (lock_key, hold_token)
        SELECT lock_key, hold_token
        FROM OPENJSON(@p_locks) WITH (
            lock_key VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 '$.lock_key',
            hold_token UNIQUEIDENTIFIER '$.hold_token'
        );

        -- Lock holds renew first and never skip: no routine locks both locks and jobs_state. A hold whose token
        -- moved is not answered, and the attempt that relied on it stops.
        DECLARE @lock_renewed TABLE (lock_key VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL PRIMARY KEY);
        UPDATE k
        SET expires_at_utc = @new_expiry
        OUTPUT INSERTED.lock_key INTO @lock_renewed (lock_key)
        FROM {{schema}}.locks k WITH (FORCESEEK)
        INNER JOIN @lock_held h ON h.lock_key = k.lock_key AND h.hold_token = k.hold_token;

        /* Push the held executions' leases forward. Only a row this worker still leases at the execution the
           process holds is answered, so a row the process lost lapses and recovery takes it. Deliberately no
           version bump: a lease refresh is not a claim-generation change. */
        -- Read the held rows first so the update locks base rows only, never an index key; complete_executions locks in
        -- the other order. A row another transaction holds is skipped and answered unrenewed, so the read never waits;
        -- only a row read that no longer matches goes unanswered, which stops its attempt (docs/sql-internals.md).
        DECLARE @seen TABLE (
            job_id BIGINT NOT NULL,
            execution_number INT NOT NULL,
            matched BIT NOT NULL,
            PRIMARY KEY (job_id, execution_number)
        );
        INSERT INTO @seen (job_id, execution_number, matched)
        SELECT
            h.job_id,
            h.execution_number,
            CASE
                WHEN r.execution_number = h.execution_number
                    AND r.leased_by_worker_id = @p_leased_by_worker_id
                    AND r.job_status_code = 50 /* JobStatusCode.Executing */
                    THEN 1
                ELSE 0
            END
        FROM @held h
        INNER JOIN {{schema}}.jobs_state r WITH (INDEX (pk_jobs_state), FORCESEEK, READCOMMITTEDLOCK, READPAST) ON r.job_id = h.job_id;

        -- A row another transaction holds is skipped and reported unrenewed, so the renewal never waits
        -- (docs/sql-internals.md, "Explicit exceptions and maintenance").
        DECLARE @renewed TABLE (job_id BIGINT NOT NULL PRIMARY KEY);
        UPDATE r
        SET lease_expires_at_utc = @new_expiry
        OUTPUT INSERTED.job_id INTO @renewed (job_id)
        FROM {{schema}}.jobs_state r WITH (INDEX(pk_jobs_state), FORCESEEK, ROWLOCK, READPAST)
        INNER JOIN @seen m ON m.job_id = r.job_id AND m.execution_number = r.execution_number AND m.matched = 1
        WHERE
            r.leased_by_worker_id = @p_leased_by_worker_id
            AND r.job_status_code = 50 /* JobStatusCode.Executing */;

        -- Takes the namespace's recovery pass when it is due, moving the next one an interval out; this worker runs it.
        -- A namespace row another transaction holds is skipped, so the heartbeat never waits on it.
        DECLARE @recovery_claimed BIT = 0;
        IF @p_recovery_interval_seconds IS NOT NULL
            BEGIN
                UPDATE n
                SET recovery_due_at_utc = DATEADD(SECOND, @p_recovery_interval_seconds, @now)
                FROM {{schema}}.namespaces n WITH (ROWLOCK, READPAST)
                WHERE n.namespace_id = @namespace_id AND (n.recovery_due_at_utc IS NULL OR n.recovery_due_at_utc <= @now);
                SET @recovery_claimed = CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END;
            END;

        SELECT
            CAST(NULL AS BIGINT) AS job_id,
            CAST(NULL AS INT) AS execution_number,
            l.lock_key,
            CAST(1 AS BIT) AS renewed,
            CAST(NULL AS DATETIME2(3)) AS definitions_modified_at_utc,
            CAST(0 AS BIT) AS recovery_claimed
        FROM @lock_renewed l
        UNION ALL
        SELECT
            h.job_id,
            h.execution_number,
            CAST(NULL AS VARCHAR(256)),
            CAST(CASE WHEN n.job_id IS NULL THEN 0 ELSE 1 END AS BIT),
            CAST(NULL AS DATETIME2(3)),
            CAST(0 AS BIT)
        FROM @held h
        LEFT JOIN @seen s ON s.job_id = h.job_id AND s.execution_number = h.execution_number
        LEFT JOIN @renewed n ON n.job_id = h.job_id
        WHERE s.job_id IS NULL OR s.matched = 1
        UNION ALL
        -- The namespace's newest definition change: the worker re-reads its catalog only when it moves. A definition
        -- another transaction holds is skipped, which at worst delays that re-read by one beat.
        SELECT
            CAST(NULL AS BIGINT) AS job_id,
            CAST(NULL AS INT) AS execution_number,
            CAST(NULL AS VARCHAR(256)) AS lock_key,
            CAST(0 AS BIT) AS renewed,
            MAX(d.modified_at_utc) AS definitions_modified_at_utc,
            @recovery_claimed AS recovery_claimed
        FROM {{schema}}.definitions d WITH (FORCESEEK, READCOMMITTEDLOCK, READPAST)
        WHERE d.namespace_id = @namespace_id;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
