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

test('validateSignalName folds case and accepts dotted names, as the server does', () => {
  assert.equal(validateSignalName('Payment-Received'), null);
  assert.equal(validateSignalName('payment.received'), null);
});

test('validateSignalName rejects an empty segment', () => {
  assert.ok(validateSignalName('payment..received'));
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
  assert.deepEqual(validateSignalPayload(''), { ok: true, error: null, json: undefined });
  assert.deepEqual(validateSignalPayload('   '), { ok: true, error: null, json: undefined });
});

test('validateSignalPayload keeps valid JSON as its text, including non-object values and big numbers', () => {
  assert.deepEqual(validateSignalPayload(' {"amount":5} '), { ok: true, error: null, json: '{"amount":5}' });
  assert.deepEqual(validateSignalPayload('9007199254740993'), { ok: true, error: null, json: '9007199254740993' });
});

test('validateSignalPayload rejects malformed JSON', () => {
  const result = validateSignalPayload('{amount:5}');
  assert.equal(result.ok, false);
  assert.ok(result.error);
});
