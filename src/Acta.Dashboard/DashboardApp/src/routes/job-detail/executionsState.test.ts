import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  deriveExecutions,
  executionGapSummary,
  executionPresentation,
  placeSteps,
  withRunningExecution
} from './executionsState.ts';
import type { EventItem, JobStepItem } from '../../api.ts';

let seq = 0;
function event(overrides: Partial<EventItem>): EventItem {
  seq += 1;
  return {
    eventId: seq,
    eventCode: 'job.execution-succeeded',
    createdAtUtc: '2026-08-09T10:00:0' + (seq % 10) + 'Z',
    jobNamespace: 'billing',
    entity: 'job',
    entityId: 7,
    jobName: 'send-invoice',
    jobRef: 'job_x',
    lineageRootJobRef: null,
    tenantName: null,
    workerRef: null,
    executionNumber: 1,
    durationMs: null,
    message: null,
    actor: null,
    detailsText: null,
    ...overrides,
  };
}

test('a failed attempt derives its execution: finish, duration, worker, outcome, reason, and message', () => {
  const runs = deriveExecutions([
    event({
      eventCode: 'job.execution-failed',
      createdAtUtc: '2026-08-09T10:00:05.000Z',
      durationMs: 125,
      workerRef: 'wrk_7',
      message: 'boom',
      detailsText: '{"reason":"unhandled-exception","to":"ready"}',
    }),
  ]);
  assert.equal(runs.length, 1);
  const run = runs[0];
  assert.equal(run.outcome, 'failed');
  assert.equal(run.finishedAtUtc, '2026-08-09T10:00:05.000Z');
  assert.equal(run.durationMs, 125);
  assert.equal(run.workerRef, 'wrk_7');
  assert.equal(run.reason, 'unhandled-exception');
  assert.equal(run.message, 'boom');
});

test('the job\'s final failure is a failed execution with its reason', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.failed', message: 'gave up', detailsText: '{"reason":"non-retryable-exception"}' }),
  ]);
  assert.equal(runs[0].outcome, 'failed');
  assert.equal(runs[0].reason, 'non-retryable-exception');
  assert.equal(runs[0].message, 'gave up');
});

test('an execution starts at its finish less its duration', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-succeeded', createdAtUtc: '2026-08-09T10:00:05.000Z', durationMs: 1250 }),
  ]);
  assert.equal(runs[0].outcome, 'succeeded');
  assert.equal(runs[0].startedAtUtc, '2026-08-09T10:00:03.750Z');
});

test('an orphan reclaim records no duration, so its start is unknown and its worker is the lease holder', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.execution-orphaned', durationMs: null, workerRef: 'wrk_4', detailsText: '{"reason":"lease-expired","to":"ready"}' }),
  ]);
  assert.equal(runs[0].outcome, 'orphaned');
  assert.equal(runs[0].startedAtUtc, null);
  assert.equal(runs[0].workerRef, 'wrk_4');
  assert.equal(runs[0].reason, 'lease-expired');
});

test('a yielded attempt takes its outcome from the details', () => {
  const outcomes = ['rescheduled', 'suspended', 'paused', 'cancelled'].map(
    (outcome) => deriveExecutions([event({ eventCode: 'job.execution-yielded', detailsText: JSON.stringify({ outcome }) })])[0].outcome
  );
  assert.deepEqual(outcomes, ['rescheduled', 'suspended', 'paused', 'cancelled']);
});

test('an execution that slept reads Sleeping, and one that waits Waiting', () => {
  const runs = deriveExecutions([
    event({ executionNumber: 2, eventCode: 'job.execution-yielded', detailsText: '{"outcome":"slept"}' }),
    event({ executionNumber: 1, eventCode: 'job.execution-yielded', detailsText: '{"outcome":"suspended"}' }),
  ]);
  assert.deepEqual(runs.map((run) => executionPresentation(run.outcome).label), ['Sleeping', 'Waiting']);
});

test('a yield whose outcome is missing, unrecognized, or unreadable maps to unknown', () => {
  for (const detailsText of [null, 'not json', '{"outcome":"mystery"}', '{"outcome":"executing"}', '{"outcome":7}']) {
    const runs = deriveExecutions([event({ eventCode: 'job.execution-yielded', detailsText })]);
    assert.equal(runs[0].outcome, 'unknown', String(detailsText));
  }
  assert.equal(executionPresentation('unknown').label, 'Unknown');
});

test('only closing events make rows: lifecycle events and an in-flight execution\'s note do not', () => {
  const runs = deriveExecutions([
    event({ eventCode: 'job.paused', executionNumber: null }),
    event({ eventCode: 'job.execution-failed', executionNumber: 2 }),
    event({ eventCode: 'job.note-recorded', executionNumber: 3 }),
  ]);
  assert.deepEqual(runs.map((run) => run.executionNumber), [2]);
});

