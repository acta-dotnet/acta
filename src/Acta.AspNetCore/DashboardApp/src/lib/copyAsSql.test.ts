// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { jobsListSql, eventsListSql, schedulesListSql } from './copyAsSql.ts';

test('jobsListSql with no filters selects the whole view, no WHERE clause', () => {
  assert.equal(jobsListSql({}), 'SELECT * FROM acta.jobs_view ORDER BY created_at_utc DESC LIMIT 100;');
});

test('jobsListSql composes filters with AND and lowercases the display status', () => {
  assert.equal(
    jobsListSql({ namespace: 'billing', status: 'Failed', jobName: 'send-invoice' }),
    "SELECT * FROM acta.jobs_view WHERE namespace = 'billing' AND status = 'failed' AND job_name = 'send-invoice' ORDER BY created_at_utc DESC LIMIT 100;"
  );
});

test('jobsListSql emits tenant_key as a quoted literal', () => {
  assert.equal(
    jobsListSql({ tenantKey: 'acme-corp' }),
    "SELECT * FROM acta.jobs_view WHERE tenant_key = 'acme-corp' ORDER BY created_at_utc DESC LIMIT 100;"
  );
  assert.equal(jobsListSql({ tenantKey: '' }), 'SELECT * FROM acta.jobs_view ORDER BY created_at_utc DESC LIMIT 100;');
});

test('jobsListSql escapes single quotes in interpolated values', () => {
  assert.equal(
    jobsListSql({ correlationKey: "o'brien" }),
    "SELECT * FROM acta.jobs_view WHERE correlation_key = 'o''brien' ORDER BY created_at_utc DESC LIMIT 100;"
  );
});

test('jobsListSql ignores blank and whitespace-only filter values', () => {
  assert.equal(
    jobsListSql({ namespace: '   ', status: '', jobName: '  ops  ' }),
    "SELECT * FROM acta.jobs_view WHERE job_name = 'ops' ORDER BY created_at_utc DESC LIMIT 100;"
  );
});

test('eventsListSql filters code families through the decoded columns and a time range', () => {
  // The "to" bound is strictly exclusive to match every provider's ListJobEvents.sql (`created_at_utc < to`).
  assert.equal(
    eventsListSql({
      namespace: 'billing',
      eventCode: 'job.cancelled',
      actorCode: 'operator',
      reasonCode: 'job.control-manual',
      createdFromUtc: '2026-07-01T00:00:00.000Z',
      createdToUtc: '2026-07-22T00:00:00.000Z'
    }),
    "SELECT * FROM acta.events_view WHERE namespace = 'billing' AND event = 'job.cancelled' AND actor = 'operator' AND reason = 'job.control-manual' AND created_at_utc >= '2026-07-01T00:00:00.000Z' AND created_at_utc < '2026-07-22T00:00:00.000Z' ORDER BY created_at_utc DESC LIMIT 100;"
  );
});

test('jobsListSql emits the SQL Server TOP form with no LIMIT', () => {
  assert.equal(
    jobsListSql({ status: 'Failed' }, { provider: 'mssql', schema: 'acta' }),
    "SELECT TOP 100 * FROM acta.jobs_view WHERE status = 'failed' ORDER BY created_at_utc DESC;"
  );
});

test('jobsListSql emits the LIMIT form for pg', () => {
  assert.equal(
    jobsListSql({}, { provider: 'pg', schema: 'acta' }),
    'SELECT * FROM acta.jobs_view ORDER BY created_at_utc DESC LIMIT 100;'
  );
});

test('jobsListSql qualifies the view with the configured schema (sqlite: the attached database)', () => {
  assert.equal(
    jobsListSql({}, { provider: 'sqlite', schema: 'main' }),
    'SELECT * FROM main.jobs_view ORDER BY created_at_utc DESC LIMIT 100;'
  );
  assert.equal(
    jobsListSql({}, { provider: 'pg', schema: 'ops' }),
    'SELECT * FROM ops.jobs_view ORDER BY created_at_utc DESC LIMIT 100;'
  );
});

test('eventsListSql emits the SQL Server TOP form and the exclusive "to" bound', () => {
  assert.equal(
    eventsListSql(
      { namespace: 'billing', createdToUtc: '2026-07-22T00:00:00.000Z' },
      { provider: 'mssql', schema: 'acta' }
    ),
    "SELECT TOP 100 * FROM acta.events_view WHERE namespace = 'billing' AND created_at_utc < '2026-07-22T00:00:00.000Z' ORDER BY created_at_utc DESC;"
  );
});

test('schedulesListSql emits the SQL Server TOP form with no LIMIT', () => {
  assert.equal(
    schedulesListSql({ liveOnly: true }, { provider: 'mssql', schema: 'acta' }),
    'SELECT TOP 100 * FROM acta.schedules_view WHERE orphaned_at_utc IS NULL ORDER BY next_run_at_utc;'
  );
});

// The ref columns store canonical uuid text, so the copied SQL compares against the decoded uuid -
// never the wrk_ string an operator sees.
test('eventsListSql emits the worker ref as its decoded uuid', () => {
  assert.equal(
    eventsListSql({ workerRef: 'wrk_01kydka200fay8000000000002' }),
    "SELECT * FROM acta.events_view WHERE worker_ref = '019f9b35-0800-7abc-8000-000000000002' "
      + 'ORDER BY created_at_utc DESC LIMIT 100;'
  );
});

test('eventsListSql drops a malformed worker ref rather than emitting an unmatchable predicate', () => {
  assert.equal(eventsListSql({ workerRef: 'not-a-ref' }), 'SELECT * FROM acta.events_view ORDER BY created_at_utc DESC LIMIT 100;');
});

test('eventsListSql with no filters selects the whole view', () => {
  assert.equal(eventsListSql({}), 'SELECT * FROM acta.events_view ORDER BY created_at_utc DESC LIMIT 100;');
});

test('schedulesListSql maps live-only to a not-orphaned predicate (no interpolated value)', () => {
  assert.equal(
    schedulesListSql({ namespace: 'billing', jobName: 'nightly-close', liveOnly: true }),
    "SELECT * FROM acta.schedules_view WHERE namespace = 'billing' AND job_name = 'nightly-close' AND orphaned_at_utc IS NULL ORDER BY next_run_at_utc LIMIT 100;"
  );
});

test('schedulesListSql omits the live-only predicate when live-only is off', () => {
  assert.equal(
    schedulesListSql({ liveOnly: false }),
    'SELECT * FROM acta.schedules_view ORDER BY next_run_at_utc LIMIT 100;'
  );
});
