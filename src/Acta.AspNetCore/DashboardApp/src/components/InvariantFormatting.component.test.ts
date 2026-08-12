import { render, screen, waitFor, within } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { displayFormatter } from '../format.ts';
import ControlDateEntryHarness from '../test/ControlDateEntryHarness.svelte';
import JobTimeline from './JobTimeline.svelte';
import MetricCard from './MetricCard.svelte';
import Pager from './Pager.svelte';

function capabilitiesResponse(): Response {
  return new Response(
    JSON.stringify({ controlsEnabled: true, version: 'test', provider: 'mock', confirmationHeader: 'X-Acta-Control' }),
    { status: 200, headers: { 'Content-Type': 'application/json' } }
  );
}

interface CapturedRequest {
  url: string;
  init: RequestInit | undefined;
}

function mockControlFetch(requests: CapturedRequest[]) {
  return vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith('/api/v1/capabilities')) return capabilitiesResponse();

    requests.push({ url, init });
    const body = url.includes('/schedules/')
      ? {
          action: 'applied',
          status: 'paused',
          pausedUntilUtc: '2026-07-15T08:26:00.000Z',
          nextRunAtUtc: null,
          version: 2,
          message: 'Schedule paused.'
        }
      : { jobRef: 'job_test', action: 'applied', status: 'ready', message: 'Job rescheduled.' };
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  });
}

function requestBody(request: CapturedRequest): unknown {
  expect(typeof request.init?.body).toBe('string');
  return JSON.parse(request.init?.body as string);
}

describe('invariant dashboard formatting', () => {
  it('uses ungrouped invariant numbers at common presentation boundaries', () => {
    render(MetricCard, { label: 'Total jobs', value: 1234567.89 });
    expect(screen.getByText('1234567.89')).toBeTruthy();

    render(Pager, { totalCount: 1234567, visibleCount: 12345 });
    expect(screen.getByText('1234567 total')).toBeTruthy();
  });

  it('uses invariant attempt, duration, and event-count values in the timeline', () => {
    render(JobTimeline, {
      props: {
        events: [
          {
            executionNumber: 12345,
            eventCode: 'job.execution-finished',
            executionStatus: 'succeeded',
            durationMs: 1234567,
            createdAtUtc: '2026-07-15T14:05:06Z'
          }
        ]
      }
    });

    expect(screen.getAllByText(`Attempt ${displayFormatter.number(12345)}`).length).toBeGreaterThan(0);
    expect(screen.getByText(displayFormatter.milliseconds(1234567))).toBeTruthy();
    expect(screen.getByText('Full retained history loaded · 1 events')).toBeTruthy();
  });

  it('rejects locale-style job reschedule input and previews valid invariant UTC input', async () => {
    const requests: CapturedRequest[] = [];
    vi.stubGlobal('fetch', mockControlFetch(requests));
    const user = userEvent.setup();
    render(ControlDateEntryHarness, { kind: 'job' });

    await user.click(await screen.findByRole('button', { name: 'Change run time' }));
    const input = screen.getByLabelText(/Next run at/);
    const continueButton = screen.getByRole('button', { name: 'Continue' });

    await user.type(input, '15/07/2026 08:26');
    expect(screen.getByRole('alert').textContent).toContain('YYYY-MM-DD HH:mm');
    expect((continueButton as HTMLButtonElement).disabled).toBe(true);

    await user.clear(input);
    await user.type(input, '2026-07-15 08:26');
    expect(screen.queryByRole('alert')).toBeNull();
    expect((continueButton as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getByText('2026-07-15T08:26:00.000Z')).toBeTruthy();

    await user.click(continueButton);
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/2026-07-15T08:26:00.000Z/)).toBeTruthy();
    await user.click(within(dialog).getByRole('button', { name: 'Change run time' }));

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].url).toMatch(/\/api\/v1\/jobs\/job_test\/reschedule$/);
    expect(requests[0].init?.method).toBe('POST');
    expect(requestBody(requests[0])).toEqual({
      nextRunAtUtc: '2026-07-15T08:26:00.000Z',
      reasonMessage: null
    });
  });

  it('rejects invalid schedule pause input before submission', async () => {
    const requests: CapturedRequest[] = [];
    vi.stubGlobal('fetch', mockControlFetch(requests));
    const user = userEvent.setup();
    render(ControlDateEntryHarness, { kind: 'schedule' });

    await user.click(await screen.findByRole('button', { name: 'Pause until...' }));
    const input = screen.getByLabelText(/Resume at/);
    const applyButton = screen.getByRole('button', { name: 'Apply' });

    await user.type(input, '2026-02-29 08:26');
    expect(screen.getByRole('alert').textContent).toContain('valid UTC date');
    expect((applyButton as HTMLButtonElement).disabled).toBe(true);

    await user.clear(input);
    await user.type(input, '2026-07-15 08:26');
    await user.click(applyButton);

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].url).toMatch(/\/api\/v1\/schedules\/pause$/);
    expect(requests[0].init?.method).toBe('POST');
    expect(requestBody(requests[0])).toEqual({
      jobNamespace: 'billing',
      jobName: 'invoice',
      scheduleName: 'daily',
      note: null,
      pausedUntilUtc: '2026-07-15T08:26:00.000Z'
    });
  });
});
