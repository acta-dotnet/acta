DROP TABLE IF EXISTS temp._due_timer;

CREATE TEMP TABLE _due_timer AS
SELECT 1 AS due
  FROM {{schema}}.checkpoints jt
 WHERE jt.job_id = @p_job_id AND jt.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND jt.name = @p_name
   AND jt.state_code = 10 /* JobCheckpointStateCode.Pending */
   AND jt.due_at_utc <= {{now}};

UPDATE {{schema}}.runtimes
   SET next_run_at_utc = NULL,
       modified_at_utc = {{now}},
       version = version + 1
 WHERE job_id = @p_job_id
   AND EXISTS (SELECT 1 FROM temp._due_timer);

UPDATE {{schema}}.checkpoints
   SET state_code = 100 /* JobCheckpointStateCode.Consumed */,
       modified_at_utc = {{now}},
       version = version + 1
 WHERE job_id = @p_job_id AND kind_code = 30 /* JobCheckpointKindCode.Timer */ AND name = @p_name
   AND state_code = 10 /* JobCheckpointStateCode.Pending */
   AND EXISTS (SELECT 1 FROM temp._due_timer);

INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, state_code, due_at_utc, created_at_utc, modified_at_utc, version)
SELECT @p_job_id, 30 /* JobCheckpointKindCode.Timer */, @p_name, 10 /* JobCheckpointStateCode.Pending */,
       COALESCE(@p_resume_at_utc, {{now}} + (@p_delay_seconds) * 1000),
       {{now}}, {{now}}, 0
WHERE NOT EXISTS (
        SELECT 1 FROM {{schema}}.checkpoints jt
        WHERE jt.job_id = @p_job_id AND jt.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND jt.name = @p_name)
  AND COALESCE(@p_resume_at_utc, {{now}} + (@p_delay_seconds) * 1000) > {{now}}
  AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.checkpoints jt2
        WHERE jt2.job_id = @p_job_id AND jt2.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND jt2.state_code = 10 /* JobCheckpointStateCode.Pending */);

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM temp._due_timer) THEN 2 /* SleepOutcome.Continue */
        WHEN jt.state_code = 10 /* JobCheckpointStateCode.Pending */ THEN 1 /* SleepOutcome.Suspend */
        WHEN jt.state_code IS NOT NULL THEN 2 /* SleepOutcome.Continue */
        WHEN COALESCE(@p_resume_at_utc, {{now}} + (@p_delay_seconds) * 1000) > {{now}}
             AND EXISTS (SELECT 1 FROM {{schema}}.checkpoints x WHERE x.job_id = @p_job_id AND x.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND x.state_code = 10 /* JobCheckpointStateCode.Pending */) THEN 3 /* SleepOutcome.Reject */
        ELSE 2 /* SleepOutcome.Continue */
    END AS outcome_code,
    CASE
        WHEN jt.state_code = 10 /* JobCheckpointStateCode.Pending */ THEN jt.due_at_utc
        ELSE NULL
    END AS due_at_utc
FROM (SELECT @p_job_id AS jid) probe
LEFT JOIN {{schema}}.checkpoints jt ON jt.job_id = @p_job_id AND jt.kind_code = 30 /* JobCheckpointKindCode.Timer */ AND jt.name = @p_name;
