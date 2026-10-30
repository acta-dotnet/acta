import { test } from 'node:test';
import assert from 'node:assert/strict';
import type { Paged } from '../api.ts';
import { PAGE_WALK_BOUND, readAllPages } from './pagedWalk.ts';

const page = <T>(items: T[], nextCursor: string | null): Paged<T> => ({ items, nextCursor, hasMore: nextCursor !== null, totalCount: null });

test('three pages of a hundred come back as one list in order, each asked with the cursor before it', async () => {
  const names = Array.from({ length: 205 }, (_, i) => `job-${String(i + 1).padStart(3, '0')}`);
  const asked: (string | undefined)[] = [];
  const items = await readAllPages<string>(async (cursor) => {
    asked.push(cursor);
    if (cursor === undefined) return page(names.slice(0, 100), 'c1');
    if (cursor === 'c1') return page(names.slice(100, 200), 'c2');
    return page(names.slice(200), null);
  });

  assert.deepEqual(items, names);
  assert.deepEqual(asked, [undefined, 'c1', 'c2']);
});

test('a page that claims more without a cursor is an error, not a shorter list', async () => {
  await assert.rejects(
    readAllPages(async () => ({ items: ['a'], nextCursor: null, hasMore: true, totalCount: null })),
    /claims more after page 1 but gave no cursor/
  );
});

test('a repeated cursor is an error before the walk loops', async () => {
  let calls = 0;
  await assert.rejects(
    readAllPages(async () => {
      calls++;
      return page(['a'], 'same');
    }),
    /repeated its cursor after page 2/
  );
  assert.equal(calls, 2);
});

test('a list that never ends stops at the bound with an error', async () => {
  let calls = 0;
  await assert.rejects(
    readAllPages(async () => page(['a'], `c${++calls}`)),
    new RegExp(`did not end within ${PAGE_WALK_BOUND} pages`)
  );
  assert.equal(calls, PAGE_WALK_BOUND);
});

test('a fetch that throws ends the walk with that error', async () => {
  await assert.rejects(
    readAllPages(async (cursor) => (cursor === undefined ? page(['a'], 'c1') : Promise.reject(new Error('aborted')))),
    /aborted/
  );
});
