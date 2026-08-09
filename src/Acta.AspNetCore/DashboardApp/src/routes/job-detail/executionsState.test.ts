import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deriveExecutions, executionGapSummary, executionPresentation } from './executionsState.ts';
import type { JobEvent } from './types.ts';

let seq = 0;
function event(overrides: Partial<JobEvent>): JobEvent {
  seq += 1;
  return {
    jobEventId: seq,
    eventCode: 'job.execution-started',
    createdAtUtc: '2026-08-09T10:00:0' + (seq % 10) + 'Z',
    jobNamespace: 'billing',
    jobRef: 'job_x',
    workerId: null,
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

test('a paired execution derives start, end, duration, worker, and outcome', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-finished', executionStatus: 'failed', durationMs: 125, workerId: 7, reasonCode: 'handler-error', reasonMessage: 'boom' }),
    event({ eventCode: 'job.execution-started', workerId: 7 }),
  ]);
  assert.equal(runs.length, 1);
  const run = runs[0];
  assert.equal(run.outcome, 'failed');
  assert.equal(run.durationMs, 125);
  assert.equal(run.workerId, 7);
  assert.equal(run.reasonMessage, 'boom');
  assert.equal(run.missingStart, false);
  assert.equal(run.missingEnd, false);
});

test('an orphan reclaim (end-only, null worker) falls back to the start event worker', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-started', workerId: 4 }),
    event({ eventCode: 'job.execution-finished', executionStatus: 'orphaned', durationMs: null, workerId: null }),
  ]);
  assert.equal(runs[0].outcome, 'orphaned');
  assert.equal(runs[0].workerId, 4);
  assert.equal(runs[0].durationMs, null);
});

test('a claim-only crash derives from the end event alone and flags the missing start', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-finished', executionStatus: 'orphaned', workerId: null }),
  ]);
  assert.equal(runs[0].outcome, 'orphaned');
  assert.equal(runs[0].missingStart, true);
  assert.equal(runs[0].workerId, null);
});

test('an in-flight execution (start-only) reads as executing', () => {
  const runs = deriveExecutions([event({ eventCode: 'job.execution-started', workerId: 2 })]);
  assert.equal(runs[0].outcome, 'executing');
  assert.equal(runs[0].missingEnd, true);
});

test('lifecycle events without an execution number are excluded', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.paused', executionNumber: null }),
    event({ eventCode: 'job.execution-started', executionNumber: 3 }),
  ]);
  assert.equal(runs.length, 1);
  assert.equal(runs[0].executionNumber, 3);
});

test('executions sort newest-first', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-started', executionNumber: 1 }),
    event({ eventCode: 'job.execution-started', executionNumber: 3 }),
    event({ eventCode: 'job.execution-started', executionNumber: 2 }),
  ]);
  assert.deepEqual(runs.map((run) => run.executionNumber), [3, 2, 1]);
});

test('an unrecognized execution status maps to unknown', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-finished', executionStatus: 'mystery' }),
  ]);
  assert.equal(runs[0].outcome, 'unknown');
  assert.equal(executionPresentation(runs[0].outcome).label, 'Unknown');
});

test('the gap summary counts executions the ledger no longer shows', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-finished', executionNumber: 9, executionStatus: 'failed' }),
    event({ eventCode: 'job.execution-finished', executionNumber: 10, executionStatus: 'succeeded' }),
  ]);
  assert.deepEqual(executionGapSummary(10, runs), { shown: 2, total: 10, missing: 8 });
  // Zero-events job: nothing derived, everything missing.
  assert.deepEqual(executionGapSummary(5, []), { shown: 0, total: 5, missing: 5 });
});

test('the gap total trusts the ledger when the event feed runs ahead of the snapshot', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-started', executionNumber: 11 }),
  ]);
  assert.deepEqual(executionGapSummary(10, runs), { shown: 1, total: 11, missing: 10 });
});
