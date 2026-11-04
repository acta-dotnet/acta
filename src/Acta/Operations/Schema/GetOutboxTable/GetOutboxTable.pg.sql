SELECT EXISTS (SELECT 1 FROM pg_class WHERE oid = to_regclass('{{table_ref}}') AND relkind IN ('r', 'p'));
