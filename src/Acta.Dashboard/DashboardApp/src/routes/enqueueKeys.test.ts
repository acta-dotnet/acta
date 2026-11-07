// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkKey, keyHint, KEY_LIMITS } from './enqueueKeys.ts';

test('an empty field is omitted and a whitespace-only one is an error, not a silent omission', () => {
  assert.deepEqual(checkKey('', 'deduplication'), { value: null, error: null });
  assert.equal(checkKey('   ', 'deduplication').value, null);
  assert.match(checkKey('   ', 'deduplication').error ?? '', /at least one character/);
});

test('outer whitespace is trimmed and case is kept', () => {
  assert.deepEqual(checkKey('  ORDER#42 ', 'correlation'), { value: 'ORDER#42', error: null });
  assert.deepEqual(checkKey('auth0|Abc', 'deduplication'), { value: 'auth0|Abc', error: null });
});

test('the normalized length boundary is 128 for a deduplication key and 64 for a correlation key', () => {
  const dedup = 'k'.repeat(KEY_LIMITS.deduplication);
  const correlation = 'c'.repeat(KEY_LIMITS.correlation);
  assert.equal(checkKey(` ${dedup} `, 'deduplication').value, dedup);
  assert.equal(checkKey(`${correlation}`, 'correlation').value, correlation);
  assert.equal(checkKey(dedup + 'x', 'deduplication').error, 'Key length 129 exceeds the 128-char limit.');
  assert.equal(checkKey(correlation + 'x', 'correlation').error, 'Key length 65 exceeds the 64-char limit.');
});

test('embedded whitespace and non-ASCII are refused with the reason', () => {
  assert.match(checkKey('order 42', 'correlation').error ?? '', /no whitespace/);
  assert.match(checkKey('naročilo-42', 'deduplication').error ?? '', /printable ASCII/);
  assert.match(checkKey('tab\there', 'deduplication').error ?? '', /no whitespace/);
});

test('the sys. prefix is reserved for deduplication keys only, in any case', () => {
  assert.match(checkKey('SYS.retention', 'deduplication').error ?? '', /reserved/);
  assert.match(checkKey('sys.x', 'deduplication').error ?? '', /reserved/);
  assert.deepEqual(checkKey('sys.trace-1', 'correlation'), { value: 'sys.trace-1', error: null });
  assert.deepEqual(checkKey('system-42', 'deduplication'), { value: 'system-42', error: null });
});

test('a concurrency key keeps its case, may start with sys., and is at most 128 characters', () => {
  assert.deepEqual(checkKey(' Customer:42 ', 'concurrency'), { value: 'Customer:42', error: null });
  assert.deepEqual(checkKey('sys.lock', 'concurrency'), { value: 'sys.lock', error: null });
  assert.equal(checkKey('k'.repeat(129), 'concurrency').error, 'Key length 129 exceeds the 128-char limit.');
});

test('the hint names the limit of each kind', () => {
  assert.match(keyHint('deduplication'), /128/);
  assert.match(keyHint('correlation'), /64/);
});
