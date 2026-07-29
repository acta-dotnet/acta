CREATE TABLE IF NOT EXISTS {{schema}}.migrations (
    version          integer NOT NULL,
    name             text    NOT NULL,
    applied_at_utc   text    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f', 'now')),
    installed_schema text    NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (version)
) STRICT;
