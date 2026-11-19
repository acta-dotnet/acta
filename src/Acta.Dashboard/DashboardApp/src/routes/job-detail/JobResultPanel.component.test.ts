import { render, screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import JobResultPanel from './JobResultPanel.svelte';

const jobRef = 'job_019f9b3509487d1b911f2020158384a3';

describe('JobResultPanel', () => {
  it('reads a stored result from its own route and offers it for download', async () => {
    const fetch = vi.fn(
      async (_input: URL | RequestInfo) =>
        new Response('{"sent":true}', { status: 200, headers: { 'Content-Type': 'application/json' } })
    );
    vi.stubGlobal('fetch', fetch);
    const { container } = render(JobResultPanel, { jobRef, formatId: 1, byteLength: 13, version: '1|13|t1' });

    await waitFor(() => expect(container.querySelector('pre')?.textContent).toBe('{\n  "sent": true\n}'));
    expect(new URL(String(fetch.mock.calls[0]![0])).pathname).toBe(`/api/v1/jobs/${jobRef}/result`);
    const download = screen.getByRole('link', { name: 'Download result' });
    expect(download.getAttribute('download')).toBe(`${jobRef}-result.json`);
  });
});
