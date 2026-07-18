// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildOverridesPayload, overridesNeedsReload } from './scheduleOverrides.ts';

test('buildOverridesPayload trims input and keeps a real value', () => {
  assert.deepEqual(buildOverridesPayload({ expression: '  0 */5 * * * *  ', timeZoneId: 'Europe/Ljubljana' }), {
    expression: '0 */5 * * * *',
    timeZoneId: 'Europe/Ljubljana'
  });
});

test('buildOverridesPayload sends null for a blank field, clearing that override', () => {
  assert.deepEqual(buildOverridesPayload({ expression: '', timeZoneId: '   ' }), { expression: null, timeZoneId: null });
});

test('overridesNeedsReload is false only for an applied result', () => {
  assert.equal(overridesNeedsReload('applied'), false);
});

test('overridesNeedsReload is true for rejected (covers a stale expectedVersion) and notFound - never silently resend', () => {
  for (const action of ['rejected', 'notFound']) {
    assert.equal(overridesNeedsReload(action), true);
  }
});
