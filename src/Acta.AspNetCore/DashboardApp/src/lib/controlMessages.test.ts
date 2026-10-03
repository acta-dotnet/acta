import { test } from 'node:test';
import assert from 'node:assert/strict';
import { definitionControlMessage, jobControlMessage, outboxControlMessage, scheduleControlMessage } from './controlMessages.ts';

test('job verbs name the verb on apply and on a status rejection', () => {
  assert.equal(jobControlMessage('pause', 'applied', 'paused'), 'Pause applied.');
  assert.equal(jobControlMessage('cancel', 'rejected', 'succeeded'), "Cancel rejected: the job's current status does not allow it.");
  assert.equal(jobControlMessage('pause', 'notFound', null), 'Job not found.');
});

test('a rejected restart names the status or the lane as the reason', () => {
  assert.equal(jobControlMessage('restart', 'rejected', 'succeeded'), "Restart rejected: the job's status or its lane does not allow it.");
});

test('an input amend rejected without a status has no input to amend', () => {
  assert.equal(jobControlMessage('input', 'rejected', null), 'Input rejected: the job has no input to amend.');
  assert.equal(jobControlMessage('input', 'rejected', 'executing'), "Input rejected: the job's current status does not allow it.");
});

test('a version conflict tells the operator to reload', () => {
  assert.match(jobControlMessage('reprioritize', 'versionConflict', 'ready'), /version conflict\)\. Reload and retry\.$/);
});

test('schedule, outbox, and definition verbs word their own outcomes', () => {
  assert.equal(scheduleControlMessage('trigger', 'applied'), 'Schedule trigger applied.');
  assert.equal(scheduleControlMessage('pause', 'notFound'), 'Schedule not found.');
  assert.equal(outboxControlMessage('requeue', 'accepted'), 'Outbox requeue accepted; the next relay pass applies it.');
  assert.match(outboxControlMessage('discard', 'rejected'), /a pending discard command is already parked/);
  assert.equal(definitionControlMessage('retire', 'applied'), 'Definition retired.');
  assert.match(definitionControlMessage('overrides', 'rejected'), /^Definition override rejected/);
});
