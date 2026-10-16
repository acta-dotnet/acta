import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deriveExecutions, executionGapSummary, executionPresentation, withRunningExecution } from './executionsState.ts';
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

test('an executing job\'s attempt in flight is a running row from the job snapshot, with no start', () => {
  const finished = deriveExecutions([
    event({ executionNumber: 1, executionStatus: 'failed' }),
    event({ executionNumber: 2, executionStatus: 'failed' }),
  ]);
  const runs = withRunningExecution(finished, { status: 'executing', executionNumber: 3, leasedByWorkerRef: 'wrk_9' });
  assert.deepEqual(runs.map((run) => run.executionNumber), [3, 2, 1]);
  const running = runs[0];
  assert.equal(running.outcome, 'executing');
  assert.equal(running.workerRef, 'wrk_9');
  assert.equal(running.startedAtUtc, null);
  assert.equal(running.finishedAtUtc, null);
  assert.equal(running.durationMs, null);
  assert.equal(executionPresentation(running.outcome).label, 'Executing');
  assert.deepEqual(executionGapSummary(3, runs), { shown: 3, total: 3, missing: 0 });
});

test('a first attempt in flight is shown, not counted missing', () => {
  const runs = withRunningExecution([], { status: 'executing', executionNumber: 1, leasedByWorkerRef: 'wrk_9' });
  assert.deepEqual(runs.map((run) => run.outcome), ['executing']);
  assert.deepEqual(executionGapSummary(1, runs), { shown: 1, total: 1, missing: 0 });
});

test('the running attempt still counts earlier executions the ledger no longer shows', () => {
  const runs = withRunningExecution([], { status: 'executing', executionNumber: 4, leasedByWorkerRef: 'wrk_9' });
  assert.deepEqual(executionGapSummary(4, runs), { shown: 1, total: 4, missing: 3 });
});

test('a finished event for the snapshot\'s attempt means the snapshot trails the feed: no running row', () => {
  const finished = deriveExecutions([event({ executionNumber: 2, executionStatus: 'succeeded' })]);
  const runs = withRunningExecution(finished, { status: 'executing', executionNumber: 2, leasedByWorkerRef: 'wrk_9' });
  assert.deepEqual(runs.map((run) => run.outcome), ['succeeded']);
});

test('a job that is not executing gets no running row', () => {
  const finished = deriveExecutions([event({ executionNumber: 1, executionStatus: 'failed' })]);
  const runs = withRunningExecution(finished, { status: 'ready', executionNumber: 1, leasedByWorkerRef: null });
  assert.equal(runs, finished);
});

test('a finished event never reads as executing', () => {
  const runs = deriveExecutions([event({ executionStatus: 'executing' })]);
  assert.equal(runs[0].outcome, 'unknown');
});
