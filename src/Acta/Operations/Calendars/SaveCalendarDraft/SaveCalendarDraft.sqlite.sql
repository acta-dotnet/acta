-- SQLite runs one writer at a time, so the latest version read here is still the family's latest when the draft lands.
-- A namespace no worker has run in yet is created here, so a calendar can be drafted before the first deploy.
DROP TABLE IF EXISTS temp._save_calendar_draft;

INSERT INTO {{schema}}.namespaces (namespace_name, namespace_status_code, created_at_utc, modified_at_utc, version)
SELECT @p_namespace_name, 10 /* NamespaceStatusCode.Active */, {{now}}, {{now}}, 0
WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.namespaces n WHERE n.namespace_name = @p_namespace_name);

-- A name is one family: a namespace's calendar cannot take a shared calendar's name, and a shared one cannot take a
-- name any namespace holds, so a schedule's plain name never resolves two ways.
CREATE TEMP TABLE _save_calendar_draft AS
SELECT
    f.namespace_id,
    f.latest_version,
    f.family_role,
    f.latest_content_hash,
    f.clash_namespace,
    f.unchanged,
    NOT f.unchanged
        AND f.latest_version = @p_expected_latest_version
        AND (f.family_role IS NULL OR f.family_role = @p_calendar_role_code)
        AND f.clash_namespace IS NULL AS applies
FROM (
    SELECT
        n.namespace_id,
        COALESCE(l.version, 0) AS latest_version,
        -- A family keeps its first role, so its latest version's role is the family's.
        l.calendar_role_code AS family_role,
        l.content_hash AS latest_content_hash,
        -- A definition the latest version holds, unless that version is Archived, saves nothing and answers that version.
        COALESCE(
            l.content_hash = @p_content_hash
            AND l.calendar_role_code = @p_calendar_role_code
            AND l.calendar_status_code <> 240 /* CalendarStatusCode.Archived */,
            0
        ) AS unchanged,
        (
            SELECT MIN(o.namespace_name)
            FROM {{schema}}.calendars x
            INNER JOIN {{schema}}.namespaces o ON o.namespace_id = x.namespace_id
            WHERE
                x.calendar_name = @p_calendar_name
                AND x.namespace_id <> n.namespace_id
                AND (n.namespace_id = 1 /* NameSyntax.SystemNamespaceId */ OR x.namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
        ) AS clash_namespace
    FROM {{schema}}.namespaces n
    LEFT JOIN {{schema}}.calendars l
        ON
            l.namespace_id = n.namespace_id
            AND l.calendar_name = @p_calendar_name
            AND l.version = (
                SELECT MAX(c.version)
                FROM {{schema}}.calendars c
                WHERE
                    c.namespace_id = n.namespace_id
                    AND c.calendar_name = @p_calendar_name
            )
    WHERE n.namespace_name = @p_namespace_name
) f;

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
    modified_at_utc)
SELECT
    f.namespace_id,
    @p_calendar_name,
    @p_expected_latest_version + 1,
    @p_calendar_role_code,
    @p_definition_format_id,
    @p_definition,
    @p_content_hash,
    10 /* CalendarStatusCode.Draft */,
    {{now}},
    {{now}}
FROM temp._save_calendar_draft f
WHERE f.applies = 1;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    event_message,
    details_format_id,
    details)
SELECT
    110 /* EventCode.CalendarDraftSaved */,
    {{now}},
    c.namespace_id,
    15 /* EntityCode.Calendar */,
    c.calendar_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._save_calendar_draft f
JOIN {{schema}}.calendars c
    ON
        c.namespace_id = f.namespace_id
        AND c.calendar_name = @p_calendar_name
        AND c.version = @p_expected_latest_version + 1
WHERE f.applies = 1;

SELECT
    CASE
        WHEN f.applies = 1 THEN 1 /* ControlAction.Applied */
        WHEN f.unchanged = 1 THEN 6 /* ControlAction.AlreadyInState */
        WHEN f.latest_version <> @p_expected_latest_version THEN 5 /* ControlAction.VersionConflict */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    CASE WHEN f.applies = 1 THEN @p_expected_latest_version + 1 ELSE f.latest_version END AS version,
    CASE
        WHEN f.applies = 1 THEN @p_content_hash
        WHEN f.unchanged = 1 THEN f.latest_content_hash
    END AS content_hash,
    f.family_role,
    f.clash_namespace
FROM temp._save_calendar_draft f;
