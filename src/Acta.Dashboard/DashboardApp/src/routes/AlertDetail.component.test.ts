import { render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import AlertDetailHarness from '../test/AlertDetailHarness.svelte';

const alertRef = 'alr_019f9b3508007e4dbfd13d9d20bc81f4';

// An automatic alert about a failed job, sent once; retryAfterUtc then holds the next send, which depends on the status.
function alert(deliveryStatus: string, retryCount: number) {
  return {
    alertRef,
    jobNamespace: 'shipping',
    jobRef: 'job_019f71ce69e07b85bf41fc7bf0562502',
    origin: 'automatic',
    severity: 'error',
    kind: 'final-failure',
    title: "Job 'print-label' failed",
    message: 'Terminal failure: Printer offline.',
    channelName: 'default',
    occurrenceCount: 1,
    resolvedAtUtc: null,
    deliveryStatus,
    retryCount,
    retryAfterUtc: '2026-11-09T05:51:00Z',
    createdAtUtc: '2026-11-08T05:51:00Z',
    modifiedAtUtc: '2026-11-08T05:51:00Z',
    acknowledgedAtUtc: null
  };
}

function serve(body: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(input as string | URL);
      if (url.pathname.endsWith('/capabilities')) {
        return new Response(JSON.stringify({ controlsEnabled: false, engineVersion: 'v', provider: 'sqlite', confirmationHeader: 'X-Acta-Control', permissions: ['acta.read'] }), { status: 200 });
      }
      return new Response(JSON.stringify(body), { status: 200 });
    })
  );
}

describe('AlertDetail', () => {
  it('names a delivered alert\'s next send as its reminder, not a next attempt', async () => {
    serve(alert('delivered', 0));
    render(AlertDetailHarness, { alertRef });

    expect(await screen.findByText('Next reminder')).toBeTruthy();
    expect(screen.queryByText('Next attempt')).toBeNull();
  });

  it('names a deferred send\'s next try as its next attempt', async () => {
    serve(alert('retry-after', 2));
    render(AlertDetailHarness, { alertRef });

    expect(await screen.findByText('Next attempt')).toBeTruthy();
    expect(screen.queryByText('Next reminder')).toBeNull();
  });
});
