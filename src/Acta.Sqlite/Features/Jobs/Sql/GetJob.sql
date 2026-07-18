SELECT j.id, j.job_ref,
       j.lineage_root_id, lroot.job_ref AS lineage_root_job_ref,
       j.parent_id, pjob.job_ref AS parent_job_ref,
       j.deduplication_key, j.correlation_key,
       ns.name AS namespace_name, jd.name AS job_name,
       r.status_code, r.priority_code,
       r.execution_number, r.failure_count,
       j.input_format_id,
       r.next_run_at_utc,
       r.leased_by_worker_id, r.lease_expires_at_utc,
       j.exclusive_key, r.retention_until_utc,
       j.created_at_utc, r.modified_at_utc,
       j.tenant_id
  FROM {{schema}}.jobs j
  INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
  INNER JOIN {{schema}}.namespaces  ns ON ns.id = j.namespace_id
  INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
  LEFT JOIN {{schema}}.jobs pjob ON pjob.id = j.parent_id
  LEFT JOIN {{schema}}.jobs lroot ON lroot.id = j.lineage_root_id
 WHERE j.id = @p_id;
