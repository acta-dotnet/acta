import { fireEvent, render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import JobControlsHarness from '../test/JobControlsHarness.svelte';
import { routes } from '../routes.ts';

const jobRef = 'job_01kxrwwtf0fe2vygfwffr5c981';
const redriveJobRef = 'job_01kxrwwtf0fe2vygfwffr5c999';

function respond(url: string): Response {
  if (url.includes('/capabilities')) {
    return new Response(
      JSON.stringify({ controlsEnabled: true, version: '1', provider: 'sqlite', schema: 'acta', confirmationHeader: 'X-Acta-Control' }),
      { status: 200 }
    );
  }
  return new Response(
    JSON.stringify({
      jobRef,
      action: 'applied',
      status: 'succeeded',
      message: 'Restart applied: the finished job stays as history and a new job joined the end of its lane.',
      version: 4,
      redriveJobRef
    }),
    { status: 200 }
  );
}

describe('JobControls', () => {
  it('links to the new job when a restart redrives a finished laned job', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => respond(String(input))));
    render(JobControlsHarness, { jobRef, status: 'succeeded' });

    await fireEvent.click(await screen.findByRole('button', { name: /Run again/ }));
    await fireEvent.input(screen.getByRole('textbox'), { target: { value: 'replay the order' } });
    const confirm = screen.getAllByRole('button', { name: /Run again/ }).at(-1)!;
    await fireEvent.click(confirm);

    const link = await screen.findByRole('link', { name: 'Open the new job' });
    expect(link.getAttribute('href')).toBe(routes.job(redriveJobRef));
  });
});
