SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @entity_id BIGINT;
DECLARE @items TABLE (tag_name VARCHAR(128) NOT NULL PRIMARY KEY, tag_value NVARCHAR(128) NULL, tag_value_search NVARCHAR(128) NULL);

INSERT INTO @items (tag_name, tag_value, tag_value_search)
SELECT tag_name, tag_value, tag_value_search
FROM OPENJSON (@p_items_json) WITH (
    tag_name VARCHAR(128) '$.tag_name',
    tag_value NVARCHAR(128) '$.tag_value',
    tag_value_search NVARCHAR(128) '$.tag_value_search'
);

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    IF @p_entity_code = 120 /* EntityCode.Tenant */
        SELECT @entity_id = CONVERT(BIGINT, t.tenant_id)
        FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
        WHERE t.tenant_name = @p_lookup_name;
    ELSE IF @p_entity_code = 80 /* EntityCode.Namespace */
        SELECT @entity_id = CONVERT(BIGINT, n.namespace_id)
        FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
        WHERE n.namespace_name = @p_lookup_name;
    ELSE IF @p_entity_code = 30 /* EntityCode.Definition */
        SELECT @entity_id = CONVERT(BIGINT, d.definition_id)
        FROM {{schema}}.definitions d WITH (UPDLOCK, ROWLOCK)
        WHERE d.definition_id = @p_lookup_id;
    ELSE IF @p_entity_code = 50 /* EntityCode.Job */
        SELECT @entity_id = CONVERT(BIGINT, j.job_id)
        FROM {{schema}}.jobs j WITH (UPDLOCK, ROWLOCK)
        WHERE j.job_id = @p_lookup_id;

    IF @entity_id IS NOT NULL
    BEGIN
        DELETE t
        FROM {{schema}}.tags t
        JOIN @items i ON i.tag_name = t.tag_name
        WHERE t.entity_code = @p_entity_code AND t.entity_id = @entity_id;
    END;

    SELECT
        CAST(CASE WHEN @entity_id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 1 /* ControlAction.Applied */ END AS TINYINT) AS action;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
