SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @next_retention_utc DATETIME2(3) = NULL;
    DECLARE @next_job_id BIGINT = 0;
    -- The index keys are DATETIME2(3), and a forced seek on them compiles only against values of that type.
    DECLARE @cutoff_utc DATETIME2(3) = @p_cutoff_utc;
    DECLARE @after_utc DATETIME2(3) = @p_after_retention_utc;
    DECLARE @candidates TABLE (job_id BIGINT NOT NULL PRIMARY KEY, retention_until_utc DATETIME2(3) NOT NULL);
    DECLARE @job_del TABLE (job_id BIGINT NOT NULL);
    DECLARE @alert_del TABLE (alert_id BIGINT NOT NULL PRIMARY KEY, deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL);
    DECLARE @alert_seen TABLE (alert_id BIGINT NOT NULL PRIMARY KEY);

    /* This namespace's expired jobs from ix_jobs_state_retention after the cursor, read without update locks and skipping
       a key another transaction holds. A full batch answers its last candidate, so the next call reads on past it; a
       shorter one reached the end of the expired range and answers no cursor. */
    INSERT INTO @candidates (job_id, retention_until_utc)
    SELECT TOP (@p_batch_size) k.job_id, k.retention_until_utc
    FROM {{schema}}.jobs_state k WITH (READCOMMITTEDLOCK, READPAST, FORCESEEK (ix_jobs_state_retention (namespace_id, retention_until_utc)))
    WHERE
        k.namespace_id = @p_namespace_id
        AND k.retention_until_utc IS NOT NULL
        AND k.retention_until_utc < @cutoff_utc
        AND k.retention_until_utc >= @after_utc
        AND (k.retention_until_utc > @after_utc OR k.job_id > @p_after_job_id)
    ORDER BY k.retention_until_utc, k.job_id;

    IF (SELECT COUNT(*) FROM @candidates) = @p_batch_size
        SELECT TOP (1) @next_retention_utc = c.retention_until_utc, @next_job_id = c.job_id
        FROM @candidates c
        ORDER BY c.retention_until_utc DESC, c.job_id DESC;

    /* A locking read seeks (docs/sql-internals.md, "Locking reads seek"). The candidates were read without update locks,
       so the index holds none; a job is locked through its state row, where the status and the deadline are read
       again, and then its own row. */
    INSERT INTO @job_del (job_id)
    SELECT j.job_id
    FROM @candidates k
    INNER JOIN {{schema}}.jobs_state r WITH (UPDLOCK, READPAST, FORCESEEK) ON r.job_id = k.job_id
    INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, READPAST, FORCESEEK) ON j.job_id = r.job_id
    WHERE
        r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        AND r.retention_until_utc IS NOT NULL
        AND r.retention_until_utc < @p_cutoff_utc
        -- Lineage guard: parent_job_id carries no FK, so purging a parent whose children still
        -- exist would orphan their lineage (same rule as the manual purge_job). Only leaves
        -- delete; a fully-expired subtree drains bottom-up across iterations.
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.jobs c WITH (FORCESEEK (ix_jobs_parent (parent_job_id)))
            WHERE c.parent_job_id = j.job_id AND c.parent_job_id IS NOT NULL
        )
        -- A finished child is kept until its parent has finished and the parent's own retention is due, because the
        -- parent's replay reads the child's result from the child's row. Trees still drain from the leaves up.
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.jobs_state p WITH (FORCESEEK)
            WHERE p.job_id = j.parent_job_id
                AND (
                    p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    OR p.retention_until_utc IS NULL
                    OR p.retention_until_utc > @p_cutoff_utc
                )
        )
        -- A stopped recurring job stays while its definition is live: the next deploy would register
        -- its declaration as a new job and undo the cancel or failure. Retiring the definition lets
        -- it drain.
        AND NOT EXISTS (
            SELECT 1 FROM {{schema}}.schedules s WITH (FORCESEEK (ix_schedules_job (job_id)))
            INNER JOIN {{schema}}.definitions d WITH (FORCESEEK) ON d.definition_id = j.definition_id
            WHERE
                s.job_id = j.job_id
                AND s.job_id IS NOT NULL
                AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
        )
    OPTION (FORCE ORDER, LOOP JOIN);

    /* A job stays while an alert of it is undelivered: the failure it reports has reached nobody yet. The batch has given
       the walk its place, so a job kept here is passed. The batch's alert ids come from ix_alerts_job alone and their
       rows by primary key (docs/sql-internals.md, "Alert lock order"). */
    INSERT INTO @alert_seen (alert_id)
    SELECT k.alert_id
    FROM @job_del d
    INNER JOIN {{schema}}.alerts k WITH (FORCESEEK (ix_alerts_job (job_id))) ON k.job_id = d.job_id AND k.job_id IS NOT NULL;
    DELETE d
    FROM @job_del d
    WHERE d.job_id IN (
        SELECT a.job_id
        FROM @alert_seen s
        INNER JOIN {{schema}}.alerts a WITH (FORCESEEK (pk_alerts (alert_id))) ON a.alert_id = s.alert_id
        WHERE a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.Retrying */)
    );

    -- Each delete below seeks its rows by job id: an unhinted plan can scan
    -- the whole child table under update locks
    -- (docs/sql-internals.md, "Foreign keys in set-based writes").
    DELETE t FROM {{schema}}.tags t WITH (FORCESEEK (pk_tags (entity_code, entity_id)))
    INNER JOIN @job_del d ON t.entity_code = 4 /* EntityCode.Job */ AND t.entity_id = d.job_id;

    /* The jobs' settled alerts go with them, so no alert outlives its job: the held job rows let none join since the ids
       were read. Each identity row is locked before its row (docs/sql-internals.md, "Alert lock order"), and the delete
       rechecks the job, since a deduplicated raise moves an open alert. */
    INSERT INTO @alert_del (alert_id, deduplication_key)
    SELECT a.alert_id, a.deduplication_key
    FROM @alert_seen s
    INNER JOIN {{schema}}.alerts a WITH (FORCESEEK (pk_alerts (alert_id))) ON a.alert_id = s.alert_id
    WHERE EXISTS (SELECT 1 FROM @job_del j WHERE j.job_id = a.job_id);
    DECLARE @alert_identities INT = (
        SELECT COUNT(*)
        FROM @alert_del d
        INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, FORCESEEK (ix_alerts_deduplication_key_identity (namespace_id, deduplication_key)))
            ON
                a.namespace_id = @p_namespace_id
                AND a.deduplication_key = d.deduplication_key
                AND a.alert_id = d.alert_id
        WHERE d.deduplication_key IS NOT NULL
    );
    DELETE a FROM {{schema}}.alerts a WITH (FORCESEEK (pk_alerts (alert_id))) INNER JOIN @alert_del d ON d.alert_id = a.alert_id
    WHERE EXISTS (SELECT 1 FROM @job_del j WHERE j.job_id = a.job_id);
    DELETE c FROM {{schema}}.checkpoints c WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = c.job_id;
    DELETE r FROM {{schema}}.jobs_state r WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = r.job_id;
    DELETE s FROM {{schema}}.steps s WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = s.job_id;
    DELETE st FROM {{schema}}.schedules_state st WITH (FORCESEEK (pk_schedules_state (schedule_id)))
    INNER JOIN {{schema}}.schedules s WITH (FORCESEEK (ix_schedules_job (job_id))) ON s.schedule_id = st.schedule_id
    INNER JOIN @job_del d ON d.job_id = s.job_id;
    DELETE s FROM {{schema}}.schedules s WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = s.job_id;
    DELETE j FROM {{schema}}.jobs j WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = j.job_id;

    SELECT (SELECT COUNT(*) FROM @job_del) AS deleted_count, @next_retention_utc AS next_retention_utc, @next_job_id AS next_job_id;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
