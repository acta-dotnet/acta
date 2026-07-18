UPDATE {{schema}}.alerts
   SET delivery_status_code = @p_delivery_status_code,
       retry_count          = @p_retry_count,
       retry_after_utc      = @p_retry_after_utc,
       modified_at_utc      = SYSUTCDATETIME(),
       version              = version + 1
 WHERE id = @p_id;
