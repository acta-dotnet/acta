-- Version-CAS consume of an applied operator command: a miss means a newer command superseded the row
-- mid-apply and survives for the next tick. See IOutboxSignalStore.ConsumeAsync.
DELETE FROM {{schema}}.checkpoints
WHERE
    job_id = @p_job_id
    AND kind_code = 20 /* JobCheckpointKindCode.Signal */
    AND name = @p_name
    AND version = @p_expected_version;

SELECT CHANGES() AS consumed;
