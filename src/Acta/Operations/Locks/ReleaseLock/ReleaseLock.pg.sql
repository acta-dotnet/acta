DELETE FROM {{schema}}.locks
WHERE lock_key = @p_lock_key AND hold_token = @p_hold_token
RETURNING hold_token;
