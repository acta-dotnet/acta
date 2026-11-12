CREATE INDEX IF NOT EXISTS {{schema}}.ix_lanes_namespace ON lanes (namespace_id, lane_id);

INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261112231452_index_lanes_by_namespace')
ON CONFLICT (name) DO NOTHING;
