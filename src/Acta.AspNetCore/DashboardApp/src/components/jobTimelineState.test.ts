import { test } from 'node:test';
import assert from 'node:assert/strict';
import { failedTimelineAttempts, matchesTimelineCategory, timelineAttemptNumbers } from './jobTimelineState.ts';

const events = [
  { executionNumber: 3, eventCode: 'job.execution-finished', executionStatus: 'failed' },
  { executionNumber: 2, eventCode: 'job.signal-raised', executionStatus: 'succeeded' },
  { executionNumber: 1, eventCode: 'schedule.triggered', executionStatus: 'succeeded' },
  { executionNumber: null, eventCode: 'job.paused', executionStatus: null }
];

test('timeline attempts are unique and newest first with lifecycle represented as zero', () => {
  assert.deepEqual(timelineAttemptNumbers(events), [3, 2, 1, 0]);
  assert.deepEqual(failedTimelineAttempts(events), [3]);
});

test('timeline category filters recognize failure, control, signal, and schedule events', () => {
  assert.equal(matchesTimelineCategory(events[0], 'failure'), true);
  assert.equal(matchesTimelineCategory(events[3], 'control'), true);
  assert.equal(matchesTimelineCategory(events[1], 'signal'), true);
  assert.equal(matchesTimelineCategory(events[2], 'schedule'), true);
  assert.equal(matchesTimelineCategory(events[1], 'failure'), false);
});
