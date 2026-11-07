// Run with Node's built-in test runner (see "npm test"). Asserts scheduleControlState matches the
// legacy ScheduleControls.svelte's `$: paused = status === 'paused'` behavior, plus trigger-now.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { scheduleControlState } from './scheduleControlState.ts';

test('active: not paused, trigger available', () => {
  assert.deepEqual(scheduleControlState('active'), { paused: false, canTrigger: true });
});

test('paused: paused true, trigger still available (an operator override)', () => {
  assert.deepEqual(scheduleControlState('paused'), { paused: true, canTrigger: true });
});

test('orphaned: not paused, trigger hidden (no origin declaration to fire)', () => {
  assert.deepEqual(scheduleControlState('orphaned'), { paused: false, canTrigger: false });
});
