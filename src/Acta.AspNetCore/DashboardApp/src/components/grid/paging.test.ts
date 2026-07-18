import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  first,
  current,
  canPrev,
  push,
  pop,
  gridDisplay,
  shouldIncludeTotal,
  createPagingState,
  pagingFor,
  nextPaging,
  exactCountPaging
} from './paging.ts';

test('exact counts stay off for large grids until explicitly requested', () => {
  assert.equal(shouldIncludeTotal('none', true), false);
  assert.equal(shouldIncludeTotal('on-demand', false), false);
  assert.equal(shouldIncludeTotal('on-demand', true), true);
  assert.equal(shouldIncludeTotal('always', false), true);
  assert.equal(shouldIncludeTotal('none', false, true), true);
});

test('starts on the first page with no prev', () => {
  assert.equal(current(first), null);
  assert.equal(canPrev(first), false);
});

test('push advances, pop returns, floor is the first page', () => {
  let stack = push(first, 'c1');
  assert.equal(current(stack), 'c1');
  assert.equal(canPrev(stack), true);
  stack = push(stack, 'c2');
  assert.equal(current(stack), 'c2');
  stack = pop(stack);
  assert.equal(current(stack), 'c1');
  stack = pop(pop(stack));
  assert.equal(current(stack), null);
  assert.equal(canPrev(stack), false);
});

test('push without a cursor stays put', () => {
  assert.equal(push(first, null), first);
});

test('filter change resets the active cursor', () => {
  const old = nextPaging(createPagingState('status=ready'), 'status=ready', 'ready-cursor');
  const changed = pagingFor(old, 'status=failed');

  assert.equal(current(changed.stack), null);
  assert.equal(changed.filterKey, 'status=failed');
});

test('filter change resets an exact-count request', () => {
  const old = exactCountPaging(createPagingState('status=ready'), 'status=ready');

  assert.equal(pagingFor(old, 'status=failed').countRequested, false);
});

test('next page after a filter change starts from the new first page', () => {
  const old = nextPaging(createPagingState('status=ready'), 'status=ready', 'ready-cursor');
  const changed = nextPaging(old, 'status=failed', 'failed-cursor');

  assert.deepEqual(changed.stack, [null, 'failed-cursor']);
});

test('previous filter cursors cannot leak into the new query', () => {
  let old = nextPaging(createPagingState('status=ready'), 'status=ready', 'ready-cursor-1');
  old = nextPaging(old, 'status=ready', 'ready-cursor-2');

  assert.deepEqual(pagingFor(old, 'status=failed').stack, [null]);
});

test('gridDisplay: loading or error shows only the state view, no pager', () => {
  assert.deepEqual(gridDisplay(true, false, 0, 0), { showState: true, showTable: false, showPager: false });
  assert.deepEqual(gridDisplay(false, true, 0, 0), { showState: true, showTable: false, showPager: false });
});

test('gridDisplay: a genuinely empty raw page is a true empty state with no pager', () => {
  assert.deepEqual(gridDisplay(false, false, 0, 0), { showState: true, showTable: false, showPager: false });
});

test('gridDisplay: raw rows hidden by the filter keep the pager reachable (sparse-bucket dead-end fix)', () => {
  assert.deepEqual(gridDisplay(false, false, 50, 0), { showState: true, showTable: false, showPager: true });
});

test('gridDisplay: rows survive the filter, show the table and the pager', () => {
  assert.deepEqual(gridDisplay(false, false, 50, 3), { showState: false, showTable: true, showPager: true });
});

test('gridDisplay: a failed poll or background refetch over cached rows keeps the table (no spinner/error swap)', () => {
  assert.deepEqual(gridDisplay(false, true, 50, 3), { showState: false, showTable: true, showPager: true });
  assert.deepEqual(gridDisplay(true, false, 50, 3), { showState: false, showTable: true, showPager: true });
});
