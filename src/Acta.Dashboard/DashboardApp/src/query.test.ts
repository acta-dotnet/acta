// Run with Node's built-in test runner + native type-stripping (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { can, canWriteAny, capabilitiesQuery, keys, LIST_REFETCH_MS, listPage, Permission, retryOnce } from './query.ts';
import { ApiError } from './api.ts';
import { DETAIL_REFETCH_MS, detailRefetchInterval, listRefetchDelay, listRefetchInterval } from './polling.ts';

test('list keys drop blank filter values so blank and missing agree', () => {
  assert.deepEqual(keys.list('jobs', { status: '', tenantName: null, jobName: undefined }), ['jobs', {}]);
  assert.deepEqual(keys.list('jobs', { status: 'failed' }), ['jobs', { status: 'failed' }]);
});

test('detail keys are [entity, detail, ref] with a string ref', () => {
  assert.deepEqual(keys.detail('definitions', 7), ['definitions', 'detail', '7']);
});

test('feed keys are namespaced apart from list keys', () => {
  assert.notDeepEqual(keys.feed('events', {}), keys.list('events', {}));
});

test('listPage folds paging into the list key; first page carries no cursor', () => {
  const page1 = listPage('workers', { status: 'active' }, { pageSize: 50, cursor: null });
  assert.deepEqual(page1.queryKey, ['workers', { status: 'active', pageSize: 50 }]);
  assert.equal(page1.refetchInterval, 10_000);
});

test('listPage keys include includeTotal only when set, cursor only when paged', () => {
  const deep = listPage('workers', {}, { pageSize: 50, cursor: 'c9', includeTotal: true });
  assert.deepEqual(deep.queryKey, ['workers', { pageSize: 50, cursor: 'c9', includeTotal: true }]);
});

test('capabilitiesQuery is keyed and read again on focus, since grants change while a page is open', () => {
  const opts = capabilitiesQuery();
  assert.deepEqual(opts.queryKey, ['capabilities']);
  assert.notEqual(opts.staleTime, Infinity);
  assert.equal(opts.refetchOnWindowFocus, 'always');
});

const caps = (controlsEnabled: boolean, permissions: string[]) => ({
  controlsEnabled,
  provider: 'pg',
  schema: 'acta',
  confirmationHeader: 'X-Acta-Control',
  permissions
});

test('can fails closed until capabilities load, when controls are off, or when the caller lacks the permission', () => {
  assert.equal(can(undefined, Permission.control), false);
  assert.equal(can(caps(false, ['acta.read', 'acta.control']), Permission.control), false);
  assert.equal(can(caps(true, ['acta.read']), Permission.control), false);
  assert.equal(can(caps(true, ['acta.read', 'acta.control']), Permission.control), true);
  assert.equal(can(caps(true, ['acta.read', 'acta.control']), Permission.admin), false);
});

test('canWriteAny is false for a reader and true for any write permission', () => {
  assert.equal(canWriteAny(caps(true, ['acta.read'])), false);
  assert.equal(canWriteAny(caps(true, ['acta.read', 'acta.enqueue'])), true);
  assert.equal(canWriteAny(caps(false, ['acta.read', 'acta.admin'])), false);
});

test('detail polling stops when live updates are paused or the entity is inactive', () => {
  assert.equal(detailRefetchInterval(true, false), DETAIL_REFETCH_MS);
  assert.equal(detailRefetchInterval(true, true), false);
  assert.equal(detailRefetchInterval(false, false), false);
});

test('a polled list waits its cadence plus a jitter that its data timestamp fixes', () => {
  const updatedAt = 1_500_000_000_700;
  assert.equal(listRefetchDelay(updatedAt), LIST_REFETCH_MS + 700);
  assert.equal(listRefetchInterval({ state: { fetchFailureCount: 0, dataUpdatedAt: updatedAt } }), listRefetchDelay(updatedAt));
  assert.equal(listRefetchDelay(updatedAt, 1), 2 * LIST_REFETCH_MS + 700);
  assert.equal(listRefetchDelay(updatedAt, 10), 120_000 + 700);
});

test('a query retries once, but never a 401', () => {
  assert.equal(retryOnce(0, new ApiError(503, 'Unavailable', null, null)), true);
  assert.equal(retryOnce(1, new ApiError(503, 'Unavailable', null, null)), false);
  assert.equal(retryOnce(0, new ApiError(401, null, null, null)), false);
  assert.equal(retryOnce(0, new Error('network')), true);
});
