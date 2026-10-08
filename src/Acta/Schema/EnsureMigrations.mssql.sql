IF SCHEMA_ID(N'{{schema}}') IS NULL EXEC (N'CREATE SCHEMA {{schema}}');
IF OBJECT_ID(N'{{schema}}.migrations', N'U') IS NULL
    CREATE TABLE {{schema}}.migrations (
        version INT NOT NULL,
        name VARCHAR(256) NOT NULL,
        applied_at_utc DATETIME2(3) DEFAULT SYSUTCDATETIME() NOT NULL,
        installed_schema VARCHAR(64) NOT NULL,
        CONSTRAINT pk_migrations PRIMARY KEY (version)
    );
