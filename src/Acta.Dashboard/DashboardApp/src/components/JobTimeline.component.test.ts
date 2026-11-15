import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobTimeline from './JobTimeline.svelte';

// Execution 21 failed and execution 22 succeeded: the number counts claims, so a resume or a bounce adds one too.
const events = [
  { eventId: 2, eventCode: 'job.execution-succeeded', createdAtUtc: '2026-07-14T09:00:00Z', executionNumber: 22, durationMs: 40, message: null, detailsText: null },
  { eventId: 1, eventCode: 'job.execution-failed', createdAtUtc: '2026-07-14T08:00:00Z', executionNumber: 21, durationMs: 40, message: 'Carrier refused the label.', detailsText: null }
];

describe('JobTimeline', () => {
  it('labels each group by its execution, not an attempt', () => {
    const { container } = render(JobTimeline, { props: { events } });
    expect(Array.from(container.querySelectorAll('.timeline-eyebrow')).map((eyebrow) => eyebrow.textContent?.trim())).toEqual(['Execution 21', 'Execution 22']);
    expect(screen.getByLabelText('Execution')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Jump to latest failed execution' })).toBeTruthy();
    expect(screen.queryByText(/attempt/i)).toBeNull();
  });

  it('links a child landing to the child its details name', () => {
    const child = 'job_0123456789abcdef0123456789abcdef';
    const landing = {
      eventId: 3,
      eventCode: 'job.signal-raised',
      createdAtUtc: '2026-07-14T10:00:00Z',
      executionNumber: 2,
      durationMs: null,
      message: "Child 'echo' succeeded.",
      jobNamespace: 'shipping',
      detailsText: JSON.stringify({ child, status: 'succeeded' })
    };
    render(JobTimeline, { props: { events: [landing] } });
    expect(screen.getByRole('link', { name: 'open the child' }).getAttribute('href')).toContain(child);
  });
});