test('the latest closing event of an execution wins by event id', () => {
  const runs = deriveExecutions([
    event({ eventId: 20, eventCode: 'job.execution-succeeded', executionNumber: 4 }),
    event({ eventId: 10, eventCode: 'job.execution-orphaned', executionNumber: 4 }),
  ]);
  assert.deepEqual(runs.map((run) => run.outcome), ['succeeded']);
});

test('executions sort newest-first', () => {
  const runs = deriveExecutions([
    event({ executionNumber: 1 }),
    event({ executionNumber: 3 }),
    event({ executionNumber: 2 }),
  ]);
  assert.deepEqual(runs.map((run) => run.executionNumber), [3, 2, 1]);
});

test('the gap summary counts executions the ledger no longer shows', () => {
  const runs = deriveExecutions([
    event({ executionNumber: 9, eventCode: 'job.execution-failed' }),
    event({ executionNumber: 10, eventCode: 'job.execution-succeeded' }),
  ]);
  assert.deepEqual(executionGapSummary(10, runs), { shown: 2, total: 10, missing: 8 });
  // Zero-events job: nothing derived, everything missing.
  assert.deepEqual(executionGapSummary(5, []), { shown: 0, total: 5, missing: 5 });
});

test('the gap total trusts the ledger when the event feed runs ahead of the snapshot', () => {
  const runs = deriveExecutions([
    event({ executionNumber: 11, eventCode: 'job.execution-succeeded' }),
  ]);
  assert.deepEqual(executionGapSummary(10, runs), { shown: 1, total: 11, missing: 10 });
});

