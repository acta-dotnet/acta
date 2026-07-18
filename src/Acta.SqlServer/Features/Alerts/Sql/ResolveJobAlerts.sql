UPDATE {{schema}}.alerts
   SET resolved_at_utc = SYSUTCDATETIME(),
       modified_at_utc = SYSUTCDATETIME(),
       version         = version + 1
 WHERE namespace_id  = @p_namespace_id
   AND job_id            = @p_job_id
   AND origin_code       = 10 /* AlertOriginCode.Automatic */
   AND kind_code IN (10 /* AlertKindCode.FirstFailure */, 20 /* AlertKindCode.ThresholdReached */, 30 /* AlertKindCode.FinalFailure */)
   AND resolved_at_utc IS NULL;
