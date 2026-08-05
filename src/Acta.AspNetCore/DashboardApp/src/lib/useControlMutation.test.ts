// Run with Node's built-in test runner + native type-stripping (see "npm test"). createMutation and
// useQueryClient need a Svelte component context, so useControlMutation() itself is thin, untested
// wiring; the logic it's built from - buildBody, invalidateAll, and controlRequest's 409/404 mapping
// (imported from api.ts, which useControlMutation calls through) - is plain and tested directly here.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { QueryClient } from '@tanstack/query-core';
import { controlRequest } from '../api.ts';
import { buildBody, invalidateAll } from './controlMutation.ts';

// controlRequest builds a URL against document.baseURI; stub a minimal document since this suite
// runs under node --test (no DOM).
(globalThis as unknown as { document: { baseURI: string } }).document = { baseURI: 'http://localhost/' };

test('buildBody trims the reason and merges it with the caller body, blank reason becomes null', () => {
  assert.deepEqual(buildBody({ reason: '  keep it  ' }, (v) => ({ foo: 1 })), { foo: 1, reasonMessage: 'keep it' });
  assert.deepEqual(buildBody({ reason: '   ' }, undefined), { reasonMessage: null });
  assert.deepEqual(buildBody({}, (v) => ({ a: 2 })), { a: 2, reasonMessage: null });
});

test('invalidateAll marks every caller-named query key invalidated', () => {
  const queryClient = new QueryClient();
  queryClient.setQueryData(['jobs', 'detail', 'J1'], { status: 'executing' });
  queryClient.setQueryData(['jobs', 'feed', {}], { items: [] });

  invalidateAll(queryClient, [['jobs', 'detail', 'J1'], ['jobs', 'feed', {}]]);

  assert.equal(queryClient.getQueryState(['jobs', 'detail', 'J1'])?.isInvalidated, true);
  assert.equal(queryClient.getQueryState(['jobs', 'feed', {}])?.isInvalidated, true);
});

test('controlRequest maps a 409 with a typed body to the rejected result', async () => {
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ action: 'rejected', message: 'Job is not pausable.' }), { status: 409 })) as typeof fetch;

  const result = await controlRequest(
    'jobs/J1/pause',
    { reasonMessage: null },
    { action: 'notFound', jobRef: 'J1', status: null, message: 'Job not found.' },
    'POST'
  );

  assert.equal(result.action, 'rejected');
});

test('controlRequest maps a 404 with no body to the caller-supplied not-found fallback', async () => {
  globalThis.fetch = (async () => new Response(null, { status: 404 })) as typeof fetch;

  const notFound = { action: 'notFound', jobRef: 'J1', status: null, message: 'Job not found.' };
  const result = await controlRequest('jobs/J1/pause', { reasonMessage: null }, notFound, 'POST');

  assert.deepEqual(result, notFound);
});

test('controlRequest returns the parsed body on 200', async () => {
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ action: 'applied', message: 'Paused.' }), { status: 200 })) as typeof fetch;

  const result = await controlRequest(
    'jobs/J1/pause',
    { reasonMessage: null },
    { action: 'notFound', jobRef: 'J1', status: null, message: 'Job not found.' },
    'POST'
  );

  assert.deepEqual(result, { action: 'applied', message: 'Paused.' });
});

test('controlRequest sends a truly empty body (no bytes) when body is undefined - presence-only signal', async () => {
  let capturedBody: unknown;
  globalThis.fetch = (async (_url: unknown, init?: RequestInit) => {
    capturedBody = init?.body;
    return new Response(JSON.stringify({ action: 'applied', message: 'Signal raised.' }), { status: 200 });
  }) as typeof fetch;

  const result = await controlRequest(
    'jobs/J1/signals/retry',
    undefined,
    { action: 'notFound', jobRef: 'J1', status: null, message: 'Job not found.' },
    'POST'
  );

  assert.equal(capturedBody, undefined);
  assert.equal(result.action, 'applied');
});

test('controlRequest maps a 409 with no typed body to the caller-supplied versionConflict fallback', async () => {
  // Admin control (tenant/namespace suspend/resume/details) returns a bare Problem body on
  // VersionConflict - no 'action' field - unlike job/schedule/alert control, which always includes
  // a typed action even at 409.
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ title: 'Version conflict.', detail: 'stale version' }), { status: 409 })) as typeof fetch;

  const versionConflict = { action: 'versionConflict', version: null };
  const result = await controlRequest('tenants/t1', { expectedVersion: 1 }, { action: 'notFound', version: null }, 'PATCH', versionConflict);

  assert.deepEqual(result, versionConflict);
});

test('controlRequest still throws on a 409 with no typed body when no versionConflict fallback is given', async () => {
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ title: 'Version conflict.', detail: 'stale version' }), { status: 409 })) as typeof fetch;

  await assert.rejects(
    () => controlRequest('tenants/t1', { expectedVersion: 1 }, { action: 'notFound', version: null }, 'PATCH'),
    /stale version/
  );
});

test('controlRequest throws with the problem detail on an unmapped status', async () => {
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ detail: 'Controls are disabled on this host.' }), { status: 400 })) as typeof fetch;

  await assert.rejects(
    () =>
      controlRequest(
        'jobs/J1/pause',
        { reasonMessage: null },
        { action: 'notFound', jobRef: 'J1', status: null, message: 'Job not found.' },
        'POST'
      ),
    /Controls are disabled on this host\./
  );
});
