CREATE OR REPLACE FUNCTION {{schema}}.arm_or_consume_sleep_timer(
    p_job_id        BIGINT,
    p_name          VARCHAR,
    p_delay_seconds INT DEFAULT NULL,
    p_resume_at_utc TIMESTAMPTZ DEFAULT NULL
)
RETURNS TABLE (
    outcome_code SMALLINT,
    due_at_utc   TIMESTAMPTZ
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now          TIMESTAMPTZ := now();
    v_due          TIMESTAMPTZ := COALESCE(p_resume_at_utc, now() + make_interval(secs => p_delay_seconds));
    v_state        SMALLINT;
    v_existing_due TIMESTAMPTZ;
BEGIN

    PERFORM 1 FROM {{schema}}.runtimes r0 WHERE r0.job_id = p_job_id FOR UPDATE;

    SELECT jt.state_code, jt.due_at_utc INTO v_state, v_existing_due
      FROM {{schema}}.checkpoints jt
     WHERE jt.job_id = p_job_id AND jt.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND jt.name = p_name;

    IF v_state = 10 /* JobCheckpointStateCode.Pending */ AND v_existing_due > v_now THEN

        RETURN QUERY SELECT 1 /* SleepOutcome.Suspend */::SMALLINT, v_existing_due;
    ELSIF v_state = 10 /* JobCheckpointStateCode.Pending */ THEN

        UPDATE {{schema}}.checkpoints jt
           SET state_code = 100 /* JobCheckpointStateCode.Consumed */, modified_at_utc = now(), version = jt.version + 1
         WHERE jt.job_id = p_job_id AND jt.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND jt.name = p_name;
        UPDATE {{schema}}.runtimes r2
           SET next_run_at_utc = NULL, modified_at_utc = now(), version = r2.version + 1
         WHERE r2.job_id = p_job_id;
        RETURN QUERY SELECT 2 /* SleepOutcome.Continue */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSIF v_state IS NOT NULL THEN

        RETURN QUERY SELECT 2 /* SleepOutcome.Continue */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSIF v_due <= v_now THEN

        RETURN QUERY SELECT 2 /* SleepOutcome.Continue */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSIF EXISTS (SELECT 1 FROM {{schema}}.checkpoints jt WHERE jt.job_id = p_job_id AND jt.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND jt.state_code = 10 /* JobCheckpointStateCode.Pending */) THEN
        RETURN QUERY SELECT 3 /* SleepOutcome.Reject */::SMALLINT, NULL::TIMESTAMPTZ;
    ELSE
        INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, state_code, due_at_utc, created_at_utc, modified_at_utc, version)
        VALUES (p_job_id, 30 /* JobCheckpointKindCode.Timer */, p_name, 10 /* JobCheckpointStateCode.Pending */, v_due, now(), now(), 0);
        RETURN QUERY SELECT 1 /* SleepOutcome.Suspend */::SMALLINT, v_due;
    END IF;
END;
$$;
