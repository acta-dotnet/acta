-- The slot is the namespace's root sys.alerts job; its cursor and skip records are variables keyed under it.
SELECT
    c.checkpoint_name,
    c.value_format_id,
    c.value
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.definitions d ON d.definition_id = j.definition_id AND d.definition_name = 'sys.alerts'
LEFT JOIN {{schema}}.checkpoints c
    ON
        c.job_id = j.job_id
        AND c.checkpoint_kind_code = 10 /* CheckpointKindCode.Variable */
        AND (c.checkpoint_name = 'alerts-cursor' OR c.checkpoint_name LIKE 'alerts-skip-%')
WHERE
    j.namespace_id = COALESCE(@p_namespace_id, (SELECT o.namespace_id FROM {{schema}}.jobs o WHERE o.job_id = @p_job_id))
    AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
ORDER BY c.checkpoint_name;
