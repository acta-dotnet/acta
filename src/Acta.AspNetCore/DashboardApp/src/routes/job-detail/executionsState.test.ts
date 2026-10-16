import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deriveExecutions, executionGapSummary, executionPresentation } from './executionsState.ts';
import type { JobEvent } from './types.ts';

let seq = 0;
function event(overrides: Partial<JobEvent>): JobEvent {
  seq += 1;
  return {
    jobEventId: seq,
    eventCode: 'job.execution-finished',
    createdAtUtc: '2026-08-09T10:00:0' + (seq % 10) + 'Z',
    jobNamespace: 'billing',
    jobRef: 'job_x',
    workerRef: null,
    executionNumber: 1,
    fromStatus: null,
    toStatus: null,
    executionStatus: null,
    durationMs: null,
    reasonCode: null,
    reasonMessage: null,
    ...overrides,
  };
}

test('a finished event derives its execution: finish, duration, worker, outcome, and reason', () => {
  const runs = deriveExecutions([
    event({ executionStatus: 'failed', createdAtUtc: '2026-08-09T10:00:05.000Z', durationMs: 125, workerRef: 'wrk_7', reasonCode: 'handler-error', reasonMessage: 'boom' }),
  ]);
  assert.equal(runs.length, 1);
  const run = runs[0];
  assert.equal(run.outcome, 'failed');
  assert.equal(run.finishedAtUtc, '2026-08-09T10:00:05.000Z');
  assert.equal(run.durationMs, 125);
  assert.equal(run.workerRef, 'wrk_7');
  assert.equal(run.reasonCode, 'handler-error');
  assert.equal(run.reasonMessage, 'boom');
});

test('an execution starts at its finish less its duration', () => {
  const runs = deriveExecutions([
    event({ executionStatus: 'succeeded', createdAtUtc: '2026-08-09T10:00:05.000Z', durationMs: 1250 }),
  ]);
  assert.equal(runs[0].startedAtUtc, '2026-08-09T10:00:03.750Z');
});

test('an orphan reclaim records no duration, so its start is unknown and its worker is the lease holder', () => {
  const runs = deriveExecutions([
    event({ executionStatus: 'orphaned', durationMs: null, workerRef: 'wrk_4' }),
  ]);
  assert.equal(runs[0].outcome, 'orphaned');
  assert.equal(runs[0].startedAtUtc, null);
  assert.equal(runs[0].workerRef, 'wrk_4');
});

test('only finished events make rows: lifecycle events and an in-flight execution\'s note do not', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.paused', executionNumber: null }),
    event({ executionNumber: 2, executionStatus: 'failed' }),
    event({ eventCode: 'job.note-recorded', executionNumber: 3 }),
  ]);
  assert.deepEqual(runs.map((run) => run.executionNumber), [2]);
});

test('executions sort newest-first', () => {
  const runs = deriveExecutions([
    event({ executionNumber: 1 }),
    event({ executionNumber: 3 }),
    event({ executionNumber: 2 }),
  ]);
  assert.deepEqual(runs.map((run) => run.executionNumber), [3, 2, 1]);
});

test('an unrecognized execution status maps to unknown', () => {
  const runs = deriveExecutions([
    event({ executionStatus: 'mystery' }),
  ]);
  assert.equal(runs[0].outcome, 'unknown');
  assert.equal(executionPresentation(runs[0].outcome).label, 'Unknown');
});

test('the gap summary counts executions the ledger no longer shows', () => {
  const runs = deriveExecutions([
    event({ executionNumber: 9, executionStatus: 'failed' }),
    event({ executionNumber: 10, executionStatus: 'succeeded' }),
  ]);
  assert.deepEqual(executionGapSummary(10, runs), { shown: 2, total: 10, missing: 8 });
  // Zero-events job: nothing derived, everything missing.
  assert.deepEqual(executionGapSummary(5, []), { shown: 0, total: 5, missing: 5 });
});

test('the gap total trusts the ledger when the event feed runs ahead of the snapshot', () => {
  const runs = deriveExecutions([
    event({ executionNumber: 11, executionStatus: 'succeeded' }),
  ]);
  assert.deepEqual(executionGapSummary(10, runs), { shown: 1, total: 11, missing: 10 });
});
