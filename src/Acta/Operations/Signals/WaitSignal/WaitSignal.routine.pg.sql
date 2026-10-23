/* Slot-locked arbiter for one durable wait: Set wins even past the due, an overdue Pending flips to
   Expired, Expired replays TimedOut forever. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a bound (a length, or an instant: a
   group's stored deadline), so code redeployed with a bound can un-strand a wait suspended without one; a stored due
   is never overwritten, never extended, and never cleared by a subsequent unbounded call. */
/* One resolution block, reached holding the row lock. An absent slot is armed first, because FOR
   UPDATE locks no key that does not exist yet and the arming insert can lose to a concurrent first
   arrival; the loser re-reads the winner's row and resolves against that. */
CREATE OR REPLACE FUNCTION {{schema}}.wait_signal(
    p_job_id BIGINT,
    p_execution_number INT,
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
BEGIN
    /* A slot this call arms at or past its due is armed Expired, so a child landing after it cannot
       revive the wait, and the wait times out now. Only an instant can be due already: a length is
       positive, so a wait armed with one suspends once before it can expire. */
    v_arm_state := CASE WHEN v_arm_due <= v_now THEN 30 /* CheckpointStatusCode.Expired */ ELSE 10 /* CheckpointStatusCode.Pending */ END;

    SELECT js.checkpoint_status_code, js.value_format_id, js.value, js.due_at_utc
    INTO v_state, v_fmt, v_val, v_due
    FROM {{schema}}.checkpoints js
    WHERE js.job_id = p_job_id AND js.checkpoint_kind_code = p_checkpoint_kind_code AND js.checkpoint_name = p_checkpoint_name
    FOR UPDATE;

    /* The wait changes the slot only while this attempt holds the job's row, share-locked to commit after a stored
       slot's row lock and before an absent slot's insert, the order raise_signal and a child's landing take them in
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
        RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:wait_signal: job % is no longer executing attempt %.', p_job_id, p_execution_number
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

        /* Returning here rather than resolving is what keeps a wait this call armed with a length
           suspending once on all three providers; mssql and sqlite reach that by construction. */
        IF v_armed = 1 THEN
            RETURN QUERY SELECT
                CASE
                    WHEN v_arm_state = 30 /* CheckpointStatusCode.Expired */ THEN 3 /* WaitSignalOutcomeCode.TimedOut */
                    ELSE 1 /* WaitSignalOutcomeCode.SuspendPending */
                END::SMALLINT,
                0 /* JobPayloadFormat.None */::SMALLINT,
                NULL::BYTEA;
            RETURN;
        END IF;

        /* Lost the race, so DO NOTHING waited the winner out and its row is committed. The re-read
           sees it because this runs at READ COMMITTED - the repo sets no isolation level - where each
           statement takes a fresh snapshot; under Serializable it would abort, which is also correct. */
        SELECT js.checkpoint_status_code, js.value_format_id, js.value, js.due_at_utc
        INTO v_state, v_fmt, v_val, v_due
        FROM {{schema}}.checkpoints js
        WHERE js.job_id = p_job_id AND js.checkpoint_kind_code = p_checkpoint_kind_code AND js.checkpoint_name = p_checkpoint_name
        FOR UPDATE;
    END IF;

    IF v_state = 20 /* CheckpointStatusCode.Set */ THEN
        RETURN QUERY SELECT
            2 /* WaitSignalOutcomeCode.ContinueSet */::SMALLINT,
            v_fmt,
            v_val;
    ELSIF v_state = 30 /* CheckpointStatusCode.Expired */ THEN
        RETURN QUERY SELECT
            3 /* WaitSignalOutcomeCode.TimedOut */::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
    ELSIF v_state = 10 /* CheckpointStatusCode.Pending */ AND v_due IS NOT NULL AND v_due <= v_now THEN

        UPDATE {{schema}}.checkpoints js
        SET
            checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */,
            modified_at_utc = v_now,
            version = js.version + 1
        WHERE js.job_id = p_job_id AND js.checkpoint_kind_code = p_checkpoint_kind_code AND js.checkpoint_name = p_checkpoint_name;

        RETURN QUERY SELECT
            3 /* WaitSignalOutcomeCode.TimedOut */::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
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

        RETURN QUERY SELECT
            CASE
                WHEN v_arm_state = 30 /* CheckpointStatusCode.Expired */ THEN 3 /* WaitSignalOutcomeCode.TimedOut */
                ELSE 1 /* WaitSignalOutcomeCode.SuspendPending */
            END::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
    ELSE
        RETURN QUERY SELECT
            1 /* WaitSignalOutcomeCode.SuspendPending */::SMALLINT,
            0 /* JobPayloadFormat.None */::SMALLINT,
            NULL::BYTEA;
    END IF;
END;
$$;
