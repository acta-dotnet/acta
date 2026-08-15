import { render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import WorkerDetailHarness from '../test/WorkerDetailHarness.svelte';

const worker = {
  workerRef: 'wrk_01kydka200fay8000000000002',
  jobNamespace: 'billing',
  status: 'active',
  host: 'host-a',
  deploymentVersion: 'deploy-1',
  engineVersion: 'engine-1',
  dotnetVersion: '.NET 10',
  processId: 4242,
  maxConcurrency: 8,
  lastHeartbeatAtUtc: '2026-07-14T08:00:00Z',
  startedAtUtc: '2026-07-14T07:00:00Z',
  modifiedAtUtc: '2026-07-14T08:00:00Z'
};

describe('WorkerDetail', () => {
  it('shows a loading state before the worker response arrives', async () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
    render(WorkerDetailHarness, { workerRef: worker.workerRef });

    // StateView holds the loading line back briefly so fast loads never flash it.
    expect(await screen.findByText('Loading worker...')).toBeTruthy();
  });

  it('renders durable worker identity and lifecycle evidence', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(worker), { status: 200 })));
    render(WorkerDetailHarness, { workerRef: worker.workerRef });

    expect(await screen.findByText('Live and eligible to claim jobs in its namespace.')).toBeTruthy();
    expect(screen.getByText('host-a')).toBeTruthy();
    expect(screen.getByText('deploy-1')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Namespace' }).getAttribute('href')).toContain('ns=billing');
  });

  it('shows a request error without replacing it with a not-found state', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ title: 'Backend unavailable', detail: 'Try again later.' }), { status: 503 }))
    );
    render(WorkerDetailHarness, { workerRef: worker.workerRef });

    expect(await screen.findByText('Try again later.')).toBeTruthy();
    expect(screen.queryByText('Worker not found.')).toBeNull();
  });
});
