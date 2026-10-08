CREATE SCHEMA IF NOT EXISTS {{schema}};
CREATE TABLE IF NOT EXISTS {{schema}}.migrations (
    version INTEGER NOT NULL,
    name VARCHAR(256) NOT NULL,
    applied_at_utc TIMESTAMPTZ DEFAULT now() NOT NULL,
    installed_schema VARCHAR(64) NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (version)
);
