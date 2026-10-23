CREATE OR REPLACE FUNCTION {{schema}}.arm_or_consume_sleep_timer(
    p_job_id BIGINT,
    p_execution_number INT,
    p_leased_by_worker_id INT,
    p_checkpoint_name VARCHAR,
    p_delay_seconds INT DEFAULT NULL,
    p_resume_at_utc TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    outcome_code SMALLINT,
    due_at_utc TIMESTAMPTZ
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now TIMESTAMPTZ := now();
    v_due TIMESTAMPTZ := COALESCE(p_resume_at_utc, now() + make_interval(secs => p_delay_seconds));
    v_state SMALLINT;
    v_existing_due TIMESTAMPTZ;
BEGIN
    -- The job's row is the timer's job-level mutex, and the execution fence: the timer changes only while this attempt
    -- holds it (docs/sql-internals.md, "Execution fence").
    PERFORM 1
    FROM {{schema}}.jobs_state r0
    WHERE
        r0.job_id = p_job_id
        AND r0.job_status_code = 50 /* JobStatusCode.Executing */
        AND r0.execution_number = p_execution_number
        AND r0.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:arm_or_consume_sleep_timer: job % is no longer executing attempt %.', p_job_id, p_execution_number
            USING ERRCODE = 'P0001';
    END IF;

    SELECT jt.checkpoint_status_code, jt.due_at_utc INTO v_state, v_existing_due
    FROM {{schema}}.checkpoints jt
    WHERE
        jt.job_id = p_job_id
        AND jt.checkpoint_kind_code = 30 /* CheckpointKindCode.Timer */
        AND jt.checkpoint_name = p_checkpoint_name;

    IF v_state = 10 /* CheckpointStatusCode.Pending */ AND v_existing_due > v_now THEN

        RETURN QUERY SELECT 1 /* ArmOrConsumeSleepTimerOutcomeCode.Suspend */::SMALLINT, v_existing_due;
    ELSIF v_state = 10 /* CheckpointStatusCode.Pending */ THEN

        UPDATE {{schema}}.checkpoints jt
        SET
            checkpoint_status_code = 100 /* CheckpointStatusCode.Consumed */,
            modified_at_utc = now(),
            version = jt.version + 1
        WHERE
            jt.job_id = p_job_id
            AND jt.checkpoint_kind_code = 30 /* CheckpointKindCode.Timer */
            AND jt.checkpoint_name = p_checkpoint_name;
        /* Only the worker still executing may clear the instant. A reclaimed row is Ready again on an
           instant the reclaim owns, and ck_jobs_state_ready_due requires a Ready row to keep one. */
        UPDATE {{schema}}.jobs_state r2
        SET
            next_run_at_utc = NULL,
            modified_at_utc = now(),
            version = r2.version + 1
        WHERE
            r2.job_id = p_job_id
            AND r2.job_status_code = 50 /* JobStatusCode.Executing */;
        RETURN QUERY SELECT 2 /* ArmOrConsumeSleepTimerOutcomeCode.Continue */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSIF v_state IS NOT NULL THEN

        RETURN QUERY SELECT 2 /* ArmOrConsumeSleepTimerOutcomeCode.Continue */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSIF v_due <= v_now THEN

        RETURN QUERY SELECT 2 /* ArmOrConsumeSleepTimerOutcomeCode.Continue */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSIF EXISTS (SELECT 1 FROM {{schema}}.checkpoints jt WHERE jt.job_id = p_job_id AND jt.checkpoint_kind_code = 30 /* CheckpointKindCode.Timer */ AND jt.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */) THEN
        RETURN QUERY SELECT 3 /* ArmOrConsumeSleepTimerOutcomeCode.Reject */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSE
        INSERT INTO {{schema}}.checkpoints (
            job_id,
            checkpoint_kind_code,
            checkpoint_name,
            checkpoint_status_code,
            due_at_utc,
            created_at_utc,
            modified_at_utc,
            version)
        VALUES (
            p_job_id,
            30 /* CheckpointKindCode.Timer */,
            p_checkpoint_name,
            10 /* CheckpointStatusCode.Pending */,
            v_due,
            now(),
            now(),
            0);
        RETURN QUERY SELECT 1 /* ArmOrConsumeSleepTimerOutcomeCode.Suspend */::SMALLINT, v_due;
    END IF;
END;
$$;
