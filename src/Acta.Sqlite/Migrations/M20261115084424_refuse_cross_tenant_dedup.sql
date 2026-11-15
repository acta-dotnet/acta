
INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261115084424_refuse_cross_tenant_dedup')
ON CONFLICT (name) DO NOTHING;
