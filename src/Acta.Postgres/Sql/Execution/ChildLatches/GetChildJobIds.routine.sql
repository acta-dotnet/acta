CREATE OR REPLACE FUNCTION {{schema}}.get_child_job_ids(
    p_parent_id BIGINT
)
RETURNS TABLE (job_id BIGINT)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT j.id
    FROM {{schema}}.jobs j
    WHERE j.parent_id = p_parent_id;
END;
$$;
