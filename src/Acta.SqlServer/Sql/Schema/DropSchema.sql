-- SQL Server has no `DROP SCHEMA ... CASCADE`: drop installed routines, FKs, views, tables, sequences,
-- and user-defined types ourselves (one sp_executesql block). Order matters - routines reference TVP
-- types in their signatures, so routines drop before TVPs; FKs and views go first so tables drop freely.
DECLARE @sql NVARCHAR(MAX) = N'';

-- Routines (installed by the MNNN migration) pin TVP types via their @-parameters; drop these
-- first so the type drops below succeed.
SELECT @sql += N'DROP PROCEDURE {{schema}}.' + QUOTENAME(name) + N';' + CHAR(10)
FROM sys.procedures
WHERE SCHEMA_NAME(schema_id) = N'{{schema}}';
SELECT @sql += N'DROP FUNCTION {{schema}}.' + QUOTENAME(name) + N';' + CHAR(10)
FROM sys.objects
WHERE type IN ('FN', 'IF', 'TF') AND SCHEMA_NAME(schema_id) = N'{{schema}}';
IF LEN(@sql) > 0 EXEC sp_executesql @sql;

SET @sql = N'';
SELECT @sql += N'ALTER TABLE {{schema}}.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';' + CHAR(10)
FROM sys.foreign_keys fk
JOIN sys.tables t ON t.object_id = fk.parent_object_id
WHERE SCHEMA_NAME(t.schema_id) = N'{{schema}}';
IF LEN(@sql) > 0 EXEC sp_executesql @sql;

SET @sql = N'';
SELECT @sql += N'DROP VIEW {{schema}}.' + QUOTENAME(name) + N';' + CHAR(10) FROM sys.views
WHERE SCHEMA_NAME(schema_id) = N'{{schema}}';
SELECT @sql += N'DROP TABLE {{schema}}.' + QUOTENAME(name) + N';' + CHAR(10) FROM sys.tables
WHERE SCHEMA_NAME(schema_id) = N'{{schema}}';
SELECT @sql += N'DROP SEQUENCE {{schema}}.' + QUOTENAME(name) + N';' + CHAR(10)
FROM sys.sequences
WHERE SCHEMA_NAME(schema_id) = N'{{schema}}';
-- User-defined TYPEs (table types and scalar UDTs) are not implicitly dropped with DROP SCHEMA;
-- enumerate and drop them before dropping the schema container.
SELECT @sql += N'DROP TYPE {{schema}}.' + QUOTENAME(name) + N';' + CHAR(10)
FROM sys.types
WHERE is_user_defined = 1 AND SCHEMA_NAME(schema_id) = N'{{schema}}';
IF LEN(@sql) > 0 EXEC sp_executesql @sql;

IF SCHEMA_ID(N'{{schema}}') IS NOT NULL EXEC (N'DROP SCHEMA {{schema}}');
