CREATE TABLE IF NOT EXISTS {{schema}}.migrations (
    name TEXT NOT NULL,
    applied_at_utc INTEGER NOT NULL DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)),
    CONSTRAINT pk_migrations PRIMARY KEY (name)
) STRICT;
