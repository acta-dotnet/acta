SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @scope_id BIGINT, @namespace_id INT;
DECLARE @items TABLE (name VARCHAR(128) NOT NULL PRIMARY KEY, value NVARCHAR(128) NULL, value_search NVARCHAR(128) NULL);

INSERT INTO @items (name, value, value_search)
SELECT name, value, value_search
FROM OPENJSON (@p_items_json) WITH (name VARCHAR(128) '$.name', value NVARCHAR(128) '$.value', value_search NVARCHAR(128) '$.value_search');

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    IF @p_scope_code = 20 /* TagScopeCode.Tenant */
        SELECT @scope_id = CONVERT(BIGINT, t.id), @namespace_id = NULL
        FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
        WHERE t.tenant_name = @p_lookup_name;
    ELSE IF @p_scope_code = 30 /* TagScopeCode.Namespace */
        SELECT @scope_id = CONVERT(BIGINT, n.id), @namespace_id = n.id
        FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
        WHERE n.name = @p_lookup_name;
    ELSE IF @p_scope_code = 40 /* TagScopeCode.Definition */
        SELECT @scope_id = CONVERT(BIGINT, d.id), @namespace_id = d.namespace_id
        FROM {{schema}}.definitions d WITH (UPDLOCK, ROWLOCK)
        WHERE d.id = @p_lookup_id;
    ELSE IF @p_scope_code = 50 /* TagScopeCode.Job */
        SELECT @scope_id = CONVERT(BIGINT, j.id), @namespace_id = j.namespace_id
        FROM {{schema}}.jobs j WITH (UPDLOCK, ROWLOCK)
        WHERE j.id = @p_lookup_id;

    IF @scope_id IS NOT NULL
    BEGIN
        DELETE t
        FROM {{schema}}.tags t
        JOIN @items i ON i.name = t.name
        WHERE t.scope_code = @p_scope_code AND t.scope_id = @scope_id;
    END;

    SELECT CAST(CASE WHEN @scope_id IS NULL THEN 2 /* TagMutationAction.NotFound */ ELSE 1 /* TagMutationAction.Applied */ END AS TINYINT) AS action;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
