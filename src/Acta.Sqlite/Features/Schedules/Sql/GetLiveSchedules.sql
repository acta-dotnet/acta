SELECT t.id,
       t.name,
       t.expression_effective AS expression,
       t.time_zone_id_effective AS time_zone,
       t.misfire_strategy_code,
       t.expression_kind_code,
       t.next_run_at_utc,
       t.status_code,
       t.paused_until_utc,
       t.expression AS base_expression,
       t.time_zone_id AS base_time_zone
  FROM {{schema}}.schedules t
 WHERE t.job_id = @p_job_id
   AND t.orphaned_at_utc IS NULL
 ORDER BY t.name;
