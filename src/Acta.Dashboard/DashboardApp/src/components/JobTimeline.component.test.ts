import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobTimeline from './JobTimeline.svelte';
import type { ExecutionStep } from './jobTimelineState.ts';

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

  it('calls the instant ahead a run unless the page says what it is', () => {
    const { unmount } = render(JobTimeline, { props: { events, nextRunAtUtc: '2026-07-14T10:00:00Z' } });
    expect(screen.getByText('Next run')).toBeTruthy();
    unmount();

    // A waiting job's instant is its wait's deadline: nothing runs then unless the wait times out.
    render(JobTimeline, { props: { events, nextRunAtUtc: '2026-07-14T10:00:00Z', nextRunLabel: 'Times out' } });
    expect(screen.getByText('Times out')).toBeTruthy();
    expect(screen.queryByText('Next run')).toBeNull();
  });

  // Execution 21 failed in its last step; execution 22 ran that step alone.
  const succeeded = (name: string): ExecutionStep => ({ name, state: 'succeeded' });
  const started = (name: string): ExecutionStep => ({ name, state: 'started' });
  const stepsOf = (container: HTMLElement, execution: number) => container.querySelector(`#attempt-${execution} .timeline-steps`) as HTMLElement | null;

  it("lists each execution's steps under its name, a success with a check and an unfinished step as started", () => {
    const stepLines = new Map([
      [21, [succeeded('book-shipment'), succeeded('charge-card'), succeeded('issue-invoice'), started('send-receipt')]],
      [22, [succeeded('send-receipt')]]
    ]);
    const { container } = render(JobTimeline, { props: { events, stepLines } });

    const first = stepsOf(container, 21)!;
    const list = within(first).getByRole('list', { name: 'steps' });
    expect(within(list).getAllByRole('listitem').map((item) => item.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'book-shipment succeeded',
      'charge-card succeeded',
      'issue-invoice succeeded',
      'send-receipt started'
    ]);
    // The check is a glyph with its word for a screen reader, and an unfinished step never reads as failed.
    expect(first.querySelectorAll('.timeline-step-check svg')).toHaveLength(3);
    expect(first.querySelectorAll('.timeline-step-check .sr-only')).toHaveLength(3);
    expect(first.textContent).not.toMatch(/fail/i);
    expect(stepsOf(container, 22)!.textContent?.replace(/\s+/g, ' ').trim()).toBe('steps send-receipt succeeded');
    // The line sits with the execution's name, ahead of its events.
    expect(first.previousElementSibling?.classList.contains('timeline-eyebrow')).toBe(true);
  });

  it('draws no line for an execution that recorded no step', () => {
    const { container } = render(JobTimeline, { props: { events, stepLines: new Map([[22, [succeeded('send-receipt')]]]) } });
    expect(stepsOf(container, 21)).toBeNull();
    expect(container.querySelectorAll('.timeline-steps')).toHaveLength(1);
  });

  it('keeps the newest steps of a long line, its unfinished last one in view, and unfolds the earlier ones in place', async () => {
    const line = [...Array.from({ length: 10 }, (_, i) => succeeded('step-' + String(i + 1).padStart(2, '0'))), started('send-receipt')];
    const { container } = render(JobTimeline, { props: { events, stepLines: new Map([[21, line]]) } });
    const steps = stepsOf(container, 21)!;
    const items = () => Array.from(steps.querySelectorAll('li')).map((item) => item.textContent?.replace(/\s+/g, ' ').trim());

    const earlier = within(steps).getByRole('button', { name: '+3 earlier steps' });
    expect(earlier.getAttribute('aria-expanded')).toBe('false');
    expect(items()[0]).toBe('+3 earlier steps');
    expect(items().slice(1)).toEqual(line.slice(3).map((step) => step.name + ' ' + step.state));
    expect(items().at(-1)).toBe('send-receipt started');

    await fireEvent.click(earlier);
    const fewer = within(steps).getByRole('button', { name: 'show fewer steps' });
    expect(fewer.getAttribute('aria-expanded')).toBe('true');
    expect(items().slice(1)).toEqual(line.map((step) => step.name + ' ' + step.state));

    await fireEvent.click(fewer);
    expect(within(steps).getByRole('button', { name: '+3 earlier steps' }).getAttribute('aria-expanded')).toBe('false');
    expect(items()).toHaveLength(9);
  });
});
