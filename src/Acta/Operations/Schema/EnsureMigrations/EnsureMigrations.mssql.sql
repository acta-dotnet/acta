IF SCHEMA_ID(N'{{schema}}') IS NULL EXEC (N'CREATE SCHEMA {{schema}}');
IF OBJECT_ID(N'{{schema}}.migrations', N'U') IS NULL
    CREATE TABLE {{schema}}.migrations (
        name VARCHAR(256) NOT NULL,
        applied_at_utc DATETIME2(3) DEFAULT SYSUTCDATETIME() NOT NULL,
        CONSTRAINT pk_migrations PRIMARY KEY (name)
    );
