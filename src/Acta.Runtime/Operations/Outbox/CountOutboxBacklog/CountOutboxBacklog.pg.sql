SELECT COUNT(*) FROM {{table_ref}} WHERE status_code = 10 /* OutboxStatusCode.Pending */;
