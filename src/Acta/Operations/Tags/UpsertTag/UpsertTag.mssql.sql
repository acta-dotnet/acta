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
        IF
            EXISTS (
                SELECT 1 FROM @items i
                WHERE NOT EXISTS (
                    SELECT 1 FROM {{schema}}.tags t
                    WHERE t.entity_code = @p_entity_code AND t.entity_id = @entity_id AND t.tag_name = i.tag_name
                )
            )
            AND (SELECT COUNT(*) FROM {{schema}}.tags t WHERE t.entity_code = @p_entity_code AND t.entity_id = @entity_id) >= 32
            THROW 50000, 'A target may carry at most 32 tags.', 1;

        UPDATE t
        SET tag_value = i.tag_value, tag_value_search = i.tag_value_search
        FROM {{schema}}.tags t
        JOIN @items i ON i.tag_name = t.tag_name
        WHERE t.entity_code = @p_entity_code AND t.entity_id = @entity_id;

        INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
        SELECT @p_entity_code, @entity_id, i.tag_name, i.tag_value, i.tag_value_search
        FROM @items i
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tags t WHERE t.entity_code = @p_entity_code AND t.entity_id = @entity_id AND t.tag_name = i.tag_name
        );
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
