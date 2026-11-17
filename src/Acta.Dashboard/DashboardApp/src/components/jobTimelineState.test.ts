import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  failedTimelineAttempts,
  matchesTimelineCategory,
  timelineActor,
  timelineAttemptNumbers,
  timelinePresentation
} from './jobTimelineState.ts';

const events = [
  { executionNumber: 4, eventCode: 'job.execution-orphaned', detailsText: '{"reason":"lease-expired","to":"ready"}' },
  { executionNumber: 3, eventCode: 'job.execution-failed', detailsText: '{"reason":"unhandled-exception","to":"ready"}' },
  { executionNumber: 2, eventCode: 'job.signal-raised', detailsText: null },
  { executionNumber: 1, eventCode: 'schedule.triggered', detailsText: '{"schedule":"nightly"}' },
  { executionNumber: null, eventCode: 'job.paused', detailsText: null }
];

test('timeline attempts are unique and newest first with lifecycle represented as zero', () => {
  assert.deepEqual(timelineAttemptNumbers(events), [4, 3, 2, 1, 0]);
  assert.deepEqual(failedTimelineAttempts(events), [4, 3]);
});

test('timeline category filters recognize failure, control, signal, and schedule events', () => {
  assert.equal(matchesTimelineCategory(events[0], 'failure'), true);
  assert.equal(matchesTimelineCategory(events[1], 'failure'), true);
  assert.equal(matchesTimelineCategory({ eventCode: 'job.failed' }, 'failure'), true);
  assert.equal(matchesTimelineCategory({ eventCode: 'job.cancelled' }, 'failure'), true);
  assert.equal(matchesTimelineCategory(events[4], 'control'), true);
  assert.equal(matchesTimelineCategory(events[2], 'signal'), true);
  assert.equal(matchesTimelineCategory(events[3], 'schedule'), true);
  assert.equal(matchesTimelineCategory(events[2], 'failure'), false);
  assert.equal(matchesTimelineCategory({ eventCode: 'job.execution-succeeded' }, 'failure'), false);
});

test('each attempt-closing code has its own rail title', () => {
  const titles = ['job.execution-succeeded', 'job.execution-failed', 'job.execution-orphaned', 'job.failed'].map(
    (eventCode) => timelinePresentation({ eventCode }).title
  );
  assert.deepEqual(titles, ['Completed', 'Execution failed', 'Orphaned', 'Job failed']);
});

test('a yielded attempt is presented by the outcome in its details, and an unreadable one stays neutral', () => {
  const yielded = (detailsText: string | null) => timelinePresentation({ eventCode: 'job.execution-yielded', detailsText });
  assert.deepEqual(yielded('{"outcome":"rescheduled","reason":"handler-rescheduled"}'), {
    icon: 'counter-clockwise-clock',
    tone: 'warn',
    title: 'Attempt rescheduled'
  });
  assert.equal(yielded('{"outcome":"suspended"}').title, 'Waiting');
  const suspended = (detailsText: string) => timelinePresentation({ eventCode: 'job.suspended', detailsText }).title;
  assert.equal(suspended('{"to":"ready"}'), 'Sleeping');
  assert.equal(suspended('{"to":"suspended"}'), 'Waiting');
  // A suspended yield reads as the job.suspended event its own execution wrote.
  const yieldedIn = (to: string) =>
    timelinePresentation({ eventCode: 'job.execution-yielded', executionNumber: 2, detailsText: '{"outcome":"suspended"}' }, [
      { eventCode: 'job.suspended', executionNumber: 1, detailsText: '{"to":"ready"}' },
      { eventCode: 'job.suspended', executionNumber: 2, detailsText: JSON.stringify({ to }) }
    ]).title;
  assert.equal(yieldedIn('ready'), 'Sleeping');
  assert.equal(yieldedIn('suspended'), 'Waiting');
  assert.equal(yielded('{"outcome":"paused"}').title, 'Paused');
  assert.equal(yielded('{"outcome":"cancelled"}').tone, 'bad');
  assert.deepEqual(yielded('not json'), { icon: 'minus-circle', tone: 'neutral', title: 'Attempt yielded' });
  assert.equal(yielded(null).title, 'Attempt yielded');
});

test('an action the job took on itself reads as the job, and any other actor by name', () => {
  const resetBy = (actor: string | null) => timelineActor({ eventCode: 'job.state-reset', entity: 'job', entityId: 3, actor });
  assert.equal(resetBy('3'), 'the job');
  assert.equal(resetBy('4'), '4');
  assert.equal(resetBy('marko'), 'marko');
  assert.equal(resetBy(null), null);
  // Only a job's own id is the job: the same number on another kind of subject names something else.
  assert.equal(timelineActor({ eventCode: 'namespace.suspended', entity: 'namespace', entityId: 3, actor: '3' }), '3');
});
