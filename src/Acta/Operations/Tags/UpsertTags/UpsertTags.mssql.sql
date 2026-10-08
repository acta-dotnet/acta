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
        FROM {{schema}}.tenants t WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE t.tenant_key = @p_lookup_name;
    ELSE IF @p_scope_code = 30 /* TagScopeCode.Namespace */
        SELECT @scope_id = CONVERT(BIGINT, n.id), @namespace_id = n.id
        FROM {{schema}}.namespaces n WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE n.name = @p_lookup_name;
    ELSE IF @p_scope_code = 40 /* TagScopeCode.Definition */
        SELECT @scope_id = CONVERT(BIGINT, d.id), @namespace_id = d.namespace_id
        FROM {{schema}}.definitions d WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE d.id = @p_lookup_id;
    ELSE IF @p_scope_code = 50 /* TagScopeCode.Job */
        SELECT @scope_id = CONVERT(BIGINT, j.id), @namespace_id = j.namespace_id
        FROM {{schema}}.jobs j WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE j.id = @p_lookup_id;
    ELSE IF @p_scope_code = 60 /* TagScopeCode.Schedule */
        SELECT @scope_id = CONVERT(BIGINT, s.id), @namespace_id = s.namespace_id
        FROM {{schema}}.schedules s WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE s.job_id = @p_lookup_id AND s.name = @p_lookup_name;
    ELSE IF @p_scope_code = 70 /* TagScopeCode.Worker */
        SELECT @scope_id = CONVERT(BIGINT, w.id), @namespace_id = w.namespace_id
        FROM {{schema}}.workers w WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE w.id = @p_lookup_id;
    ELSE IF @p_scope_code = 80 /* TagScopeCode.Alert */
        SELECT @scope_id = CONVERT(BIGINT, a.id), @namespace_id = a.namespace_id
        FROM {{schema}}.alerts a WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE a.id = @p_lookup_id;
    ELSE IF @p_scope_code = 90 /* TagScopeCode.Event */
        SELECT @scope_id = CONVERT(BIGINT, e.id), @namespace_id = e.namespace_id
        FROM {{schema}}.events e WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE e.id = @p_lookup_id;

    IF @scope_id IS NOT NULL
    BEGIN
        IF
            EXISTS (
                SELECT 1 FROM @items i
                WHERE NOT EXISTS (
                    SELECT 1 FROM {{schema}}.tags t WHERE t.scope_code = @p_scope_code AND t.scope_id = @scope_id AND t.name = i.name
                )
            )
            AND (SELECT COUNT(*) FROM {{schema}}.tags t WHERE t.scope_code = @p_scope_code AND t.scope_id = @scope_id) >= 32
            THROW 50000, 'A target may carry at most 32 tags.', 1;

        UPDATE t
        SET namespace_id = @namespace_id, value = i.value, value_search = i.value_search
        FROM {{schema}}.tags t
        JOIN @items i ON i.name = t.name
        WHERE t.scope_code = @p_scope_code AND t.scope_id = @scope_id;

        INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
        SELECT @p_scope_code, @scope_id, @namespace_id, i.name, i.value, i.value_search
        FROM @items i
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tags t WHERE t.scope_code = @p_scope_code AND t.scope_id = @scope_id AND t.name = i.name
        );
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
