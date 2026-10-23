// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { eventFacts } from './eventFacts.ts';

test('eventFacts reads the text facts of a JSON object payload', () => {
  assert.deepEqual(eventFacts('{"reason":"lease-expired","to":"ready"}'), { reason: 'lease-expired', to: 'ready' });
});

test('eventFacts reads no facts from a missing, non-JSON, or non-object payload', () => {
  assert.deepEqual(eventFacts(null), {});
  assert.deepEqual(eventFacts(''), {});
  assert.deepEqual(eventFacts('worker stopped'), {});
  assert.deepEqual(eventFacts('["reason"]'), {});
  assert.deepEqual(eventFacts('"reason"'), {});
  assert.deepEqual(eventFacts('null'), {});
});

test('eventFacts skips a fact whose value is not text', () => {
  assert.deepEqual(eventFacts('{"actor":"marko","count":3,"nested":{"a":"b"}}'), { actor: 'marko' });
});
