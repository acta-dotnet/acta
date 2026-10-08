CREATE TABLE IF NOT EXISTS {{schema}}.migrations (
    version INTEGER NOT NULL,
    name TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL DEFAULT (STRFTIME('%Y-%m-%d %H:%M:%f', 'now')),
    installed_schema TEXT NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (version)
) STRICT;
