CREATE TABLE IF NOT EXISTS {{schema}}.migrations (
    name TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL DEFAULT (STRFTIME('%Y-%m-%d %H:%M:%f', 'now')),
    CONSTRAINT pk_migrations PRIMARY KEY (name)
) STRICT;
