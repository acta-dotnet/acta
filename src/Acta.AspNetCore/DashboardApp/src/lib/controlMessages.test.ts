import { test } from 'node:test';
import assert from 'node:assert/strict';
import { definitionControlMessage, jobControlMessage, outboxControlMessage, scheduleControlMessage } from './controlMessages.ts';

test('job verbs name the verb on apply and on a status rejection', () => {
  assert.equal(jobControlMessage('pause', 'applied'), 'Pause applied.');
  assert.equal(jobControlMessage('cancel', 'rejected'), "Cancel rejected: the job's current status does not allow it.");
  assert.equal(jobControlMessage('pause', 'notFound'), 'Job not found.');
});

test('a rejected restart names the status or the lane as the reason', () => {
  assert.equal(jobControlMessage('restart', 'rejected'), "Restart rejected: the job's status or its lane does not allow it.");
});

test('schedule, outbox, and definition verbs word their own outcomes', () => {
  assert.equal(scheduleControlMessage('trigger', 'applied'), 'Schedule trigger applied.');
  assert.equal(scheduleControlMessage('pause', 'notFound'), 'Schedule not found.');
  assert.match(scheduleControlMessage('overrides', 'versionConflict'), /changed since you loaded it \(version conflict\)/);
  assert.equal(outboxControlMessage('requeue', 'accepted'), 'Outbox requeue accepted; the next relay pass applies it.');
  assert.match(outboxControlMessage('discard', 'rejected'), /a pending discard command is already parked/);
  assert.equal(definitionControlMessage('retire', 'applied'), 'Definition retired.');
  assert.match(definitionControlMessage('overrides', 'versionConflict'), /^Definition override rejected/);
  assert.match(definitionControlMessage('retire', 'versionConflict'), /^Retire rejected/);
});
