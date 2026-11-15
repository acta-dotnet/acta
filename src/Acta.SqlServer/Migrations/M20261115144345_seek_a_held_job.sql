SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO


IF NOT EXISTS (SELECT 1 FROM {{schema}}.migrations WHERE name = 'M20261115144345_seek_a_held_job')
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115144345_seek_a_held_job');
GO
