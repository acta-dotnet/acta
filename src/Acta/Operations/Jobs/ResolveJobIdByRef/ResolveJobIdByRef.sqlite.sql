SELECT COALESCE(
    (
        SELECT j.id FROM {{schema}}.jobs j
        WHERE j.job_ref = @p_job_ref
    ),
    (
        SELECT MAX(e.job_id) FROM {{schema}}.events e
        WHERE e.job_ref = @p_job_ref
    )
);
