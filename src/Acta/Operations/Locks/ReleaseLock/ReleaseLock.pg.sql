CREATE OR REPLACE FUNCTION {{schema}}.release_lock(
    p_lock_key VARCHAR,
    p_hold_token UUID
)
RETURNS TABLE (hold_token UUID)
LANGUAGE sql
AS $$
    DELETE FROM {{schema}}.locks
    WHERE lock_key = p_lock_key AND hold_token = p_hold_token
    RETURNING hold_token;
$$;
