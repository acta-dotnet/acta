CREATE OR ALTER PROCEDURE {{schema}}.enqueue_batch
    @p_batch     {{schema}}.job_enqueue_batch     READONLY,
    @p_tag_batch {{schema}}.job_enqueue_tag_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

    -- Own a local transaction only when invoked outside one. Inside a caller's transaction (direct
    -- transactional enqueue) the entry count is > 0: run the work but neither commit nor roll back it;
    -- on error rethrow and let the caller roll back the whole transaction.
    DECLARE @entry_trancount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @resolved TABLE (
            ordinal         INT PRIMARY KEY,
            ns_id           SMALLINT NOT NULL,
            ns_status       TINYINT  NOT NULL,
            def_id          INT      NOT NULL,
            def_priority    SMALLINT NOT NULL,
            def_audit_level TINYINT  NOT NULL,
            def_status      TINYINT  NOT NULL,
            def_tenant_req  TINYINT  NOT NULL,
            tenant_id       INT      NULL
        );

        INSERT INTO @resolved (ordinal, ns_id, ns_status, def_id, def_priority, def_audit_level, def_status, def_tenant_req)
        SELECT b.ordinal, ns.id, ns.status_code, jd.id,
               jd.priority_code_effective,
               jd.audit_level_code_effective,
               jd.status_code,
               jd.tenant_requirement_code
          FROM @p_batch b
          INNER JOIN {{schema}}.namespaces ns ON ns.name = b.namespace_name
          INNER JOIN {{schema}}.definitions jd
                  ON jd.namespace_id = ns.id
                 AND jd.name = b.job_name;

        IF (SELECT COUNT(*) FROM @resolved) < (SELECT COUNT(*) FROM @p_batch)
        BEGIN
            THROW 50001, 'ACTA:ENQ_ROUTE_UNKNOWN:Enqueue rejected: one or more rows reference an unknown namespace or job. Has the owning worker run InitializeAsync yet?', 1;
        END;

        IF EXISTS (SELECT 1 FROM @resolved WHERE ns_status <> 10 /* JobNamespaceStatusCode.Active */)
        BEGIN
            THROW 50005, 'ACTA:ENQ_NS_SUSPENDED:Enqueue rejected: one or more rows reference a suspended namespace.', 1;
        END;

        IF EXISTS (SELECT 1 FROM @resolved WHERE def_status <> 10 /* JobDefinitionStatusCode.Active */)
        BEGIN
            THROW 50003, 'ACTA:ENQ_DEF_RETIRED:Enqueue rejected: the job definition is retired.', 1;
        END;

        IF EXISTS (SELECT 1 FROM @p_batch WHERE tenant_key IS NOT NULL)
        BEGIN
            UPDATE r
               SET tenant_id = t.id
              FROM @resolved r
              INNER JOIN @p_batch b ON b.ordinal = r.ordinal
              INNER JOIN {{schema}}.tenants t
                      ON t.tenant_key = b.tenant_key
                     AND t.status_code = 10 /* TenantStatusCode.Active */
             WHERE b.tenant_key IS NOT NULL;

            IF EXISTS (SELECT 1 FROM @p_batch b WHERE b.tenant_key IS NOT NULL
                         AND NOT EXISTS (SELECT 1 FROM {{schema}}.tenants t WHERE t.tenant_key = b.tenant_key))
            BEGIN
                THROW 50004, 'ACTA:ENQ_TENANT_UNKNOWN:Enqueue rejected: one or more rows reference an unknown tenant.', 1;
            END;
            IF EXISTS (SELECT 1 FROM @p_batch b JOIN {{schema}}.tenants t ON t.tenant_key = b.tenant_key
                       WHERE b.tenant_key IS NOT NULL AND t.status_code <> 10 /* TenantStatusCode.Active */)
            BEGIN
                THROW 50006, 'ACTA:ENQ_TENANT_SUSPENDED:Enqueue rejected: one or more rows reference a suspended tenant.', 1;
            END;
        END;

        DECLARE @existing TABLE (
            ordinal INT PRIMARY KEY,
            id      BIGINT NOT NULL,
            job_ref UNIQUEIDENTIFIER NOT NULL
        );

        -- Skip when no row has a key: the dedup probes' HOLDLOCK range locks (held to commit) would
        -- otherwise serialize every concurrent keyless enqueue on the namespace.
        IF EXISTS (SELECT 1 FROM @p_batch WHERE deduplication_key IS NOT NULL)
        BEGIN
            INSERT INTO @existing (ordinal, id, job_ref)
            SELECT b.ordinal, j.id, j.job_ref
              FROM @p_batch b
              INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, HOLDLOCK)
                      ON j.parent_id  = b.parent_id
                     AND j.deduplication_key = b.deduplication_key
             WHERE b.deduplication_key IS NOT NULL
               AND b.parent_id IS NOT NULL;

            INSERT INTO @existing (ordinal, id, job_ref)
            SELECT b.ordinal, j.id, j.job_ref
              FROM @p_batch b
              INNER JOIN @resolved r ON r.ordinal = b.ordinal
              INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, HOLDLOCK)
                      ON j.namespace_id = r.ns_id
                     AND j.deduplication_key       = b.deduplication_key
                     AND j.parent_id IS NULL
             WHERE b.deduplication_key IS NOT NULL
               AND b.parent_id IS NULL;
        END;

        DECLARE @parents TABLE (
            ordinal         INT PRIMARY KEY,
            lineage_root_id BIGINT NOT NULL,
            correlation_key  VARCHAR(64) NULL,
            tenant_id       INT NULL
        );

        INSERT INTO @parents (ordinal, lineage_root_id, correlation_key, tenant_id)
        SELECT b.ordinal, COALESCE(pj.lineage_root_id, pj.id), pj.correlation_key, pj.tenant_id
          FROM @p_batch b
          INNER JOIN {{schema}}.jobs pj WITH (UPDLOCK, ROWLOCK) ON pj.id = b.parent_id
          INNER JOIN {{schema}}.runtimes pr ON pr.job_id = pj.id
         WHERE b.parent_id IS NOT NULL
           AND pr.status_code NOT IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
         ORDER BY pj.id DESC;

        IF (SELECT COUNT(*) FROM @parents) < (SELECT COUNT(*) FROM @p_batch WHERE parent_id IS NOT NULL)
        BEGIN
            THROW 50002, 'Enqueue rejected: one or more child rows reference a missing or terminal parent job.', 1;
        END;

        IF EXISTS (
            SELECT 1
              FROM @resolved r
              INNER JOIN @p_batch b ON b.ordinal = r.ordinal
              LEFT JOIN @parents p  ON p.ordinal = b.ordinal
             WHERE r.def_tenant_req = 10 /* JobTenantRequirementCode.Required */
               AND r.tenant_id IS NULL
               AND p.tenant_id IS NULL
        )
        BEGIN
            THROW 50007, 'ACTA:ENQ_TENANT_REQUIRED:Enqueue rejected: one or more rows target a definition that requires a tenant and carry none.', 1;
        END;

        IF EXISTS (
            SELECT 1
              FROM @resolved r
              INNER JOIN @p_batch b ON b.ordinal = r.ordinal
             WHERE r.def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */
               AND b.tenant_key IS NOT NULL
        )
        BEGIN
            THROW 50008, 'ACTA:ENQ_TENANT_FORBIDDEN:Enqueue rejected: one or more rows target a definition that forbids a tenant and name one.', 1;
        END;

        IF EXISTS (
            SELECT 1
              FROM @resolved r
              INNER JOIN @p_batch b ON b.ordinal = r.ordinal
              INNER JOIN @parents p ON p.ordinal = b.ordinal
             WHERE r.tenant_id IS NOT NULL
               AND p.tenant_id IS NOT NULL
               AND r.tenant_id <> p.tenant_id
               AND b.tenant_override = 0
        )
        BEGIN
            THROW 50009, 'ACTA:ENQ_TENANT_MISMATCH:Enqueue rejected: one or more child rows name a TenantKey that differs from the parent tenant without an explicit override.', 1;
        END;

        DECLARE @map TABLE (
            job_ref UNIQUEIDENTIFIER PRIMARY KEY,
            id      BIGINT NOT NULL
        );

        INSERT INTO {{schema}}.jobs (
            job_ref, lineage_root_id, parent_id,
            deduplication_key, correlation_key,
            namespace_id, definition_id, tenant_id,
            input_format_id, input,
            exclusive_key, audit_level_code,
            created_at_utc)
        OUTPUT inserted.job_ref, inserted.id INTO @map (job_ref, id)
        SELECT
            b.job_ref, p.lineage_root_id, b.parent_id,
            b.deduplication_key, COALESCE(b.correlation_key, p.correlation_key),
            r.ns_id, r.def_id,
            CASE WHEN r.def_tenant_req = 20 /* JobTenantRequirementCode.Forbidden */ THEN NULL
                 ELSE COALESCE(r.tenant_id, p.tenant_id) END,
            b.input_format_id, b.input,
            b.exclusive_key, r.def_audit_level,
            @now
          FROM @p_batch b
          INNER JOIN @resolved r ON r.ordinal = b.ordinal
          LEFT JOIN @parents p   ON p.ordinal = b.ordinal
          LEFT JOIN @existing e  ON e.ordinal = b.ordinal
         WHERE e.ordinal IS NULL;

        INSERT INTO {{schema}}.runtimes (
            job_id, namespace_id, status_code, priority_code, next_run_at_utc,
            execution_number, failure_count, retention_until_utc,
            modified_at_utc, version)
        SELECT
            m.id, r.ns_id, 10 /* JobStatusCode.Ready */,
            COALESCE(b.priority_override, r.def_priority),
            COALESCE(b.next_run_at_utc, DATEADD(SECOND, COALESCE(b.delay_seconds, 0), @now)),
            0, 0, NULL,
            @now, 0
          FROM @map m
          INNER JOIN @p_batch b  ON b.job_ref = m.job_ref
          INNER JOIN @resolved r ON r.ordinal = b.ordinal;

        INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
        SELECT 50 /* TagScopeCode.Job */, m.id, r.ns_id, t.name, t.value, t.value_search
          FROM @p_tag_batch t
          INNER JOIN @p_batch b ON b.ordinal = t.ordinal
          INNER JOIN @map m     ON m.job_ref = b.job_ref
          INNER JOIN @resolved  r ON r.ordinal = t.ordinal;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;

    SELECT b.ordinal,
           COALESCE(m.id, e.id) AS job_id,
           COALESCE(m.job_ref, e.job_ref) AS job_ref,
            CASE WHEN m.id IS NOT NULL
                 THEN 1 /* JobEnqueueAction.Inserted */
                 ELSE 2 /* JobEnqueueAction.Deduplicated */ END AS action
      FROM @p_batch b
      LEFT JOIN @map m     ON m.job_ref = b.job_ref
      LEFT JOIN @existing  e ON e.ordinal = b.ordinal
     ORDER BY b.ordinal;
END;
GO
