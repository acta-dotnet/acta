import { strict as assert } from 'node:assert';
import test from 'node:test';
import { filterZones, ianaTimeZones, suggestedTimeZones } from './timeZones.ts';

test('filtering matches case-insensitively anywhere in the id and keeps list order', () => {
  const zones = ['UTC', 'Europe/Ljubljana', 'Europe/London', 'America/New_York'];
  assert.deepEqual(filterZones(zones, 'lj'), ['Europe/Ljubljana']);
  assert.deepEqual(filterZones(zones, 'EUROPE'), ['Europe/Ljubljana', 'Europe/London']);
  assert.deepEqual(filterZones(zones, 'york'), ['America/New_York']);
  assert.deepEqual(filterZones(zones, '  '), zones);
  assert.deepEqual(filterZones(zones, 'nope'), []);
});

test('iana zones come from the browser tzdb', () => {
  const zones = ianaTimeZones();
  assert.ok(zones.length > 100, 'expected a full tzdb list');
  assert.ok(zones.includes('Europe/Ljubljana'));
});

test('UTC leads the suggestions: the canonical tzdb list omits it and schedules default to it', () => {
  const zones = suggestedTimeZones();
  assert.equal(zones[0], 'UTC');
  assert.equal(zones.filter((zone) => zone === 'UTC').length, 1);
});

test('the local zone follows UTC, exactly once', () => {
  const local = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const zones = suggestedTimeZones();
  if (local === 'UTC' || !ianaTimeZones().includes(local)) return;
  assert.equal(zones[1], local);
  assert.equal(zones.filter((zone) => zone === local).length, 1);
  assert.equal(zones.length, ianaTimeZones().length + 1);
});

test('an environment without supportedValuesOf yields no suggestions rather than throwing', () => {
  const original = (Intl as { supportedValuesOf?: unknown }).supportedValuesOf;
  try {
    delete (Intl as { supportedValuesOf?: unknown }).supportedValuesOf;
    assert.deepEqual(ianaTimeZones(), []);
    assert.deepEqual(suggestedTimeZones(), []);
  } finally {
    (Intl as { supportedValuesOf?: unknown }).supportedValuesOf = original;
  }
});
