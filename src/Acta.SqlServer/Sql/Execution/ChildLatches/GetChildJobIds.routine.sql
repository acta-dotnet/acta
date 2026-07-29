CREATE OR ALTER PROCEDURE {{schema}}.get_child_job_ids
    @p_parent_id BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT j.id AS job_id
      FROM {{schema}}.jobs j
     WHERE j.parent_id = @p_parent_id;
END;
GO
