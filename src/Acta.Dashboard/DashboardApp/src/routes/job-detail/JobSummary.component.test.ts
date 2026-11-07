import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobSummary from './JobSummary.svelte';
import type { JobDetail } from './types.ts';
import { routes } from '../../routes.ts';

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

describe('JobSummary', () => {
  it('links the lane to the jobs list filtered to it', () => {
    const { container } = render(JobSummary, { job: job({ lane: 'customer-42' }) });
    const href = routes.jobs({ lane: 'customer-42', namespace: 'billing' });
    const link = Array.from(container.querySelectorAll('a')).find((a) => a.getAttribute('href') === href);
    expect(link?.textContent).toBe('customer-42');
  });

  it('names and links the job a Blocked member waits behind', () => {
    const { container, getByText } = render(JobSummary, { job: job({ status: 'blocked', lane: 'customer-42', blockedBehindJobRef: head }) });
    expect(getByText('Blocked behind')).toBeTruthy();
    expect(container.querySelector(`a[href="${routes.job(head, { namespace: 'billing' })}"]`)).not.toBeNull();
  });

  it('shows no blocked-behind row for a job that is not Blocked', () => {
    const { queryByText } = render(JobSummary, { job: job({ lane: 'customer-42' }) });
    expect(queryByText('Blocked behind')).toBeNull();
  });

  it('reads a saturated failure count as a floor', () => {
    const { getByText } = render(JobSummary, { job: job({ executionNumber: 40000, failureCount: 32767 }) });
    expect(getByText(/40000 started, 32,767\+ consecutive failures/)).toBeTruthy();
  });

  it('quotes the last event\'s reason from its details and its message', () => {
    const lastEvent = {
      eventId: 9,
      eventCode: 'job.execution-failed',
      createdAtUtc: '2026-07-14T08:00:00Z',
      jobNamespace: 'billing',
      entityKind: 'job',
      entityId: 3,
      jobName: 'send-invoice',
      jobRef: 'job_019f71ce69e07b85bf41fc7bf0562502',
      lineageRootJobRef: null,
      tenantName: null,
      workerRef: null,
      executionNumber: 1,
      durationMs: 12,
      message: 'Invoice provider timed out.',
      detailsText: '{"reason":"unhandled-exception","to":"ready"}'
    };
    const { getByText } = render(JobSummary, { job: job({}), lastEvent });
    expect(getByText('· unhandled-exception')).toBeTruthy();
    expect(getByText(/“Invoice provider timed out\.”/)).toBeTruthy();
  });
});
