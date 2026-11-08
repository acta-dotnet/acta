import { render } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import WorkersListHarness from '../test/WorkersListHarness.svelte';
import { setScope } from '../scope.ts';

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });
const paged = (items: unknown[]) => ({ items, nextCursor: null, hasMore: false, pageSize: 100, totalCount: items.length });

const worker = (workerRef: string, lastSeenAtUtc: string, stale: boolean) => ({
  workerRef,
  jobNamespace: 'billing',
  status: 'active',
  stale,
  hostName: 'host-a',
  deploymentVersion: '1.0.0',
  engineVersion: null,
  dotnetVersion: null,
  processId: null,
  maxConcurrentExecutors: 4,
  lastSeenAtUtc,
  startedAtUtc: lastSeenAtUtc,
  modifiedAtUtc: lastSeenAtUtc
});

describe('WorkersList', () => {
  // The server judges a worker stale against its own lease window, so a row follows that flag, not this browser's clock.
  it('marks the workers the server judges stale, whatever their last heartbeat reads here', async () => {
    setScope('');
    location.hash = '#/workers';
    const judged = worker('wrk_019f9b3508007e4dbfd13d9d20bc81f4', new Date().toISOString(), true);
    const fresh = worker('wrk_019f9b3508007e4dbfd13d9d20bc81f5', new Date(Date.now() - 600_000).toISOString(), false);
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = new URL(input as string | URL);
        if (url.pathname.endsWith('/capabilities')) {
          return json({ controlsEnabled: false, engineVersion: 'v', provider: 'sqlite', confirmationHeader: 'X-Acta-Control', permissions: ['acta.read'] });
        }
        if (url.pathname.endsWith('/workers')) return json(paged([judged, fresh]));
        return json(paged([]));
      })
    );

    const { container } = render(WorkersListHarness);

    await vi.waitFor(() => expect(container.querySelectorAll('tbody tr').length).toBe(2));
    const [first, second] = container.querySelectorAll('tbody tr');
    expect(first.classList.contains('stale')).toBe(true);
    expect(first.querySelector('td[data-label="Status"]')?.textContent).toContain('stale');
    expect(second.classList.contains('stale')).toBe(false);
    expect(second.querySelector('td[data-label="Status"]')?.textContent).not.toContain('stale');
  });
});
