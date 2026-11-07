SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Runs in the deploy's transaction, which owns the application locks below and ends them.
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @lock_result INT;
DECLARE @lock_resource NVARCHAR(255);

DECLARE @names TABLE (namespace_name VARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
INSERT INTO @names (namespace_name)
SELECT CAST(j.value AS VARCHAR(128)) FROM OPENJSON (@p_namespace_names) j;

-- A name has no row to lock before its first deploy: two first deploys of one namespace queue on the name, in name
-- order, and the second finds the first one's row instead of failing on the unique name.
DECLARE @name VARCHAR(128) = (SELECT MIN(d.namespace_name) FROM @names d);
WHILE @name IS NOT NULL
BEGIN
    SET @lock_resource = N'{{schema}}.namespaces.' + @name;
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:NAMESPACE_LOCK:The namespace name lock was not granted (deadlock victim or cancelled).', 1;
    SET @name = (SELECT MIN(d.namespace_name) FROM @names d WHERE d.namespace_name > @name);
END

-- Inserted only when the name is absent, so a deploy of an existing namespace takes no identity value.
INSERT INTO {{schema}}.namespaces
(namespace_name, namespace_status_code, created_at_utc, modified_at_utc, version)
SELECT d.namespace_name, @p_namespace_status_code, @now, @now, 0
FROM @names d
WHERE NOT EXISTS (
    SELECT 1 FROM {{schema}}.namespaces n
    WHERE n.namespace_name = d.namespace_name
);

-- Every namespace's schedule lock, exclusive, in namespace id order, before any lock on a row another statement can
-- reach (docs/sql-internals.md, "Schedule lock"): a calendar publication takes several in that order too. Two deploys
-- of one namespace serialize here, and each reads the current version below and writes under it.
DECLARE @locks TABLE (namespace_id INT NOT NULL PRIMARY KEY);
INSERT INTO @locks (namespace_id)
SELECT n.namespace_id
FROM {{schema}}.namespaces n
INNER JOIN @names d ON d.namespace_name = n.namespace_name;
DECLARE @ns INT = (SELECT MIN(l.namespace_id) FROM @locks l);
WHILE @ns IS NOT NULL
BEGIN
    SET @lock_resource = N'{{schema}}.schedules.' + CAST(@ns AS NVARCHAR(12));
    EXEC @lock_result = sys.sp_getapplock @lock_resource, 'Exclusive', 'Transaction', -1;
    IF @lock_result < 0
        THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
    SET @ns = (SELECT MIN(l.namespace_id) FROM @locks l WHERE l.namespace_id > @ns);
END

SELECT
    n.namespace_name,
    n.namespace_id,
    n.deployment_version AS current_deployment_version,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM {{schema}}.deployments d
        WHERE d.namespace_id = n.namespace_id AND d.deployment_version = @p_deployment_version
    ) THEN 1 ELSE 0 END AS BIT) AS is_deployed
FROM {{schema}}.namespaces n
INNER JOIN @locks l ON l.namespace_id = n.namespace_id;
