import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobDiagnosis from './JobDiagnosis.svelte';
import type { JobExplanation } from './types.ts';
import { routes } from '../../routes.ts';

const workerRef = 'wrk_019f71ce69e07b85bf41fc7bf0562509';

function explanation(overrides: Partial<JobExplanation>): JobExplanation {
  return {
    headline: 'Executing.',
    activeWait: null,
    lease: null,
    lastExecutedBy: null,
    steps: [],
    stepsHasMore: false,
    reason: null,
    nextActions: [],
    ...overrides
  };
}

describe('JobDiagnosis', () => {
  it('names the lease holder by its ref, with its deployment version beside it', () => {
    const lease = {
      workerRef,
      workerVersion: '1.0.0',
      expiresAtUtc: null,
      expired: false,
      workerLastSeenAtUtc: null,
      workerStale: false,
      recoveryExpectation: ''
    };
    const { container, getByText } = render(JobDiagnosis, { jobNamespace: 'billing', explanation: explanation({ lease }) });
    const link = container.querySelector(`a[href="${routes.worker(workerRef, { namespace: 'billing' })}"]`);
    expect(link?.textContent).toBe(workerRef);
    expect(getByText(/version 1\.0\.0/)).toBeTruthy();
  });

  it('spaces the lease holder apart from its version and its heartbeat', () => {
    const lease = {
      workerRef,
      workerVersion: '1.0.0',
      expiresAtUtc: '2026-07-14T08:00:00Z',
      expired: true,
      workerLastSeenAtUtc: '2026-07-14T07:58:00Z',
      workerStale: true,
      recoveryExpectation: 'Recovery reclaims the job on its next pass.'
    };
    const { container } = render(JobDiagnosis, { jobNamespace: 'billing', explanation: explanation({ lease }) });
    expect(container.querySelector('#job-worker-evidence')?.textContent).toMatch(
      new RegExp(`^${workerRef} · version 1\\.0\\.0 · lease expired .+ · last heartbeat `)
    );
  });
});
