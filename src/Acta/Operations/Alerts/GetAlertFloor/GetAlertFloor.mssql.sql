DECLARE @ns_id INT = COALESCE(@p_namespace_id, (SELECT o.namespace_id FROM {{schema}}.jobs o WHERE o.job_id = @p_job_id));

-- The slot is the namespace's root sys.alerts job; its cursor and skip records are variables keyed under it. The
-- definition is read by name and the slot through ux_jobs_slot, so the read never touches a job an application is
-- still enqueuing (docs/sql-internals.md, "Locking reads seek").
SELECT
    c.checkpoint_name,
    c.value_format_id,
    c.value
FROM {{schema}}.definitions d WITH (FORCESEEK)
INNER JOIN {{schema}}.jobs j WITH (INDEX (ux_jobs_slot), FORCESEEK)
    ON j.definition_id = d.definition_id AND j.job_kind_code = 20 /* JobKindCode.Permanent */
LEFT JOIN {{schema}}.checkpoints c
    ON
        c.job_id = j.job_id
        AND c.checkpoint_kind_code = 10 /* CheckpointKindCode.Variable */
        AND (c.checkpoint_name = 'alerts-cursor' OR c.checkpoint_name LIKE 'alerts-skip-%')
WHERE d.namespace_id = @ns_id AND d.definition_name = 'sys.alerts'
ORDER BY c.checkpoint_name;
