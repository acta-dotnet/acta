-- One batch in the caller's transaction, under the namespace's schedule lock the deploy took at its start. With no
-- version (a start under MigrateOnStartup) only the namespace's owner and description are written.
INSERT INTO {{schema}}.deployments (namespace_id, deployment_version, created_at_utc)
SELECT @p_namespace_id, @p_deployment_version, now()
WHERE @p_deployment_version IS NOT NULL
ON CONFLICT (namespace_id, deployment_version) DO NOTHING;

-- An unchanged namespace writes nothing, so a repeated deploy leaves its version and modified_at_utc.
-- A value the build declares is the build's, and every deploy rewrites it; one it does not declare is the operator's.
UPDATE {{schema}}.namespaces SET
    deployment_version = COALESCE(@p_deployment_version, deployment_version),
    owner_team = COALESCE(@p_owner_team, owner_team),
    description = COALESCE(@p_description, description),
    modified_at_utc = now(),
    version = version + 1
WHERE
    namespace_id = @p_namespace_id
    AND (
        deployment_version IS DISTINCT FROM COALESCE(@p_deployment_version, deployment_version)
        OR owner_team IS DISTINCT FROM COALESCE(@p_owner_team, owner_team)
        OR description IS DISTINCT FROM COALESCE(@p_description, description)
    );

-- The evidence of the deploy: one event per recorded run, also for a run that changed nothing.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    event_message, details_format_id, details
)
SELECT
    23 /* EventCode.NamespaceDeployed */, now(), @p_namespace_id,
    80 /* EntityCode.Namespace */, @p_namespace_id,
    @p_event_message, @p_details_format_id, @p_details
WHERE @p_deployment_version IS NOT NULL;
