CREATE INDEX IF NOT EXISTS ix_lanes_namespace ON {{schema}}.lanes (namespace_id, lane_id);

INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261112231452_index_lanes_by_namespace')
ON CONFLICT (name) DO NOTHING;
