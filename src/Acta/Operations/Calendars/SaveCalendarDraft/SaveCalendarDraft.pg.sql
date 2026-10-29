-- One batch, one implicit transaction; only the final SELECT returns rows. The namespace's schedule lock, exclusive,
-- serializes every catalog write in the namespace (docs/sql-internals.md, "Schedule lock"), so the latest version the
-- family read finds is still its latest when the draft lands. A namespace no worker has run in yet is created here.
DROP TABLE IF EXISTS pg_temp._save_calendar_draft_namespace;
DROP TABLE IF EXISTS pg_temp._save_calendar_draft_lock;
DROP TABLE IF EXISTS pg_temp._save_calendar_draft_family;
INSERT INTO {{schema}}.namespaces (namespace_name, namespace_status_code, created_at_utc, modified_at_utc, version)
SELECT @p_namespace_name, 10 /* NamespaceStatusCode.Active */, now(), now(), 0
WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name)
ON CONFLICT (namespace_name) DO NOTHING;

CREATE TEMP TABLE _save_calendar_draft_namespace ON COMMIT DROP AS
SELECT n.namespace_id
FROM {{schema}}.namespaces n
WHERE n.namespace_name = @p_namespace_name;

CREATE TEMP TABLE _save_calendar_draft_lock ON COMMIT DROP AS
SELECT 1 AS granted
FROM (
    SELECT pg_advisory_xact_lock(hashtext('{{schema}}.schedules'), t.namespace_id)
    FROM _save_calendar_draft_namespace t
) l;

-- A name is one family: a namespace's calendar cannot take a shared calendar's name, and a shared one cannot take a
-- name any namespace holds, so a schedule's plain name never resolves two ways.
CREATE TEMP TABLE _save_calendar_draft_family ON COMMIT DROP AS
SELECT
    t.namespace_id,
    COALESCE(MAX(c.version), 0) AS latest_version,
    MIN(c.calendar_role_code) AS family_role,
    (
        SELECT MIN(o.namespace_name)
        FROM {{schema}}.calendars x
        INNER JOIN {{schema}}.namespaces o ON o.namespace_id = x.namespace_id
        WHERE
            x.calendar_name = @p_calendar_name
            AND x.namespace_id <> t.namespace_id
            AND (t.namespace_id = 1 /* NameSyntax.SystemNamespaceId */ OR x.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
    ) AS clash_namespace
FROM _save_calendar_draft_namespace t
LEFT JOIN {{schema}}.calendars c ON c.namespace_id = t.namespace_id AND c.calendar_name = @p_calendar_name
GROUP BY t.namespace_id;

WITH saved AS (
    INSERT INTO {{schema}}.calendars (
        namespace_id,
        calendar_name,
        version,
        calendar_role_code,
        definition_format_id,
        definition,
        content_hash,
        calendar_status_code,
        created_at_utc,
        modified_at_utc
    )
    SELECT
        f.namespace_id,
        @p_calendar_name,
        @p_expected_latest_version + 1,
        @p_calendar_role_code,
        @p_definition_format_id,
        @p_definition,
        @p_content_hash,
        10 /* CalendarStatusCode.Draft */,
        now(),
        now()
    FROM _save_calendar_draft_family f
    WHERE
        f.latest_version = @p_expected_latest_version
        AND (f.family_role IS NULL OR f.family_role = @p_calendar_role_code)
        AND f.clash_namespace IS NULL
    RETURNING calendar_id, namespace_id, version, content_hash
),
audit AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        event_message, details_format_id, details
    )
    SELECT
        110 /* EventCode.CalendarDraftSaved */, now(), s.namespace_id,
        15 /* EntityCode.Calendar */, s.calendar_id,
        @p_event_message, @p_details_format_id, @p_details
    FROM saved s
)
SELECT
    CASE
        WHEN s.calendar_id IS NOT NULL THEN 1 /* ControlAction.Applied */
        WHEN f.latest_version <> @p_expected_latest_version THEN 5 /* ControlAction.VersionConflict */
        ELSE 3 /* ControlAction.Rejected */
    END::SMALLINT AS action,
    COALESCE(s.version, f.latest_version) AS version,
    s.content_hash,
    f.family_role,
    f.clash_namespace
FROM _save_calendar_draft_family f
LEFT JOIN saved s ON true;
