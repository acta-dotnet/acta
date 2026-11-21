// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkKey, keyHint, KEY_LIMITS } from './enqueueKeys.ts';

test('an empty field is omitted and a whitespace-only one is an error, not a silent omission', () => {
  assert.deepEqual(checkKey('', 'idempotency'), { value: null, error: null });
  assert.equal(checkKey('   ', 'idempotency').value, null);
  assert.match(checkKey('   ', 'idempotency').error ?? '', /at least one character/);
});

test('outer whitespace is trimmed and case is kept', () => {
  assert.deepEqual(checkKey('  ORDER#42 ', 'correlation'), { value: 'ORDER#42', error: null });
  assert.deepEqual(checkKey('auth0|Abc', 'idempotency'), { value: 'auth0|Abc', error: null });
});

test('the normalized length boundary is 128 for an idempotency key and 64 for a correlation key', () => {
  const idempotency = 'k'.repeat(KEY_LIMITS.idempotency);
  const correlation = 'c'.repeat(KEY_LIMITS.correlation);
  assert.equal(checkKey(` ${idempotency} `, 'idempotency').value, idempotency);
  assert.equal(checkKey(`${correlation}`, 'correlation').value, correlation);
  assert.equal(checkKey(idempotency + 'x', 'idempotency').error, 'Key length 129 exceeds the 128-char limit.');
  assert.equal(checkKey(correlation + 'x', 'correlation').error, 'Key length 65 exceeds the 64-char limit.');
});

test('embedded whitespace and non-ASCII are refused with the reason', () => {
  assert.match(checkKey('order 42', 'correlation').error ?? '', /no whitespace/);
  assert.match(checkKey('naročilo-42', 'idempotency').error ?? '', /printable ASCII/);
  assert.match(checkKey('tab\there', 'idempotency').error ?? '', /no whitespace/);
});

test('the sys. prefix is reserved for idempotency keys only, in any case', () => {
  assert.match(checkKey('SYS.retention', 'idempotency').error ?? '', /reserved/);
  assert.match(checkKey('sys.x', 'idempotency').error ?? '', /reserved/);
  assert.deepEqual(checkKey('sys.trace-1', 'correlation'), { value: 'sys.trace-1', error: null });
  assert.deepEqual(checkKey('system-42', 'idempotency'), { value: 'system-42', error: null });
});

test('a concurrency key keeps its case, may start with sys., and is at most 128 characters', () => {
  assert.deepEqual(checkKey(' Customer:42 ', 'concurrency'), { value: 'Customer:42', error: null });
  assert.deepEqual(checkKey('sys.lock', 'concurrency'), { value: 'sys.lock', error: null });
  assert.equal(checkKey('k'.repeat(129), 'concurrency').error, 'Key length 129 exceeds the 128-char limit.');
});

test('the hint names the limit of each kind', () => {
  assert.match(keyHint('idempotency'), /128/);
  assert.match(keyHint('correlation'), /64/);
});
