-- The applying tick's read of one pending operator command; empty when the inbox slot is free.
CREATE OR ALTER PROCEDURE {{schema}}.get_outbox_signal
    @p_job_id BIGINT,
    @p_name VARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.value_format_id, c.value, c.version
    FROM {{schema}}.checkpoints c
    WHERE
        c.job_id = @p_job_id
        AND c.kind_code = 20 /* JobCheckpointKindCode.Signal */
        AND c.name = @p_name
        AND c.status_code = 20 /* JobCheckpointStatusCode.Set */;
END
