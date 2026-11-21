-- Joined from the names, which seeks the parent's part of ux_jobs_idempotency_key_child; a list compared with
-- = ANY (ARRAY(SELECT ...)) is tested row by row against every child of the parent.
SELECT j.job_id, j.job_ref, j.idempotency_key
FROM json_array_elements_text(@p_child_names::json) AS n (idempotency_key)
INNER JOIN {{schema}}.jobs j
    ON
        j.parent_job_id = @p_parent_job_id
        AND j.idempotency_key = n.idempotency_key
        AND j.parent_job_id IS NOT NULL
        AND j.idempotency_key IS NOT NULL;
