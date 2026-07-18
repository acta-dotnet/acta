// Run with Node's built-in test runner (see "npm test"). Asserts jobControlState's can-* fields
// match exactly what JobControls.svelte's legacy `$:` reactive declarations computed before the
// runes conversion, plus the three new reschedule/reprioritize/purge derivations.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { jobControlState } from './jobControlState.ts';
import { TERMINAL_STATUSES } from '../format.ts';

test('ready: pause/restart/cancel/reschedule/reprioritize available, resume/purge are not', () => {
  assert.deepEqual(jobControlState('ready'), {
    terminal: false,
    canPause: true,
    canResume: false,
    canRestart: true,
    canCancel: true,
    canReschedule: true,
    canReprioritize: true,
    canPurge: false
  });
});

test('paused: resume/restart/cancel/reschedule/reprioritize available, pause/purge are not', () => {
  assert.deepEqual(jobControlState('paused'), {
    terminal: false,
    canPause: false,
    canResume: true,
    canRestart: true,
    canCancel: true,
    canReschedule: true,
    canReprioritize: true,
    canPurge: false
  });
});

test('executing: only cancel/reprioritize available (in-flight rejects pause/restart/reschedule; not terminal so no purge)', () => {
  assert.deepEqual(jobControlState('executing'), {
    terminal: false,
    canPause: true, // hidden only when already paused or terminal; server rejects the actual pause
    canResume: false,
    canRestart: false,
    canCancel: true,
    canReschedule: false,
    canReprioritize: true,
    canPurge: false
  });
});

test('terminal statuses: only restart and purge are offered', () => {
  for (const status of TERMINAL_STATUSES) {
    assert.deepEqual(jobControlState(status), {
      terminal: true,
      canPause: false,
      canResume: false,
      canRestart: true,
      canCancel: false,
      canReschedule: false,
      canReprioritize: false,
      canPurge: true
    });
  }
});
