import { render, screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import JobCheckpointsPanel from './JobCheckpointsPanel.svelte';
import type { JobCheckpoint } from '../../api.ts';

const jobRef = 'job_019f9b3509487d1b911f2020158384a3';

const counter: JobCheckpoint = {
  kind: 'variable',
  name: 'counter',
  status: null,
  dueAtUtc: null,
  valueFormatId: 1,
  valueByteLength: 11,
  createdAtUtc: '2026-07-14T07:56:00Z',
  modifiedAtUtc: '2026-07-14T07:57:00Z'
};

const pending: JobCheckpoint = {
  kind: 'signal',
  name: 'approval',
  status: 'pending',
  dueAtUtc: null,
  valueFormatId: 0,
  valueByteLength: null,
  createdAtUtc: '2026-07-14T07:56:00Z',
  modifiedAtUtc: '2026-07-14T07:56:00Z'
};

// Answers each value read with the next response, and records the path it asked for.
function serveValues(...responses: (() => Response)[]): string[] {
  const paths: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: URL | RequestInfo) => {
      paths.push(new URL(String(input)).pathname);
      return responses.shift()!();
    })
  );
  return paths;
}

const json = (text: string) => () => new Response(text, { status: 200, headers: { 'Content-Type': 'application/json' } });

function toggle(container: HTMLElement, open: boolean): void {
  const details = container.querySelector('details')!;
  details.open = open;
  details.dispatchEvent(new Event('toggle'));
}

describe('JobCheckpointsPanel', () => {
  it("states each value's format and size, offers its download in the row, and reads none until a row is opened", () => {
    const paths = serveValues();
    const { container } = render(JobCheckpointsPanel, { checkpoints: [counter, pending], jobRef });

    expect(screen.getByText('json, 11 B')).toBeTruthy();
    expect(container.querySelectorAll('details')).toHaveLength(1);
    const download = screen.getByRole('link', { name: 'Download variable counter' });
    expect(download.getAttribute('download')).toBe(`${jobRef}-variable-counter.json`);
    expect(download.closest('details')).toBeNull();
    expect(paths).toHaveLength(0);
  });

  it('reads a value when its row is first opened and renders it', async () => {
    const paths = serveValues(json('{"count":3}'));
    const { container } = render(JobCheckpointsPanel, { checkpoints: [counter], jobRef });

    toggle(container, true);

    await waitFor(() => expect(container.querySelector('pre')?.textContent).toBe('{\n  "count": 3\n}'));
    expect(paths).toEqual([`/api/v1/jobs/${jobRef}/checkpoints/variable/counter/value`]);
  });

  it('reads an open value again on a newer write, reads nothing while the row is closed, and catches up on reopening', async () => {
    const paths = serveValues(json('{"count":3}'), json('{"count":5}'), json('{"count":6}'));
    const { container, rerender } = render(JobCheckpointsPanel, { checkpoints: [counter], jobRef });
    toggle(container, true);
    await waitFor(() => expect(container.querySelector('pre')?.textContent).toContain('3'));

    await rerender({ checkpoints: [{ ...counter }], jobRef });
    expect(paths).toHaveLength(1);
    await rerender({ checkpoints: [{ ...counter, modifiedAtUtc: '2026-07-14T07:58:00Z' }], jobRef });
    await waitFor(() => expect(container.querySelector('pre')?.textContent).toContain('5'));

    toggle(container, false);
    await rerender({ checkpoints: [{ ...counter, modifiedAtUtc: '2026-07-14T07:59:00Z' }], jobRef });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(paths).toHaveLength(2);

    toggle(container, true);
    await waitFor(() => expect(container.querySelector('pre')?.textContent).toContain('6'));
    expect(paths).toHaveLength(3);
  });

  it('reads a write that keeps the time but changes the size', async () => {
    const paths = serveValues(json('{"count":3}'), json('{"count":30}'));
    const { container, rerender } = render(JobCheckpointsPanel, { checkpoints: [counter], jobRef });
    toggle(container, true);
    await waitFor(() => expect(paths).toHaveLength(1));

    await rerender({ checkpoints: [{ ...counter, valueByteLength: 12 }], jobRef });

    await waitFor(() => expect(container.querySelector('pre')?.textContent).toContain('30'));
    expect(paths).toHaveLength(2);
  });
});
