import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobTimeline from './JobTimeline.svelte';

// A permanent job's execution 21 failed and its execution 22 succeeded: the number counts every firing's executions.
const events = [
  { eventId: 2, eventCode: 'job.execution-succeeded', createdAtUtc: '2026-07-14T09:00:00Z', executionNumber: 22, durationMs: 40, message: null, detailsText: null },
  { eventId: 1, eventCode: 'job.execution-failed', createdAtUtc: '2026-07-14T08:00:00Z', executionNumber: 21, durationMs: 40, message: 'Carrier refused the label.', detailsText: null }
];

describe('JobTimeline', () => {
  it('labels a permanent job\'s executions as executions, not attempts', () => {
    const { container } = render(JobTimeline, { props: { events, permanent: true } });
    expect(Array.from(container.querySelectorAll('.timeline-eyebrow')).map((eyebrow) => eyebrow.textContent?.trim())).toEqual(['Execution 21', 'Execution 22']);
    expect(screen.getByLabelText('Execution')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Jump to latest failed execution' })).toBeTruthy();
    expect(screen.queryByText(/attempt/i)).toBeNull();
  });

  it('labels a one-shot job\'s executions as attempts', () => {
    const { container } = render(JobTimeline, { props: { events } });
    expect(Array.from(container.querySelectorAll('.timeline-eyebrow')).map((eyebrow) => eyebrow.textContent?.trim())).toEqual(['Attempt 21', 'Attempt 22']);
    expect(screen.getByLabelText('Attempt')).toBeTruthy();
  });
});
