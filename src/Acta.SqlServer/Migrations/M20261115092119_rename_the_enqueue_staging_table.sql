SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO


IF NOT EXISTS (SELECT 1 FROM {{schema}}.migrations WHERE name = 'M20261115092119_rename_the_enqueue_staging_table')
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115092119_rename_the_enqueue_staging_table');
GO
