import { test } from 'node:test';
import assert from 'node:assert/strict';
import { get } from 'svelte/store';
import { api, ApiError, controlRequest, online } from './api.ts';

(globalThis as unknown as { document: { baseURI: string } }).document = {
  baseURI: 'http://localhost/acta/jobs/'
};

test('api constructs a base-path URL and serializes nonblank query values', async () => {
  let requested = '';
  globalThis.fetch = (async (input: URL | RequestInfo) => {
    requested = String(input);
    return new Response('{"ok":true}', { status: 200 });
  }) as typeof fetch;

  await api('jobs', { status: 'failed', empty: '', missing: null, pageSize: 50, exact: true });

  const url = new URL(requested);
  assert.equal(url.pathname, '/acta/jobs/api/jobs');
  assert.equal(url.searchParams.get('status'), 'failed');
  assert.equal(url.searchParams.get('pageSize'), '50');
  assert.equal(url.searchParams.get('exact'), 'true');
  assert.equal(url.searchParams.has('empty'), false);
  assert.equal(url.searchParams.has('missing'), false);
});

test('api appends one query entry per non-blank array member', async () => {
  let requested = '';
  globalThis.fetch = (async (input: URL | RequestInfo) => {
    requested = String(input);
    return new Response('{"ok":true}', { status: 200 });
  }) as typeof fetch;

  await api('jobs', { tag: ['env:prod', '', 'team'] });

  const url = new URL(requested);
  assert.deepEqual(url.searchParams.getAll('tag'), ['env:prod', 'team']);
});

test('controlRequest serializes a JSON body and required headers', async () => {
  let init: RequestInit | undefined;
  globalThis.fetch = (async (_input: URL | RequestInfo, requestInit?: RequestInit) => {
    init = requestInit;
    return new Response('{"action":"applied"}', { status: 200 });
  }) as typeof fetch;

  await controlRequest('jobs/J1/pause', { reasonMessage: 'maintenance' }, { action: 'notFound' });

  const headers = new Headers(init?.headers);
  assert.equal(init?.method, 'POST');
  assert.equal(init?.body, '{"reasonMessage":"maintenance"}');
  assert.equal(headers.get('content-type'), 'application/json');
  assert.equal(headers.get('accept'), 'application/json');
  assert.equal(headers.get('x-acta-control'), 'true');
});

test('api preserves parsed ProblemDetails on ApiError', async () => {
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({ title: 'Query failed.', detail: 'Database unavailable.', traceId: 'trace-42' }),
      { status: 503 }
    )) as typeof fetch;

  await assert.rejects(
    () => api('jobs'),
    (error: unknown) => {
      assert.ok(error instanceof ApiError);
      assert.equal(error.status, 503);
      assert.equal(error.title, 'Query failed.');
      assert.equal(error.detail, 'Database unavailable.');
      assert.equal(error.traceId, 'trace-42');
      return true;
    }
  );
});

test('api reports a non-JSON error response by status', async () => {
  globalThis.fetch = (async () => new Response('upstream failed', { status: 502 })) as typeof fetch;

  await assert.rejects(
    () => api('jobs'),
    (error: unknown) => {
      assert.ok(error instanceof ApiError);
      assert.equal(error.message, 'HTTP 502');
      assert.equal(error.title, null);
      assert.equal(error.detail, null);
      assert.equal(error.traceId, null);
      return true;
    }
  );
});

test('api rejects empty, truncated, and non-JSON successful responses', async () => {
  for (const body of ['', '{"items":', 'upstream returned HTML']) {
    globalThis.fetch = (async () => new Response(body, { status: 200 })) as typeof fetch;

    await assert.rejects(
      () => api('jobs'),
      (error: unknown) => {
        assert.ok(error instanceof ApiError);
        assert.equal(error.status, 200);
        assert.equal(error.title, 'Invalid response.');
        assert.equal(error.detail, 'Expected a JSON response body.');
        return true;
      }
    );
  }
});

test('network failures mark the backend offline', async () => {
  online.set(true);
  globalThis.fetch = (async () => {
    throw new TypeError('fetch failed');
  }) as typeof fetch;

  await assert.rejects(() => api('jobs'), /fetch failed/);
  assert.equal(get(online), false);
});

test('abort failures do not mark the backend offline', async () => {
  online.set(true);
  globalThis.fetch = (async () => {
    throw Object.assign(new Error('aborted'), { name: 'AbortError' });
  }) as typeof fetch;

  const controller = new AbortController();
  controller.abort();
  await assert.rejects(() => api('jobs', {}, { signal: controller.signal }), /aborted/);
  assert.equal(get(online), true);
});

test('any HTTP response marks the backend online even when it is an error', async () => {
  online.set(false);
  globalThis.fetch = (async () => new Response(null, { status: 500 })) as typeof fetch;

  await assert.rejects(() => api('jobs'), ApiError);
  assert.equal(get(online), true);
});

test('controlRequest accepts typed 404 and 409 outcomes', async () => {
  const responses = [
    new Response('{"action":"notFound"}', { status: 404 }),
    new Response('{"action":"rejected"}', { status: 409 })
  ];
  globalThis.fetch = (async () => responses.shift()!) as typeof fetch;

  const notFound = await controlRequest('jobs/J1/pause', {}, { action: 'notFound' });
  const rejected = await controlRequest('jobs/J1/pause', {}, { action: 'notFound' });

  assert.equal(notFound.action, 'notFound');
  assert.equal(rejected.action, 'rejected');
});

test('controlRequest keeps the typed fallback for a malformed accepted error response', async () => {
  globalThis.fetch = (async () => new Response('upstream returned HTML', { status: 404 })) as typeof fetch;

  const result = await controlRequest('jobs/J1/pause', {}, { action: 'notFound' });

  assert.equal(result.action, 'notFound');
});
