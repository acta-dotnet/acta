// Run with Node's built-in test runner + native type-stripping (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { canControl, capabilitiesQuery, keys, listPage } from './query.ts';
import { DETAIL_REFETCH_MS, detailRefetchInterval } from './polling.ts';

test('list keys drop blank filter values so blank and missing agree', () => {
  assert.deepEqual(keys.list('jobs', { status: '', tenantId: null, jobName: undefined }), ['jobs', {}]);
  assert.deepEqual(keys.list('jobs', { status: 'Failed' }), ['jobs', { status: 'Failed' }]);
});

test('detail keys are [entity, detail, ref] with a string ref', () => {
  assert.deepEqual(keys.detail('definitions', 7), ['definitions', 'detail', '7']);
});

test('feed keys are namespaced apart from list keys', () => {
  assert.notDeepEqual(keys.feed('events', {}), keys.list('events', {}));
});

test('listPage folds paging into the list key; first page carries no cursor', () => {
  const page1 = listPage('workers', { status: 'Active' }, { pageSize: 50, cursor: null });
  assert.deepEqual(page1.queryKey, ['workers', { status: 'Active', pageSize: 50 }]);
  assert.equal(page1.refetchInterval, 10_000);
});

test('listPage keys include includeTotal only when set, cursor only when paged', () => {
  const deep = listPage('workers', {}, { pageSize: 50, cursor: 'c9', includeTotal: true });
  assert.deepEqual(deep.queryKey, ['workers', { pageSize: 50, cursor: 'c9', includeTotal: true }]);
});

test('capabilitiesQuery is keyed and cached for the process lifetime', () => {
  const opts = capabilitiesQuery();
  assert.deepEqual(opts.queryKey, ['capabilities']);
  assert.equal(opts.staleTime, Infinity);
});

test('canControl fails closed when capabilities is undefined or controls are off', () => {
  assert.equal(canControl(undefined), false);
  assert.equal(canControl({ controlsEnabled: false, version: '1.0', provider: 'pg', confirmationHeader: 'X-Acta-Control' }), false);
});

test('canControl is true only when the server says controls are enabled', () => {
  assert.equal(canControl({ controlsEnabled: true, version: '1.0', provider: 'pg', confirmationHeader: 'X-Acta-Control' }), true);
});

test('detail polling stops when live updates are paused or the entity is inactive', () => {
  assert.equal(detailRefetchInterval(true, false), DETAIL_REFETCH_MS);
  assert.equal(detailRefetchInterval(true, true), false);
  assert.equal(detailRefetchInterval(false, false), false);
});
