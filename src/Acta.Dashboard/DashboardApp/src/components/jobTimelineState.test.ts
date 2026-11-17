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
  assert.equal(yielded('{"outcome":"slept"}').title, 'Sleeping');
  // A sleep and a wait are two events, each with its own title.
  assert.equal(timelinePresentation({ eventCode: 'job.slept' }).title, 'Sleeping');
  assert.equal(timelinePresentation({ eventCode: 'job.suspended' }).title, 'Waiting');
  assert.equal(yielded('{"outcome":"paused"}').title, 'Paused');
  assert.equal(yielded('{"outcome":"cancelled"}').tone, 'bad');
  assert.deepEqual(yielded('not json'), { icon: 'minus-circle', tone: 'neutral', title: 'Attempt yielded' });
  assert.equal(yielded(null).title, 'Attempt yielded');
});

test('an action the job took on itself reads as the job, and any other actor by name', () => {
  const resetBy = (actor: string | null) =>
    timelineActor({ eventCode: 'job.state-reset', entity: 'job', entityId: 3, jobRef: 'job_aa', actor });
  assert.equal(resetBy('job_aa'), 'the job');
  assert.equal(resetBy('job_bb'), 'job_bb');
  assert.equal(resetBy('marko'), 'marko');
  assert.equal(resetBy(null), null);
  // Only an event about the job reads so: an alert a job resolved names the job by its ref.
  assert.equal(timelineActor({ eventCode: 'alert.resolved', entity: 'alert', entityId: 3, jobRef: null, actor: 'job_aa' }), 'job_aa');
});
