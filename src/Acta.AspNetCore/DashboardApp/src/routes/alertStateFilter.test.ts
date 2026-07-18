// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { alertStateBucket, alertStateMatches, alertStateQuery } from './alertStateFilter.ts';

test('alertStateBucket: neither timestamp set is unacknowledged', () => {
  assert.equal(alertStateBucket({ acknowledgedAtUtc: null, resolvedAtUtc: null }), 'unacknowledged');
});

test('alertStateBucket: acknowledged but not resolved is acknowledged', () => {
  assert.equal(alertStateBucket({ acknowledgedAtUtc: '2026-07-11T00:00:00Z', resolvedAtUtc: null }), 'acknowledged');
});

test('alertStateBucket: resolved wins regardless of acknowledged state (boundary: both set)', () => {
  assert.equal(
    alertStateBucket({ acknowledgedAtUtc: '2026-07-11T00:00:00Z', resolvedAtUtc: '2026-07-11T01:00:00Z' }),
    'resolved'
  );
});

test('alertStateBucket: resolved but never acknowledged is still resolved', () => {
  assert.equal(alertStateBucket({ acknowledgedAtUtc: null, resolvedAtUtc: '2026-07-11T01:00:00Z' }), 'resolved');
});

test('alertStateMatches: a row matches only its own bucket', () => {
  const open = { acknowledgedAtUtc: null, resolvedAtUtc: null };
  assert.equal(alertStateMatches('unacknowledged', open), true);
  assert.equal(alertStateMatches('acknowledged', open), false);
  assert.equal(alertStateMatches('resolved', open), false);
});

test('alertStateQuery: unacknowledged/acknowledged filter server-side via unresolvedOnly+acknowledged', () => {
  assert.deepEqual(alertStateQuery('unacknowledged'), { unresolvedOnly: true, acknowledged: false });
  assert.deepEqual(alertStateQuery('acknowledged'), { unresolvedOnly: true, acknowledged: true });
});

test('alertStateQuery: resolved has no server-side "resolved only" filter, so both params are unset', () => {
  assert.deepEqual(alertStateQuery('resolved'), { unresolvedOnly: '', acknowledged: '' });
});
