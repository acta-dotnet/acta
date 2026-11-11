SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Runs in the deploy's transaction, under the namespace's schedule lock the deploy took at its start, so no other
-- deploy inserts the row between the probe and the insert. With no version (a start under MigrateOnStartup) only the
-- namespace's owner and description are written.
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

INSERT INTO {{schema}}.deployments (namespace_id, deployment_version, created_at_utc)
SELECT @p_namespace_id, @p_deployment_version, @now
WHERE
    @p_deployment_version IS NOT NULL
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.deployments
        WHERE namespace_id = @p_namespace_id AND deployment_version = @p_deployment_version
    );

-- An unchanged namespace writes nothing, so a repeated deploy leaves its version and modified_at_utc: EXCEPT compares
-- NULL-safely, and the binary collation sees a case edit of the owner or the description. A value the build declares
-- is the build's, and every deploy rewrites it; one it does not declare is the operator's.
UPDATE n
SET
    deployment_version = COALESCE(@p_deployment_version, n.deployment_version),
    owner_team = COALESCE(@p_owner_team, n.owner_team),
    description = COALESCE(@p_description, n.description),
    modified_at_utc = @now,
    version = n.version + 1
FROM {{schema}}.namespaces AS n
WHERE
    n.namespace_id = @p_namespace_id
    AND EXISTS (
        SELECT
            n.deployment_version,
            n.owner_team COLLATE Latin1_General_100_BIN2,
            n.description COLLATE Latin1_General_100_BIN2
        EXCEPT
        SELECT
            COALESCE(@p_deployment_version, n.deployment_version),
            COALESCE(@p_owner_team, n.owner_team),
            COALESCE(@p_description, n.description)
    );

-- The evidence of the deploy: one event per recorded run, also for a run that changed nothing.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    event_message, details_format_id, details
)
SELECT
    23 /* EventCode.NamespaceDeployed */, @now, @p_namespace_id,
    80 /* EntityCode.Namespace */, @p_namespace_id,
    @p_event_message, @p_details_format_id, @p_details
WHERE @p_deployment_version IS NOT NULL;
