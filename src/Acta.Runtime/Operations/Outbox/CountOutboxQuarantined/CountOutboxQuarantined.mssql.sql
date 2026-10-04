SELECT COUNT(*) FROM {{table_ref}} WHERE status_code = 90 /* OutboxStatusCode.Quarantined */;
