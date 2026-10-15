CREATE SCHEMA IF NOT EXISTS {{schema}};
CREATE TABLE IF NOT EXISTS {{schema}}.migrations (
    name VARCHAR(256) NOT NULL,
    applied_at_utc TIMESTAMPTZ DEFAULT now() NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (name)
);
