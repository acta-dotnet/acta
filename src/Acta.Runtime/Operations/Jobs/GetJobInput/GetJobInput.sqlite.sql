SELECT
    input_format_id,
    input
FROM {{schema}}.jobs
WHERE id = @p_id;
