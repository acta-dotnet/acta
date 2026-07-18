// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mergeHistory, type HistoryEvent } from './changeHistory.ts';

function event(jobEventId: number, createdAtUtc: string): HistoryEvent {
  return { jobEventId, eventCode: 'namespace.suspended', createdAtUtc, actorCode: 'operator', actorKey: null, reasonMessage: null };
}

test('mergeHistory: merges pages newest-first and breaks timestamp ties by event id', () => {
  const merged = mergeHistory([
    [event(1, '2026-07-01T00:00:00Z'), event(3, '2026-07-03T00:00:00Z')],
    [event(2, '2026-07-03T00:00:00Z')]
  ]);

  assert.deepEqual(merged.map((e) => e.jobEventId), [3, 2, 1]);
});

test('mergeHistory: dedupes by event id across pages', () => {
  assert.equal(mergeHistory([[event(1, '2026-07-01T00:00:00Z')], [event(1, '2026-07-01T00:00:00Z')]]).length, 1);
});

test('mergeHistory: caps the merged list at the limit', () => {
  const page = Array.from({ length: 30 }, (_, i) => event(i + 1, `2026-07-01T00:00:${String(i).padStart(2, '0')}Z`));

  assert.equal(mergeHistory([page], 20).length, 20);
});
