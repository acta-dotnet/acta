UPDATE {{schema}}.workers
   SET last_seen_at_utc = {{now}},
       status_code      = CASE WHEN @p_draining = 1 AND status_code = 10 /* WorkerStatusCode.Active */
                               THEN 80 /* WorkerStatusCode.Draining */ ELSE status_code END,
       modified_at_utc  = {{now}},
       version          = version + 1
 WHERE id = @p_leased_by_worker_id;

UPDATE {{schema}}.runtimes
   SET lease_expires_at_utc = {{now}} + (@p_lease_ttl_seconds) * 1000
 WHERE leased_by_worker_id = @p_leased_by_worker_id
   AND status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
RETURNING job_id;
