-- A CROSS JOIN keeps the names the outer loop, which SQLite never reorders, so each name seeks the parent's key in its
-- filtered index; an inner join lets the planner scan the parent's children and the list once for each.
SELECT j.job_id, j.job_ref, j.deduplication_key
FROM json_each(@p_child_names) n
CROSS JOIN {{schema}}.jobs j
WHERE
    j.parent_job_id = @p_parent_job_id
    AND j.deduplication_key = n.value
    AND j.parent_job_id IS NOT NULL
    AND j.deduplication_key IS NOT NULL;
