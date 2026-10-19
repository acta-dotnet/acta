-- SQLite has no schema to drop, and the database may hold the application's own tables, so Acta's go by name.
-- Foreign keys are off for the drops, which leaves their order free; indexes go with their tables.
PRAGMA foreign_keys = OFF;
DROP VIEW IF EXISTS {{schema}}.alerts_view;
DROP VIEW IF EXISTS {{schema}}.checkpoints_view;
DROP VIEW IF EXISTS {{schema}}.definitions_view;
DROP VIEW IF EXISTS {{schema}}.events_view;
DROP VIEW IF EXISTS {{schema}}.jobs_view;
DROP VIEW IF EXISTS {{schema}}.schedules_view;
DROP VIEW IF EXISTS {{schema}}.steps_view;
DROP VIEW IF EXISTS {{schema}}.tags_view;
DROP VIEW IF EXISTS {{schema}}.workers_view;
DROP TABLE IF EXISTS {{schema}}.alerts;
DROP TABLE IF EXISTS {{schema}}.checkpoints;
DROP TABLE IF EXISTS {{schema}}.definitions;
DROP TABLE IF EXISTS {{schema}}.events;
DROP TABLE IF EXISTS {{schema}}.jobs;
DROP TABLE IF EXISTS {{schema}}.lanes;
DROP TABLE IF EXISTS {{schema}}.locks;
DROP TABLE IF EXISTS {{schema}}.migrations;
DROP TABLE IF EXISTS {{schema}}.namespaces;
DROP TABLE IF EXISTS {{schema}}.runtimes;
DROP TABLE IF EXISTS {{schema}}.schedules;
DROP TABLE IF EXISTS {{schema}}.steps;
DROP TABLE IF EXISTS {{schema}}.tags;
DROP TABLE IF EXISTS {{schema}}.tenants;
DROP TABLE IF EXISTS {{schema}}.workers;
PRAGMA foreign_keys = ON;
