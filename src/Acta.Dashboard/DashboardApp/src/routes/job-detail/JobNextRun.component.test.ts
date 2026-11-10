import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobNextRun from './JobNextRun.svelte';
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

describe('JobNextRun', () => {
  it('says an idle permanent job runs when its soonest active schedule', () => {
    const schedules = [
      schedule('hourly', 'active', '2099-01-01T10:00:00Z'),
      schedule('paused-sooner', 'paused', '2099-01-01T08:00:00Z'),
      schedule('morning', 'active', '2099-01-01T09:00:00Z')
    ];
    const { container, getByText } = render(JobNextRun, {
      job: job({ kind: 'permanent', status: 'idle' }),
      schedules,
      total: 3
    });
    expect(getByText(/from/).textContent).toBe('from morning');
    expect(container.querySelector(`a[href="${routes.schedule('billing', 'send-invoice', 'morning')}"]`)).not.toBeNull();
  });

  it('fires an idle permanent job at a timed pause\'s next occurrence when that falls after the pause', () => {
    const schedules = [
      schedule('noon', 'active', '2099-01-01T12:00:00Z'),
      schedule('hourly', 'paused', '2099-01-01T10:05:00Z', '2099-01-01T10:00:00Z')
    ];
    const { container, getByText } = render(JobNextRun, { job: job({ kind: 'permanent', status: 'idle' }), schedules, total: 2 });
    expect(getByText(/from/).textContent).toBe('from hourly');
    expect(container.querySelector(`span[title="${displayFormatter.timestamp('2099-01-01T10:05:00Z')}"]`)).not.toBeNull();
  });

  // Under Skip the hourly schedule drops 09:05 and fires at 10:05; under a catch-up policy it fires at 10:00. The
  // detail carries no misfire policy, so the page names the known 10:02 firing and that hourly may come first.
  it('names the known firing and a timed pause that may fire first once it resumes before it', () => {
    const schedules = [
      schedule('hourly', 'paused', '2099-01-01T09:05:00Z', '2099-01-01T10:00:00Z'),
      schedule('ten-past', 'active', '2099-01-01T10:02:00Z')
    ];
    const { container, getByText } = render(JobNextRun, { job: job({ kind: 'permanent', status: 'idle' }), schedules, total: 2 });
    expect(getByText(/from/).textContent).toBe('from ten-past. Schedule hourly may fire first once it resumes');
    expect(container.querySelector(`span[title="${displayFormatter.timestamp('2099-01-01T10:02:00Z')}"]`)).not.toBeNull();
    expect(container.querySelector(`span[title="${displayFormatter.timestamp('2099-01-01T10:00:00Z')}"]`)).not.toBeNull();
  });

  it('leaves out a timed pause that resumes at or after the known firing', () => {
    const schedules = [
      schedule('hourly', 'paused', '2099-01-01T09:05:00Z', '2099-01-01T10:02:00Z'),
      schedule('nightly', 'paused', '2099-01-01T08:00:00Z', '2099-01-01T11:00:00Z'),
      schedule('ten-past', 'active', '2099-01-01T10:02:00Z')
    ];
    const { container, getByText, queryByText } = render(JobNextRun, { job: job({ kind: 'permanent', status: 'idle' }), schedules, total: 3 });
    expect(getByText(/from/).textContent).toBe('from ten-past');
    expect(container.querySelector(`span[title="${displayFormatter.timestamp('2099-01-01T10:02:00Z')}"]`)).not.toBeNull();
    expect(queryByText(/resumes/)).toBeNull();
  });

  it('says when the soonest timed pause ends for an idle permanent job with no known firing', () => {
    const schedules = [
      schedule('nightly', 'paused', '2099-01-01T08:00:00Z', '2099-01-01T11:00:00Z'),
      schedule('hourly', 'paused', '2099-01-01T09:05:00Z', '2099-01-01T10:00:00Z'),
      schedule('held', 'paused', '2099-01-01T07:00:00Z')
    ];
    const { container, getByText, queryByText } = render(JobNextRun, { job: job({ kind: 'permanent', status: 'idle' }), schedules, total: 3 });
    expect(getByText(/^Schedule/).textContent).toBe('Schedule hourly resumes');
    expect(container.querySelector(`span[title="${displayFormatter.timestamp('2099-01-01T10:00:00Z')}"]`)).not.toBeNull();
    expect(queryByText(/from/)).toBeNull();
    expect(queryByText('No schedule is set to fire it.')).toBeNull();
  });

  // An occurrence already past is one the scheduler has not admitted yet: under Skip it drops it for the next one, so
  // the page cannot rank it as a firing.
  it('names an overdue schedule as one that may fire first, not as the next firing', () => {
    const schedules = [schedule('daily', 'active', '2000-01-01T09:00:00Z'), schedule('noon', 'active', '2099-01-01T12:00:00Z')];
    const { getByText } = render(JobNextRun, { job: job({ kind: 'permanent', status: 'idle' }), schedules, total: 2 });
    expect(getByText(/from/).textContent).toBe('from noon. Schedule daily is overdue and may fire first');
  });

  it('says an overdue schedule leaves its next firing to its misfire policy when no firing is known', () => {
    const schedules = [schedule('daily', 'active', '2000-01-01T09:00:00Z')];
    const { getByText, queryByText } = render(JobNextRun, { job: job({ kind: 'permanent', status: 'idle' }), schedules, total: 1 });
    expect(getByText(/^Schedule/).textContent).toBe('Schedule daily is overdue. Its misfire policy decides what fires next.');
    expect(queryByText('No schedule is set to fire it.')).toBeNull();
  });

  it('says no schedule is set to fire an idle permanent job without one', () => {
    const { getByText } = render(JobNextRun, { job: job({ kind: 'permanent', status: 'idle' }), schedules: [], total: 0 });
    expect(getByText('No schedule is set to fire it.')).toBeTruthy();
  });

});
