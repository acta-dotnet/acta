import assert from 'node:assert/strict';
import test from 'node:test';
import { displayFormatter, parseUtcDateTimeInput } from './format.ts';

test('numbers use Latin digits, a decimal point, and no grouping', () => {
  assert.equal(displayFormatter.number(1234567.89), '1234567.89');
  assert.equal(displayFormatter.number(-1234567.89), '-1234567.89');
  assert.equal(Object.isFrozen(displayFormatter), true);
});

test('human measurements retain invariant numeric formatting', () => {
  const now = Date.parse('2026-01-01T00:00:00Z');
  assert.equal(displayFormatter.duration(12345 * 86400), '12345d 0h');
  assert.equal(displayFormatter.relativeTime(new Date(now + 12345 * 86400000).toISOString(), now), 'in 12345d');
  assert.equal(displayFormatter.bytes(1536), '1.5 KB');
  assert.equal(displayFormatter.bytes(1023), '1023 B');
  assert.equal(displayFormatter.milliseconds(1234567), '1234567 ms');
});

test('zoned timestamps use fixed ISO order with DST offset and IANA zone', () => {
  assert.equal(
    displayFormatter.timestampInZone('2026-07-15T06:26:52Z', 'Europe/Ljubljana'),
    '2026-07-15 08:26:52 +02:00 [Europe/Ljubljana]'
  );
  assert.equal(
    displayFormatter.timestampInZone('2026-01-15T07:26:52Z', 'Europe/Ljubljana'),
    '2026-01-15 08:26:52 +01:00 [Europe/Ljubljana]'
  );
  assert.equal(
    displayFormatter.timestampInZone('2026-07-15T06:26:52Z', 'UTC'),
    '2026-07-15 06:26:52 +00:00 [UTC]'
  );
  const kathmanduZone = new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Kathmandu' })
    .resolvedOptions()
    .timeZone;
  assert.equal(
    displayFormatter.timestampInZone('2026-07-15T06:26:52Z', 'Asia/Kathmandu'),
    `2026-07-15 12:11:52 +05:45 [${kathmanduZone}]`
  );
});

test('UTC timestamps without a suffix are still interpreted as UTC', () => {
  assert.equal(
    displayFormatter.timestampInZone('2026-07-15T06:26:52', 'Europe/Ljubljana'),
    '2026-07-15 08:26:52 +02:00 [Europe/Ljubljana]'
  );
});

test('an invalid IANA zone falls back to the browser zone', () => {
  const formatted = displayFormatter.timestampInZone('2026-07-15T06:26:52Z', 'Invalid/Zone');

  assert.equal(formatted.endsWith(`[${displayFormatter.localTimeZone}]`), true);
  assert.equal(formatted.includes('[Invalid/Zone]'), false);
});

test('relative and exact timestamps share UTC parsing and invalid-date behavior', () => {
  const now = Date.parse('2026-07-15T06:25:52Z');
  assert.equal(displayFormatter.relativeTime('2026-07-15T06:26:52', now), 'in 1m');
  assert.equal(displayFormatter.relativeTime('not-a-date', now), 'Invalid date');
});

test('strict UTC input accepts optional seconds and normalizes the wire value', () => {
  assert.deepEqual(parseUtcDateTimeInput('2028-02-29 08:26'), {
    ok: true,
    wire: '2028-02-29T08:26:00.000Z'
  });
  assert.deepEqual(parseUtcDateTimeInput('2026-07-15 08:26:52'), {
    ok: true,
    wire: '2026-07-15T08:26:52.000Z'
  });
});

test('strict UTC input rejects rollover dates and locale-dependent forms', () => {
  assert.equal(parseUtcDateTimeInput('2026-02-29 08:26').ok, false);
  assert.equal(parseUtcDateTimeInput('2026-13-01 08:26').ok, false);
  assert.equal(parseUtcDateTimeInput('2026-07-15 24:00').ok, false);
  assert.equal(parseUtcDateTimeInput('15/07/2026 08:26').ok, false);
  assert.equal(parseUtcDateTimeInput('2026-07-15T08:26:52Z').ok, false);
});

test('missing and invalid timestamps retain deterministic fallbacks', () => {
  assert.equal(displayFormatter.timestampInZone(null, 'UTC'), '');
  assert.equal(displayFormatter.timestampInZone('not-a-date', 'UTC'), 'Invalid date');
  assert.equal(displayFormatter.duration(null), '-');
  assert.equal(displayFormatter.bytes(undefined), '-');
});

test('row timestamps drop the offset and zone suffix but keep the full instant', () => {
  assert.equal(displayFormatter.rowTimestampInZone('2026-07-15T06:26:52Z', 'Europe/Ljubljana'), '2026-07-15 08:26:52');
  assert.equal(displayFormatter.rowTimestampInZone('not-a-date', 'Europe/Ljubljana'), 'Invalid date');
  assert.equal(displayFormatter.rowTimestampInZone(null, 'Europe/Ljubljana'), '');
  // rowTimestamp = rowTimestampInZone in the browser zone.
  assert.equal(
    displayFormatter.rowTimestamp('2026-07-15T06:26:52Z'),
    displayFormatter.rowTimestampInZone('2026-07-15T06:26:52Z', displayFormatter.localTimeZone)
  );
});

test('the zone note names the local zone and its offset at the given instant', () => {
  const note = displayFormatter.zoneNote(Date.parse('2026-07-15T06:26:52Z'));
  assert.match(note, new RegExp(`^Times in ${displayFormatter.localTimeZone.replace('/', '\\/')} \\([+-]\\d\\d:\\d\\d\\)$`));
});
