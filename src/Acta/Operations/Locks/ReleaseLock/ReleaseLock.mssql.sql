DELETE FROM {{schema}}.locks
OUTPUT DELETED.hold_token
WHERE lock_key = @p_lock_key AND hold_token = @p_hold_token;
