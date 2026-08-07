-- Exact-scope point read; the scope is inferred from the targets (none = Global, namespace alone =
-- Namespace, namespace + job name = Definition). An unregistered target simply matches no row.
SELECT
    s.name,
    s.value_format_id,
    s.value,
    s.description,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.settings s
WHERE
    s.name = @p_name
    AND (
        (@p_namespace_name IS NULL AND s.scope_code = 10 /* SettingScopeCode.Global */ AND s.scope_id IS NULL)
        OR (
            @p_namespace_name IS NOT NULL AND @p_job_name IS NULL AND s.scope_code = 30 /* SettingScopeCode.Namespace */
            AND s.scope_id = (
                SELECT n.id FROM {{schema}}.namespaces n
                WHERE n.name = @p_namespace_name
            )
        )
        OR (
            @p_job_name IS NOT NULL AND s.scope_code = 40 /* SettingScopeCode.Definition */
            AND s.scope_id = (
                SELECT d.id FROM {{schema}}.definitions d
                JOIN {{schema}}.namespaces n ON n.id = d.namespace_id
                WHERE n.name = @p_namespace_name AND d.name = @p_job_name
            )
        )
    );
