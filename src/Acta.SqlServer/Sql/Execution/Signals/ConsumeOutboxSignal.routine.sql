-- Version-CAS consume of an applied operator command: a miss means a newer command superseded the row
-- mid-apply and survives for the next tick. See IOutboxSignalStore.ConsumeAsync.
CREATE OR ALTER PROCEDURE {{schema}}.consume_outbox_signal
    @p_job_id BIGINT,
    @p_name VARCHAR(128),
    @p_expected_version INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DELETE FROM {{schema}}.checkpoints
    WHERE
        job_id = @p_job_id
        AND kind_code = 20 /* JobCheckpointKindCode.Signal */
        AND name = @p_name
        AND version = @p_expected_version;

    SELECT CAST(@@ROWCOUNT AS BIGINT) AS consumed;
END
