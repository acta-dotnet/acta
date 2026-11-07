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
      workerName: '1.0.0',
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
});
