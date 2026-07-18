// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateSignalName, validateSignalPayload } from './signalDrawerState.ts';

test('validateSignalName accepts lowercase kebab names', () => {
  assert.equal(validateSignalName('payment-received'), null);
  assert.equal(validateSignalName('a'), null);
  assert.equal(validateSignalName('a1-b2'), null);
});

test('validateSignalName rejects underscores', () => {
  assert.ok(validateSignalName('payment_received'));
});

test('validateSignalName rejects uppercase', () => {
  assert.ok(validateSignalName('Payment-Received'));
});

test('validateSignalName rejects the bare reserved name and the sys. prefix', () => {
  assert.ok(validateSignalName('sys'));
  assert.ok(validateSignalName('sys.retry'));
});

test('validateSignalName rejects empty, trailing hyphen, and names over 128 chars', () => {
  assert.ok(validateSignalName(''));
  assert.ok(validateSignalName('abc-'));
  assert.ok(validateSignalName('a'.repeat(129)));
});

test('validateSignalPayload treats empty or whitespace-only text as presence-only', () => {
  assert.deepEqual(validateSignalPayload(''), { ok: true, error: null, value: undefined });
  assert.deepEqual(validateSignalPayload('   '), { ok: true, error: null, value: undefined });
});

test('validateSignalPayload parses valid JSON, including non-object values', () => {
  assert.deepEqual(validateSignalPayload('{"amount":5}'), { ok: true, error: null, value: { amount: 5 } });
  assert.deepEqual(validateSignalPayload('42'), { ok: true, error: null, value: 42 });
});

test('validateSignalPayload rejects malformed JSON', () => {
  const result = validateSignalPayload('{amount:5}');
  assert.equal(result.ok, false);
  assert.ok(result.error);
});
