SELECT COUNT(*) FROM {{table_ref}} WHERE outbox_status_code = 90 /* OutboxStatusCode.Quarantined */;
