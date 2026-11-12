import { test } from 'node:test';
import assert from 'node:assert/strict';
import { definitionControlMessage, jobControlMessage, outboxControlMessage, scheduleControlMessage } from './controlMessages.ts';

test('job verbs name the verb on apply and on a status rejection', () => {
  assert.equal(jobControlMessage('pause', 'applied'), 'Pause applied.');
  assert.equal(jobControlMessage('cancel', 'rejected'), "Cancel rejected: the job's current status does not allow it.");
  assert.equal(jobControlMessage('pause', 'not-found'), 'Job not found.');
});

test('a rejected restart names every cause the restart statement has', () => {
  const message = jobControlMessage('restart', 'rejected');
  for (const cause of ['is executing', 'firing is still outstanding', 'ancestor or descendant sits in its lane', 'full, orphaned or retired']) {
    assert.ok(message.includes(cause), cause);
  }
});

test('a rejected purge names what can hold the job, the firing and the alert projection included', () => {
  assert.match(jobControlMessage('purge', 'rejected'), /^Purge rejected: .*parent or child jobs.*outstanding firing.*sys\.alerts has not processed/);
});

test('a rejected reschedule names the retired definition and the suspended namespace beside the status', () => {
  assert.match(jobControlMessage('reschedule', 'rejected'), /status does not allow it, its definition is retired, or its namespace is suspended/);
});

test('a rejected schedule pause names its end, a rejected trigger what holds the schedule', () => {
  assert.match(scheduleControlMessage('pause', 'rejected'), /time to pause until has already passed/);
  assert.match(scheduleControlMessage('trigger', 'rejected'), /namespace is suspended, the schedule is paused or at its capacity, or its job is not idle/);
});

test('schedule, outbox, and definition verbs word their own outcomes', () => {
  assert.equal(scheduleControlMessage('trigger', 'applied'), 'Schedule trigger applied.');
  assert.equal(scheduleControlMessage('pause', 'not-found'), 'Schedule not found.');
  assert.equal(outboxControlMessage('requeue', 'accepted'), 'Outbox requeue accepted; the next relay pass applies it.');
  assert.match(outboxControlMessage('discard', 'rejected'), /a pending discard command is already parked/);
  assert.equal(definitionControlMessage('retire', 'applied'), 'Definition retired.');
  assert.match(definitionControlMessage('overrides', 'version-conflict'), /^Definition override rejected/);
  assert.match(definitionControlMessage('retire', 'version-conflict'), /^Retire rejected/);
});
