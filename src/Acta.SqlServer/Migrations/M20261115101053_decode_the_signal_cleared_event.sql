SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO


CREATE OR ALTER VIEW {{schema}}.events_view AS
SELECT
    e.event_id,
    e.created_at_utc,
    ns.namespace_name,
    e.namespace_id,
    CASE e.entity_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'alert' WHEN 15 THEN 'calendar' WHEN 20 THEN 'checkpoint' WHEN 30 THEN 'definition' WHEN 40 THEN 'event' WHEN 50 THEN 'job' WHEN 60 THEN 'lane' WHEN 70 THEN 'lock' WHEN 80 THEN 'namespace' WHEN 90 THEN 'schedule' WHEN 100 THEN 'step' WHEN 110 THEN 'tag' WHEN 120 THEN 'tenant' WHEN 130 THEN 'worker' END AS entity,
    e.entity_code,
    e.entity_id,
    CASE e.event_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'tenant.suspended' WHEN 11 THEN 'tenant.resumed' WHEN 12 THEN 'tenant.updated' WHEN 20 THEN 'namespace.suspended' WHEN 21 THEN 'namespace.resumed' WHEN 22 THEN 'namespace.updated' WHEN 23 THEN 'namespace.deployed' WHEN 30 THEN 'definition.overrides-updated' WHEN 31 THEN 'definition.retired' WHEN 40 THEN 'job.execution-succeeded' WHEN 41 THEN 'job.execution-failed' WHEN 42 THEN 'job.execution-orphaned' WHEN 43 THEN 'job.execution-yielded' WHEN 44 THEN 'job.failed' WHEN 60 THEN 'job.suspended' WHEN 61 THEN 'job.rescheduled' WHEN 70 THEN 'job.cancelled' WHEN 71 THEN 'job.paused' WHEN 72 THEN 'job.resumed' WHEN 73 THEN 'job.restarted' WHEN 74 THEN 'job.reprioritized' WHEN 75 THEN 'job.purged' WHEN 76 THEN 'job.input-amended' WHEN 80 THEN 'job.signal-raised' WHEN 81 THEN 'job.state-reset' WHEN 82 THEN 'job.signal-cleared' WHEN 90 THEN 'job.note-recorded' WHEN 100 THEN 'schedule.paused' WHEN 101 THEN 'schedule.resumed' WHEN 102 THEN 'schedule.pause-expired' WHEN 104 THEN 'schedule.triggered' WHEN 105 THEN 'schedule.admitted' WHEN 106 THEN 'schedule.skipped' WHEN 107 THEN 'schedule.retimed' WHEN 108 THEN 'schedule.evaluation-failed' WHEN 109 THEN 'schedule.orphaned' WHEN 110 THEN 'calendar.draft-saved' WHEN 111 THEN 'calendar.published' WHEN 112 THEN 'calendar.archived' WHEN 120 THEN 'worker.started' WHEN 121 THEN 'worker.stopped' WHEN 122 THEN 'worker.died' WHEN 140 THEN 'alert.acknowledged' WHEN 141 THEN 'alert.resolved' WHEN 180 THEN 'outbox.requeued' WHEN 181 THEN 'outbox.discarded' END AS event,
    e.event_code,
    e.definition_id,
    d.definition_name,
    e.tenant_id,
    t.tenant_name,
    e.lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    e.worker_id,
    wkr.worker_ref,
    e.execution_number,
    e.duration_ms,
    e.event_message,
    e.actor,
    CASE e.details_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE CONCAT('custom-', e.details_format_id)
    END AS details_format,
    CASE
        WHEN
            e.details_format_id = 1 /* JobPayloadFormat.Json */
            THEN CAST(e.details AS VARCHAR(MAX)) COLLATE latin1_general_100_bin2_utf8
    END AS details_text
FROM {{schema}}.events AS e
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = e.namespace_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = e.definition_id
LEFT JOIN {{schema}}.tenants AS t
    ON t.tenant_id = COALESCE(e.tenant_id, CASE WHEN e.entity_code = 120 /* EntityCode.Tenant */ THEN e.entity_id END)
LEFT JOIN {{schema}}.jobs AS root ON root.job_id = e.lineage_root_job_id
LEFT JOIN {{schema}}.workers AS wkr ON wkr.worker_id = e.worker_id
GO


IF NOT EXISTS (SELECT 1 FROM {{schema}}.migrations WHERE name = 'M20261115101053_decode_the_signal_cleared_event')
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115101053_decode_the_signal_cleared_event');
GO
