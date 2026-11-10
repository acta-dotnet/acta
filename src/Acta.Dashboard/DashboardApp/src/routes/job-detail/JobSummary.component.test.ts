import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobSummary from './JobSummary.svelte';
import type { JobDetail } from './types.ts';
import { routes } from '../../routes.ts';
import { displayFormatter } from '../../format.ts';

const head = 'job_019f71ce69e07b85bf41fc7bf0562501';

function job(overrides: Partial<JobDetail>): JobDetail {
  return {
    jobRef: 'job_019f71ce69e07b85bf41fc7bf0562502',
    kind: 'ordinary',
    jobNamespace: 'billing',
    jobName: 'send-invoice',
    lineageRootJobRef: null,
    parentJobRef: null,
    tenantName: null,
    deduplicationKey: null,
    correlationKey: null,
    concurrencyKey: null,
    inputFormatId: 1,
    status: 'ready',
    priority: 'normal',
    lane: null,
    blockedBehindJobRef: null,
    nextRunAtUtc: null,
    executionNumber: 0,
    failureCount: 0,
    leasedByWorkerRef: null,
    leaseExpiresAtUtc: null,
    retentionUntilUtc: null,
    createdAtUtc: '2026-07-14T08:00:00Z',
    modifiedAtUtc: '2026-07-14T08:00:00Z',
    version: 1,
    ...overrides
  };
}

describe('JobSummary technical details', () => {
  it('keeps the job name and status out of technical metadata', () => {
    const { queryByText } = render(JobSummary, { job: job({}) });
    expect(queryByText('send-invoice')).toBeNull();
    expect(queryByText('Status')).toBeNull();
  });

  it('links the lane to the filtered jobs list', () => {
    const { container } = render(JobSummary, { job: job({ lane: 'customer-42' }) });
    const href = routes.jobs({ lane: 'customer-42', namespace: 'billing' });
    expect([...container.querySelectorAll('a')].find((link) => link.getAttribute('href') === href)?.textContent).toBe('customer-42');
  });

  it('shows the complete reference and dates in technical details', () => {
    const current = job({});
    const { getByText } = render(JobSummary, { job: current });
    expect(getByText(current.jobRef)).toBeTruthy();
    expect(getByText('Created')).toBeTruthy();
    expect(getByText('Modified')).toBeTruthy();
  });

  it('keeps a permanent job budget scoped to each firing', () => {
    const { getByText } = render(JobSummary, { job: job({ kind: 'permanent' }), maxAttempts: 15 });
    expect(getByText('Attempt budget').nextElementSibling?.textContent?.trim()).toBe('15 per firing');
  });

  it('omits absent optional identifiers', () => {
    const { queryByText } = render(JobSummary, { job: job({}) });
    expect(queryByText('Tenant')).toBeNull();
    expect(queryByText('Correlation id')).toBeNull();
    expect(queryByText('Parent')).toBeNull();
  });
});
