import { fireEvent, render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import JobsListHarness from '../test/JobsListHarness.svelte';
import { setScope } from '../scope.ts';

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });
const paged = (items: unknown[]) => ({ items, nextCursor: null, hasMore: false, pageSize: 100, totalCount: items.length });

describe('JobsList', () => {
  // Six attempts, all failed, with a restart after the third: the restart reset the count, which counts failures in a row.
  it('words a job\'s failure count as its consecutive failures', async () => {
    setScope('');
    location.hash = '#/jobs';
    const job = {
      jobRef: 'job_019f71ce69e07b85bf41fc7bf0562503',
      jobName: 'print-label',
      jobNamespace: 'billing',
      status: 'failed',
      tenantName: null,
      createdAtUtc: '2026-07-14T08:00:00Z',
      nextRunAtUtc: null,
      executionNumber: 6,
      failureCount: 3
    };
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = new URL(input as string | URL);
        if (url.pathname.endsWith('/capabilities')) {
          return json({ controlsEnabled: false, engineVersion: 'v', provider: 'sqlite', confirmationHeader: 'X-Acta-Control', permissions: ['acta.read'] });
        }
        if (url.pathname.endsWith('/jobs')) return json(paged([job]));
        return json(paged([]));
      })
    );

    const { container } = render(JobsListHarness);

    await vi.waitFor(() => expect(container.querySelector('td[data-label="Attempts"]')?.textContent?.trim()).toBe('6 (3 consecutive failures)'));
  });

  it('looks a deduplication key up in the scope picked while the namespaces load', async () => {
    location.hash = '#/jobs';
    let answerNamespaces = () => {};
    const namespacesAnswered = new Promise<void>((resolve) => (answerNamespaces = resolve));
    const lookups: URL[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = new URL(input as string | URL);
        if (url.pathname.endsWith('/capabilities')) {
          return json({ controlsEnabled: false, engineVersion: 'v', provider: 'sqlite', confirmationHeader: 'X-Acta-Control', permissions: ['acta.read'] });
        }
        if (url.pathname.endsWith('/namespaces')) {
          await namespacesAnswered;
          return json(paged(['billing', 'shipping'].map((jobNamespace) => ({ jobNamespace, status: 'active', ownerTeam: null, description: null, deploymentVersion: null, version: 1 }))));
        }
        if (url.pathname.endsWith('/jobs/by-deduplication-key')) {
          lookups.push(url);
          return json({ jobRef: 'job_019f71ce69e07b85bf41fc7bf0562502' });
        }
        return json(paged([]));
      })
    );

    render(JobsListHarness);
    const jump = screen.getByLabelText('Jump to');
    await fireEvent.input(jump, { target: { value: 'invoice-42' } });
    await fireEvent.keyDown(jump, { key: 'Enter' });
    setScope('billing');
    answerNamespaces();

    await vi.waitFor(() => expect(lookups.map((url) => url.searchParams.get('jobNamespace'))).toEqual(['billing']));
    expect(screen.queryByText('Select a namespace scope to look up a deduplication key.')).toBeNull();
  });

  it('looks a deduplication key up in a scope picked while a failing namespaces read is pending', async () => {
    // The scope store outlives a test; start unscoped, as the case needs.
    setScope('');
    location.hash = '#/jobs';
    let answerNamespaces = () => {};
    const namespacesAnswered = new Promise<void>((resolve) => (answerNamespaces = resolve));
    const lookups: URL[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = new URL(input as string | URL);
        if (url.pathname.endsWith('/capabilities')) {
          return json({ controlsEnabled: false, engineVersion: 'v', provider: 'sqlite', confirmationHeader: 'X-Acta-Control', permissions: ['acta.read'] });
        }
        if (url.pathname.endsWith('/namespaces')) {
          await namespacesAnswered;
          return new Response(JSON.stringify({ title: 'The store is unavailable.' }), { status: 503 });
        }
        if (url.pathname.endsWith('/jobs/by-deduplication-key')) {
          lookups.push(url);
          return json({ jobRef: 'job_019f71ce69e07b85bf41fc7bf0562502' });
        }
        return json(paged([]));
      })
    );

    render(JobsListHarness);
    const jump = screen.getByLabelText('Jump to');
    await fireEvent.input(jump, { target: { value: 'invoice-42' } });
    await fireEvent.keyDown(jump, { key: 'Enter' });
    setScope('billing');
    answerNamespaces();

    await vi.waitFor(() => expect(lookups.map((url) => url.searchParams.get('jobNamespace'))).toEqual(['billing']));
  });
});
