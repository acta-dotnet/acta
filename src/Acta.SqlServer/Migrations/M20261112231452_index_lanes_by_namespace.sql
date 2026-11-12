SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF INDEXPROPERTY(OBJECT_ID(N'{{schema}}.lanes'), N'ix_lanes_namespace', 'IndexID') IS NULL
    CREATE INDEX ix_lanes_namespace ON {{schema}}.lanes (namespace_id, lane_id);

IF NOT EXISTS (SELECT 1 FROM {{schema}}.migrations WHERE name = 'M20261112231452_index_lanes_by_namespace')
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261112231452_index_lanes_by_namespace');
GO
