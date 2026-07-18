IF SCHEMA_ID(N'{{schema}}') IS NULL EXEC(N'CREATE SCHEMA {{schema}}');
IF OBJECT_ID(N'{{schema}}.migrations', N'U') IS NULL
CREATE TABLE {{schema}}.migrations (
    version          int          NOT NULL,
    name             varchar(256) NOT NULL,
    applied_at_utc   datetime2(3) DEFAULT SYSUTCDATETIME() NOT NULL,
    installed_schema varchar(64)  NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (version)
);
