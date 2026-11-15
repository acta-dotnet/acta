
/* Row-locked arbiter for one durable wait: Set wins even past the due, an overdue Pending flips to
   Expired, Expired replays TimedOut forever. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a bound (a length, or an instant: a
   group's stored deadline), so code redeployed with a bound can un-strand a wait suspended without one; a stored due
   is never overwritten, never extended, and never cleared by a subsequent unbounded call. */
/* One resolution block, reached holding the row lock. An absent checkpoint is armed first, because FOR
   UPDATE locks no key that does not exist yet and the arming insert can lose to a concurrent first
   arrival; the loser re-reads the winner's row and resolves against that. */
CREATE OR REPLACE FUNCTION {{schema}}.wait_job_signal(
    p_job_id BIGINT,
    p_execution_number BIGINT,
    p_leased_by_worker_id INT,
    p_checkpoint_kind_code SMALLINT,
    p_checkpoint_name VARCHAR,
    p_timeout_seconds INT DEFAULT NULL,
    p_due_at_utc TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    outcome_code SMALLINT,
    value_format_id SMALLINT,
    value BYTEA
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ := now();
    v_arm_due TIMESTAMPTZ := COALESCE(p_due_at_utc, now() + make_interval(secs => p_timeout_seconds));
    v_arm_state SMALLINT;
    v_state SMALLINT;
    v_fmt SMALLINT;
    v_val BYTEA;
    v_due TIMESTAMPTZ;
    v_armed INT;
    v_outcome SMALLINT := 1 /* WaitJobSignalOutcomeCode.SuspendPending */;
    v_expired_due TIMESTAMPTZ;
BEGIN
    /* A checkpoint this call arms at or past its due is armed Expired, so a child landing after it cannot
       revive the wait, and the wait times out now. Only an instant can be due already: a length is
       positive, so a wait armed with one suspends once before it can expire. */
    v_arm_state := CASE WHEN v_arm_due <= v_now THEN 30 /* CheckpointStatusCode.Expired */ ELSE 10 /* CheckpointStatusCode.Pending */ END;

    SELECT js.checkpoint_status_code, js.value_format_id, js.value, js.due_at_utc
    INTO v_state, v_fmt, v_val, v_due
    FROM {{schema}}.checkpoints js
    WHERE js.job_id = p_job_id AND js.checkpoint_kind_code = p_checkpoint_kind_code AND js.checkpoint_name = p_checkpoint_name
    FOR UPDATE;

    /* The wait changes the checkpoint only while this attempt holds the job's row, share-locked to commit after a stored
       checkpoint's row lock and before an absent one's insert, the order raise_job_signal and a child's landing take them in
       (docs/sql-internals.md, "Execution fence"). */
    PERFORM 1
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = p_execution_number
        AND r.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
    FOR SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:wait_job_signal: job % is no longer executing attempt %.', p_job_id, p_execution_number
            USING ERRCODE = 'P0001';
    END IF;

    IF v_state IS NULL THEN
        INSERT INTO {{schema}}.checkpoints (
            job_id,
            checkpoint_kind_code,
            checkpoint_name,
            checkpoint_status_code,
            due_at_utc,
            value_format_id,
            value,
            created_at_utc,
            modified_at_utc,
            version)
        VALUES (
            p_job_id,
            p_checkpoint_kind_code,
            p_checkpoint_name,
            v_arm_state,
            v_arm_due,
            0 /* JobPayloadFormat.None */,
            NULL,
            v_now,
            v_now,
            0)
        ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO NOTHING;
        GET DIAGNOSTICS v_armed = ROW_COUNT;

        /* Lost the race, so DO NOTHING waited the winner out and its row is committed. The re-read
           sees it because this runs at READ COMMITTED - the repo sets no isolation level - where each
           statement takes a fresh snapshot; under Serializable it would abort, which is also correct. */
        IF v_armed = 0 THEN
            SELECT js.checkpoint_status_code, js.value_format_id, js.value, js.due_at_utc
            INTO v_state, v_fmt, v_val, v_due
            FROM {{schema}}.checkpoints js
            WHERE js.job_id = p_job_id AND js.checkpoint_kind_code = p_checkpoint_kind_code AND js.checkpoint_name = p_checkpoint_name
            FOR UPDATE;
        END IF;
    END IF;

    /* A wait this call armed is not resolved further, which keeps one armed with a length suspending once on all
       three providers; mssql and sqlite reach that by construction. */
    IF v_armed = 1 THEN
        IF v_arm_state = 30 /* CheckpointStatusCode.Expired */ THEN
            v_outcome := 3 /* WaitJobSignalOutcomeCode.TimedOut */;
            v_expired_due := v_arm_due;
        END IF;
    ELSIF v_state = 20 /* CheckpointStatusCode.Set */ THEN
        RETURN QUERY SELECT
            2 /* WaitJobSignalOutcomeCode.ContinueSet */::SMALLINT,
            v_fmt,
            v_val;
        RETURN;
    ELSIF v_state = 30 /* CheckpointStatusCode.Expired */ THEN
        v_outcome := 3 /* WaitJobSignalOutcomeCode.TimedOut */;
    ELSIF v_state = 10 /* CheckpointStatusCode.Pending */ AND v_due IS NOT NULL AND v_due <= v_now THEN

        UPDATE {{schema}}.checkpoints js
        SET
            checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */,
            modified_at_utc = v_now,
            version = js.version + 1
        WHERE js.job_id = p_job_id AND js.checkpoint_kind_code = p_checkpoint_kind_code AND js.checkpoint_name = p_checkpoint_name;

        v_outcome := 3 /* WaitJobSignalOutcomeCode.TimedOut */;
        v_expired_due := v_due;
    ELSIF v_state = 10 /* CheckpointStatusCode.Pending */ AND v_due IS NULL AND v_arm_due IS NOT NULL THEN

        UPDATE {{schema}}.checkpoints js
        SET
            checkpoint_status_code = v_arm_state,
            due_at_utc = v_arm_due,
            modified_at_utc = v_now,
            version = js.version + 1
        WHERE
            js.job_id = p_job_id
            AND js.checkpoint_kind_code = p_checkpoint_kind_code
            AND js.checkpoint_name = p_checkpoint_name
            AND js.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
            AND js.due_at_utc IS NULL;

        IF v_arm_state = 30 /* CheckpointStatusCode.Expired */ THEN
            v_outcome := 3 /* WaitJobSignalOutcomeCode.TimedOut */;
            v_expired_due := v_arm_due;
        END IF;
    END IF;

    -- An expiry this call made reaches the timeline, a child named as its parent named it; a replayed one does not.
    IF v_expired_due IS NOT NULL THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            entity_code, entity_id,
            definition_id, tenant_id, lineage_root_job_id,
            execution_number,
            event_message, details_format_id, details
        )
        SELECT
            83 /* EventCode.JobWaitExpired */, v_now, j.namespace_id,
            50 /* EntityCode.Job */, j.job_id,
            j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, j.job_id),
            p_execution_number,
            'The wait for '
            || CASE
                WHEN p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
                    THEN 'child ' || COALESCE('''' || ch.deduplication_key || '''', 'job ' || substr(p_checkpoint_name, 11))
                ELSE 'signal ''' || p_checkpoint_name || ''''
            END || ' expired.',
            1 /* JobPayloadFormat.Json */,
            convert_to(
                json_build_object(
                    'kind', CASE WHEN p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */ THEN 'child-latch' ELSE 'signal' END,
                    'name', p_checkpoint_name,
                    'dueAtUtc', to_char(v_expired_due AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
                )::text,
                'UTF8'
            )
        FROM {{schema}}.jobs j
        LEFT JOIN {{schema}}.jobs ch
            ON
                p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
                AND ch.parent_job_id = j.job_id
                AND 'sys.child.' || ch.job_id = p_checkpoint_name
        WHERE j.job_id = p_job_id AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;
    END IF;

    RETURN QUERY SELECT
        v_outcome,
        0 /* JobPayloadFormat.None */::SMALLINT,
        NULL::BYTEA;
END;
$$;

CREATE OR REPLACE VIEW {{schema}}.events_view AS
SELECT
    e.event_id,
    e.created_at_utc,
    ns.namespace_name,
    e.namespace_id,
    CASE e.entity_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'alert' WHEN 15 THEN 'calendar' WHEN 20 THEN 'checkpoint' WHEN 30 THEN 'definition' WHEN 40 THEN 'event' WHEN 50 THEN 'job' WHEN 60 THEN 'lane' WHEN 70 THEN 'lock' WHEN 80 THEN 'namespace' WHEN 90 THEN 'schedule' WHEN 100 THEN 'step' WHEN 110 THEN 'tag' WHEN 120 THEN 'tenant' WHEN 130 THEN 'worker' END AS entity,
    e.entity_code,
    e.entity_id,
    CASE e.event_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'tenant.suspended' WHEN 11 THEN 'tenant.resumed' WHEN 12 THEN 'tenant.updated' WHEN 20 THEN 'namespace.suspended' WHEN 21 THEN 'namespace.resumed' WHEN 22 THEN 'namespace.updated' WHEN 23 THEN 'namespace.deployed' WHEN 30 THEN 'definition.overrides-updated' WHEN 31 THEN 'definition.retired' WHEN 40 THEN 'job.execution-succeeded' WHEN 41 THEN 'job.execution-failed' WHEN 42 THEN 'job.execution-orphaned' WHEN 43 THEN 'job.execution-yielded' WHEN 44 THEN 'job.failed' WHEN 60 THEN 'job.suspended' WHEN 61 THEN 'job.rescheduled' WHEN 70 THEN 'job.cancelled' WHEN 71 THEN 'job.paused' WHEN 72 THEN 'job.resumed' WHEN 73 THEN 'job.restarted' WHEN 74 THEN 'job.reprioritized' WHEN 75 THEN 'job.purged' WHEN 76 THEN 'job.input-amended' WHEN 80 THEN 'job.signal-raised' WHEN 81 THEN 'job.state-reset' WHEN 82 THEN 'job.signal-cleared' WHEN 83 THEN 'job.wait-expired' WHEN 90 THEN 'job.note-recorded' WHEN 100 THEN 'schedule.paused' WHEN 101 THEN 'schedule.resumed' WHEN 102 THEN 'schedule.pause-expired' WHEN 104 THEN 'schedule.triggered' WHEN 105 THEN 'schedule.admitted' WHEN 106 THEN 'schedule.skipped' WHEN 107 THEN 'schedule.retimed' WHEN 108 THEN 'schedule.evaluation-failed' WHEN 109 THEN 'schedule.orphaned' WHEN 110 THEN 'calendar.draft-saved' WHEN 111 THEN 'calendar.published' WHEN 112 THEN 'calendar.archived' WHEN 120 THEN 'worker.started' WHEN 121 THEN 'worker.stopped' WHEN 122 THEN 'worker.died' WHEN 140 THEN 'alert.acknowledged' WHEN 141 THEN 'alert.resolved' WHEN 180 THEN 'outbox.requeued' WHEN 181 THEN 'outbox.discarded' END AS event,
    e.event_code,
    e.definition_id,
    d.definition_name,
    e.tenant_id,
    t.tenant_name,
    e.lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    e.worker_id,
    wkr.worker_ref,
    e.execution_number,
    e.duration_ms,
    e.event_message,
    e.actor,
    CASE e.details_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || e.details_format_id::TEXT
    END AS details_format,
    CASE WHEN e.details_format_id = 1 /* JobPayloadFormat.Json */ THEN CONVERT_FROM(e.details, 'UTF8') END AS details_text
FROM {{schema}}.events AS e
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = e.namespace_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = e.definition_id
LEFT JOIN {{schema}}.tenants AS t
    ON t.tenant_id = COALESCE(e.tenant_id, CASE WHEN e.entity_code = 120 /* EntityCode.Tenant */ THEN e.entity_id END)
LEFT JOIN {{schema}}.jobs AS root ON root.job_id = e.lineage_root_job_id
LEFT JOIN {{schema}}.workers AS wkr ON wkr.worker_id = e.worker_id;


INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115115355_record_a_wait_expiry')
ON CONFLICT (name) DO NOTHING;
