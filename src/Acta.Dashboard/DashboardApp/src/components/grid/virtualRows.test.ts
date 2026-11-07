import { test } from 'node:test';
import assert from 'node:assert/strict';
import { renderedVirtualRows } from './virtualRows.ts';

test('virtual rows discard stale indices before computing row keys', () => {
  const keyed: unknown[] = [];
  const rows = renderedVirtualRows(
    [
      { index: 1, start: 40, end: 80 },
      { index: 125, start: 5000, end: 5040 }
    ],
    [{ id: 'a' }, { id: 'b' }],
    (item) => {
      keyed.push(item);
      return item.id;
    }
  );

  assert.deepEqual(rows, [
    {
      item: { id: 'b' },
      key: 'b',
      virtualRow: { index: 1, start: 40, end: 80 }
    }
  ]);
  assert.deepEqual(keyed, [{ id: 'b' }]);
});