test('an executing job\'s attempt in flight is a running row from the job snapshot, with no start', () => {
  const finished = deriveExecutions([
    event({ executionNumber: 1, eventCode: 'job.execution-failed' }),
    event({ executionNumber: 2, eventCode: 'job.execution-failed' }),
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

test('a closing event for the snapshot\'s attempt means the snapshot trails the feed: no running row', () => {
  const finished = deriveExecutions([event({ executionNumber: 2, eventCode: 'job.execution-succeeded' })]);
  const runs = withRunningExecution(finished, { status: 'executing', executionNumber: 2, leasedByWorkerRef: 'wrk_9' });
  assert.deepEqual(runs.map((run) => run.outcome), ['succeeded']);
});

test('a job that is not executing gets no running row', () => {
  const finished = deriveExecutions([event({ executionNumber: 1, eventCode: 'job.execution-failed' })]);
  const runs = withRunningExecution(finished, { status: 'ready', executionNumber: 1, leasedByWorkerRef: null });
  assert.equal(runs, finished);
});

function step(name: string, status: string, createdAtUtc: string, modifiedAtUtc = createdAtUtc): JobStepItem {
  return { name, status, failureMessage: null, createdAtUtc, modifiedAtUtc };
}

// Each execution's line as the timeline reads it.
function lines(executions: ReturnType<typeof deriveExecutions>, steps: JobStepItem[]): Record<number, string> {
  const placed = placeSteps(executions, steps);
  return Object.fromEntries(
    [...placed].map(([number, line]) => [number, line.map((s) => s.name + (s.state === 'succeeded' ? ' ✓' : ' started')).join('  ')])
  );
}

// Execution 1 failed in its last step at 08:00:00 after 85 ms; execution 2 ran that step alone and succeeded.
const twoExecutions = () =>
  deriveExecutions([
    event({ executionNumber: 1, eventCode: 'job.execution-failed', createdAtUtc: '2026-08-09T08:00:00.000Z', durationMs: 85 }),
    event({ executionNumber: 2, eventCode: 'job.execution-succeeded', createdAtUtc: '2026-08-09T08:05:00.000Z', durationMs: 120 })
  ]);

test('a step is recorded where it succeeded and started where it began, and a reused one is not listed again', () => {
  assert.deepEqual(
    lines(twoExecutions(), [
      step('send-receipt', 'succeeded', '2026-08-09T07:59:59.975Z', '2026-08-09T08:04:59.990Z'),
      step('issue-invoice', 'succeeded', '2026-08-09T07:59:59.955Z', '2026-08-09T07:59:59.970Z'),
      step('charge-card', 'succeeded', '2026-08-09T07:59:59.935Z', '2026-08-09T07:59:59.950Z'),
      step('book-shipment', 'succeeded', '2026-08-09T07:59:59.920Z', '2026-08-09T07:59:59.930Z')
    ]),
    {
      1: 'book-shipment ✓  charge-card ✓  issue-invoice ✓  send-receipt started',
      2: 'send-receipt ✓'
    }
  );
});

test('a step that has not succeeded is started where it began, whatever its status, never failed', () => {
  assert.deepEqual(
    lines(twoExecutions(), [
      step('charge-card', 'succeeded', '2026-08-09T07:59:59.935Z'),
      step('send-receipt', 'pending', '2026-08-09T07:59:59.975Z'),
      step('post-ledger', 'failed', '2026-08-09T08:04:59.950Z'),
      step('notify-carrier', 'interrupted', '2026-08-09T08:04:59.960Z', '2026-08-09T08:04:59.970Z')
    ]),
    { 1: 'charge-card ✓  send-receipt started', 2: 'post-ledger started  notify-carrier started' }
  );
});

test('an orphaned execution lists its unfinished step as started, and the running one holds what came after', () => {
  const executions = withRunningExecution(
    deriveExecutions([
      event({ executionNumber: 1, eventCode: 'job.execution-orphaned', createdAtUtc: '2026-08-09T08:10:00.000Z', durationMs: null })
    ]),
    { status: 'executing', executionNumber: 2, leasedByWorkerRef: 'wrk_9' }
  );
  assert.deepEqual(
    lines(executions, [
      step('book-shipment', 'succeeded', '2026-08-09T08:00:00.010Z', '2026-08-09T08:00:00.020Z'),
      step('charge-card', 'succeeded', '2026-08-09T08:00:00.030Z', '2026-08-09T08:10:01.000Z'),
      step('send-receipt', 'pending', '2026-08-09T08:10:02.000Z')
    ]),
    { 1: 'book-shipment ✓  charge-card started', 2: 'charge-card ✓  send-receipt started' }
  );
});

test('an execution with an unknown start begins right after the previous shown one, and the first one has no lower bound', () => {
  const executions = deriveExecutions([
    event({ executionNumber: 2, eventCode: 'job.execution-orphaned', createdAtUtc: '2026-08-09T08:00:00.000Z', durationMs: null }),
    event({ executionNumber: 4, eventCode: 'job.execution-orphaned', createdAtUtc: '2026-08-09T09:00:00.000Z', durationMs: null })
  ]);
  assert.deepEqual(
    lines(executions, [
      step('early', 'succeeded', '2026-08-09T07:00:00.000Z'),
      step('late', 'succeeded', '2026-08-09T08:30:00.000Z')
    ]),
    { 2: 'early ✓', 4: 'late ✓' }
  );
});

test('past a gap in the ledger the derived start bounds an execution, and a step before it is left off', () => {
  // Execution 1's events were not recorded or are already purged; execution 2 ran from 08:04:59.880.
  const executions = deriveExecutions([
    event({ executionNumber: 2, eventCode: 'job.execution-succeeded', createdAtUtc: '2026-08-09T08:05:00.000Z', durationMs: 120 })
  ]);
  assert.deepEqual(
    lines(executions, [
      step('book-shipment', 'succeeded', '2026-08-09T07:59:59.920Z'),
      step('charge-card', 'succeeded', '2026-08-09T07:59:59.935Z', '2026-08-09T08:04:59.950Z'),
      step('send-receipt', 'succeeded', '2026-08-09T08:04:59.900Z', '2026-08-09T08:04:59.990Z')
    ]),
    { 2: 'charge-card ✓  send-receipt ✓' }
  );
});

test('execution 1 has no lower bound, so a step it began at once is not lost to the late derived start', () => {
  // Finish less duration is 07:59:59.915; the handler began the step 2 ms before that instant.
  const executions = deriveExecutions([
    event({ executionNumber: 1, eventCode: 'job.execution-succeeded', createdAtUtc: '2026-08-09T08:00:00.000Z', durationMs: 85 })
  ]);
  assert.deepEqual(lines(executions, [step('book-shipment', 'succeeded', '2026-08-09T07:59:59.913Z', '2026-08-09T07:59:59.990Z')]), {
    1: 'book-shipment ✓'
  });
});

test('after a shown previous execution the bound is its end, since the derived start runs late', () => {
  // The completion is written after the handler returns, so a step the handler began at once predates finish less duration.
  assert.deepEqual(
    lines(twoExecutions(), [step('send-receipt', 'succeeded', '2026-08-09T08:04:59.878Z', '2026-08-09T08:04:59.990Z')]),
    { 2: 'send-receipt ✓' }
  );
});

test('an execution holds its own end instant, and a line runs by first start, then name', () => {
  assert.deepEqual(
    lines(twoExecutions(), [
      step('b-step', 'succeeded', '2026-08-09T07:59:59.950Z', '2026-08-09T08:00:00.000Z'),
      step('a-step', 'succeeded', '2026-08-09T07:59:59.950Z'),
      step('first', 'succeeded', '2026-08-09T07:59:59.920Z')
    ]),
    { 1: 'first ✓  a-step ✓  b-step ✓' }
  );
});
