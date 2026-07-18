CREATE SCHEMA IF NOT EXISTS {{schema}};
CREATE TABLE IF NOT EXISTS {{schema}}.migrations (
    version          integer      NOT NULL,
    name             varchar(256) NOT NULL,
    applied_at_utc   timestamptz  DEFAULT now() NOT NULL,
    installed_schema varchar(64)  NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (version)
);
