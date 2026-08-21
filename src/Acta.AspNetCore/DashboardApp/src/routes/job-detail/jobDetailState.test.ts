import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  activeWaitLabel,
  buildIncidentSummary,
  childRollup,
  latestMeaningfulEvent,
  mergeJobEvents,
  payloadFormatLabel
} from './jobDetailState.ts';
import type { JobEvent, JobDetail } from './types.ts';

const event = (jobEventId: number, createdAtUtc: string, reasonMessage: string | null = null): JobEvent => ({
  jobEventId,
  createdAtUtc,
  reasonMessage,
  reasonCode: null,
  eventCode: `event-${jobEventId}`,
  jobNamespace: 'billing',
  jobRef: 'job_1',
  workerRef: null,
  executionNumber: 1,
  fromStatus: null,
  toStatus: null,
  executionStatus: null,
  durationMs: null
});

test('latest meaningful event prefers the newest reason-bearing event', () => {
  const events = [
    event(1, '2026-01-01T00:00:00Z', 'root cause'),
    event(2, '2026-01-02T00:00:00Z'),
    event(3, '2026-01-03T00:00:00Z')
  ];
  assert.equal(latestMeaningfulEvent(events)?.jobEventId, 1);
});

test('event head replaces overlapping history without duplicating immutable pages', () => {
  const merged = mergeJobEvents(
    [event(4, '2026-01-04T00:00:00Z'), event(3, '2026-01-03T00:00:00Z')],
    [
      [event(3, '2026-01-03T00:00:00Z'), event(2, '2026-01-02T00:00:00Z')],
      [event(1, '2026-01-01T00:00:00Z')]
    ]
  );

  assert.deepEqual(merged.map((item) => item.jobEventId), [4, 3, 2, 1]);
});

test('child rollup is deterministic by count then status', () => {
  const child = (status: string) => ({ jobRef: status, jobName: status, status, createdAtUtc: '', modifiedAtUtc: '' });
  assert.deepEqual(childRollup([child('ready'), child('failed'), child('ready'), child('cancelled')]), [
    ['ready', 2],
    ['cancelled', 1],
    ['failed', 1]
  ]);
});

test('a child latch wait reads as a child job, not as a timer', () => {
  // The wire value is the kebab code the JobCheckpointKindCode converter writes, and the slot name is
  // the framework key the parent's latch is stored under.
  assert.deepEqual(activeWaitLabel('child-latch', 'sys.child.42'), { kind: 'child job', name: '42' });
  assert.deepEqual(activeWaitLabel('signal', 'approval'), { kind: 'signal', name: 'approval' });
  assert.deepEqual(activeWaitLabel('timer', 'nightly'), { kind: 'timer', name: 'nightly' });
});

test('an unfamiliar wait kind names itself instead of borrowing another kind name', () => {
  // A child latch used to land here and be announced as a timer, because the panels asked only
  // whether the kind was a signal.
  assert.deepEqual(activeWaitLabel('Progress', 'sys.progress'), { kind: 'progress', name: 'sys.progress' });
  // A child latch whose name is not the framework key keeps the name verbatim, as the explainer does.
  assert.deepEqual(activeWaitLabel('child-latch', 'legacy-latch'), { kind: 'child latch', name: 'legacy-latch' });
});

test('payload format labels known and unknown ids', () => {
  assert.equal(payloadFormatLabel(1), 'json');
  assert.equal(payloadFormatLabel(9), 'format #9');
});

test('incident summary includes current evidence in newest-first order', () => {
  const snapshot = {
    jobRef: 'job_1',
    jobNamespace: 'billing',
    jobName: 'invoice',
    status: 'failed',
    createdAtUtc: '2026-01-01T00:00:00Z',
    modifiedAtUtc: '2026-01-02T00:00:00Z'
  } as JobDetail;
  const summary = buildIncidentSummary(
    snapshot,
    null,
    [event(1, '2026-01-01T00:00:00Z'), event(2, '2026-01-02T00:00:00Z', 'timeout')],
    'http://localhost/#/jobs/job_1'
  );

  assert.match(summary, /Acta incident: job_1/);
  assert.ok(summary.indexOf('event-2') < summary.indexOf('event-1'));
  assert.match(summary, /Dashboard: http:\/\/localhost\/#\/jobs\/job_1/);
});
