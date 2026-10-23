import { render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import JobExecutionsPanelHarness from '../../test/JobExecutionsPanelHarness.svelte';
import type { EventItem } from '../../api.ts';
import type { JobDetail } from './types.ts';

const workerRef = 'wrk_019f9b3508007e4dbfd13d9d20bc81f4';

function executingJob(executionNumber: number): JobDetail {
  return {
    jobRef: 'job_019f9b3509487d1b911f2020158384a3',
    jobNamespace: 'billing',
    jobName: 'send-invoice',
    lineageRootJobRef: null,
    parentJobRef: null,
    tenantName: null,
    deduplicationKey: null,
    correlationKey: null,
    concurrencyKey: null,
    inputFormatId: 1,
    status: 'executing',
    priority: 'normal',
    lane: null,
    blockedBehindJobRef: null,
    nextRunAtUtc: '2026-07-14T07:59:00Z',
    executionNumber,
    failureCount: executionNumber - 1,
    leasedByWorkerRef: workerRef,
    leaseExpiresAtUtc: '2026-07-14T08:05:00Z',
    retentionUntilUtc: null,
    createdAtUtc: '2026-07-14T07:55:00Z',
    modifiedAtUtc: '2026-07-14T08:00:00Z',
    version: executionNumber
  };
}

function failed(executionNumber: number): EventItem {
  return {
    eventId: executionNumber,
    eventCode: 'job.execution-failed',
    createdAtUtc: '2026-07-14T07:58:00Z',
    jobNamespace: 'billing',
    entityKind: 'job',
    entityId: 42,
    jobName: 'send-invoice',
    jobRef: 'job_019f9b3509487d1b911f2020158384a3',
    lineageRootJobRef: null,
    tenantName: null,
    workerRef,
    executionNumber,
    durationMs: 125,
    message: 'Invoice provider timed out.',
    detailsText: '{"reason":"unhandled-exception","to":"ready"}'
  };
}

function serveEvents(items: EventItem[]): void {
  const page = { items, nextCursor: null, hasMore: false, pageSize: 100, totalCount: items.length };
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(page), { status: 200 })));
}

describe('JobExecutionsPanel', () => {
  it('shows a first attempt in flight as an executing row, not as missing events', async () => {
    serveEvents([]);
    render(JobExecutionsPanelHarness, { job: executingJob(1) });

    expect(await screen.findByText('Executing')).toBeTruthy();
    expect(screen.getByText(/Showing 1 of 1 executions/)).toBeTruthy();
    expect(screen.getByRole('link', { name: workerRef })).toBeTruthy();
    expect(screen.queryByText(/No execution events retained/)).toBeNull();
  });

  it('counts no execution missing when every finished attempt is on record', async () => {
    serveEvents([failed(1)]);
    render(JobExecutionsPanelHarness, { job: executingJob(2) });

    expect(await screen.findByText('Failed')).toBeTruthy();
    expect(screen.getByText('Executing')).toBeTruthy();
    expect(screen.queryByText(/earlier executions have no retained events/)).toBeNull();
  });

  it('counts only the earlier attempts without events as missing', async () => {
    serveEvents([]);
    render(JobExecutionsPanelHarness, { job: executingJob(3) });

    expect(await screen.findByText('2 earlier executions have no retained events (event recording or event retention).')).toBeTruthy();
  });
});
