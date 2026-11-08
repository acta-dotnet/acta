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

// A schedule of the job's definition as the detail carries it; a paused one keeps the cursor it had when paused.
function schedule(scheduleName: string, status: string, nextOccurrenceAtUtc: string | null, pausedUntilUtc: string | null = null) {
  return {
    jobNamespace: 'billing',
    jobName: 'send-invoice',
    scheduleName,
    expressionKind: 'cron',
    expression: '5 * * * *',
    timeZoneId: 'UTC',
    nextOccurrenceAtUtc,
    status,
    pausedUntilUtc,
    version: 1
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

  it('says an idle permanent job runs when its soonest active schedule fires', () => {
    const schedules = [
      schedule('hourly', 'active', '2099-01-01T10:00:00Z'),
      schedule('paused-sooner', 'paused', '2099-01-01T08:00:00Z'),
      schedule('morning', 'active', '2099-01-01T09:00:00Z')
    ];
    const { container, getByText } = render(JobSummary, {
      job: job({ kind: 'permanent', status: 'idle' }),
      schedules,
      schedulesTotal: 3
    });
    expect(getByText(/when schedule/).textContent).toBe('when schedule morning fires');
    expect(container.querySelector(`a[href="${routes.schedule('billing', 'send-invoice', 'morning')}"]`)).not.toBeNull();
  });

  it('fires an idle permanent job at a timed pause\'s next occurrence when that falls after the pause', () => {
    const schedules = [
      schedule('noon', 'active', '2099-01-01T12:00:00Z'),
      schedule('hourly', 'paused', '2099-01-01T10:05:00Z', '2099-01-01T10:00:00Z')
    ];
    const { container, getByText } = render(JobSummary, { job: job({ kind: 'permanent', status: 'idle' }), schedules, schedulesTotal: 2 });
    expect(getByText(/when schedule/).textContent).toBe('when schedule hourly fires');
    expect(container.querySelector(`span[title="${displayFormatter.timestamp('2099-01-01T10:05:00Z')}"]`)).not.toBeNull();
  });

  it('says when a timed pause ends for an idle permanent job whose next occurrence falls inside it', () => {
    const schedules = [schedule('hourly', 'paused', '2099-01-01T09:05:00Z', '2099-01-01T10:00:00Z')];
    const { container, getByText, queryByText } = render(JobSummary, { job: job({ kind: 'permanent', status: 'idle' }), schedules, schedulesTotal: 1 });
    expect(getByText(/^Schedule/).textContent).toBe('Schedule hourly resumes');
    expect(container.querySelector(`span[title="${displayFormatter.timestamp('2099-01-01T10:00:00Z')}"]`)).not.toBeNull();
    expect(queryByText('No schedule is set to fire it.')).toBeNull();
  });

  it('says no schedule is set to fire an idle permanent job without one', () => {
    const { getByText } = render(JobSummary, { job: job({ kind: 'permanent', status: 'idle' }), schedules: [], schedulesTotal: 0 });
    expect(getByText('No schedule is set to fire it.')).toBeTruthy();
  });

  it('counts a permanent job\'s attempts across its firings and gives each firing the budget', () => {
    const { getByText, queryByText } = render(JobSummary, {
      job: job({ kind: 'permanent', status: 'idle', executionNumber: 28 }),
      maxAttempts: 15
    });
    expect(getByText(/28 started across all firings, 0 consecutive failures in the latest firing/)).toBeTruthy();
    expect(getByText('(each firing gets 15 attempts)')).toBeTruthy();
    expect(queryByText(/fails permanently/)).toBeNull();
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
