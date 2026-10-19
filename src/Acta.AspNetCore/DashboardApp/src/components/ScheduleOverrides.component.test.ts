import { render, screen, waitFor } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import ControlDateEntryHarness from '../test/ControlDateEntryHarness.svelte';

function stubFetch(): unknown[] {
  const bodies: unknown[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
      const url = String(input);
      const body = url.endsWith('/api/v1/capabilities')
        ? { controlsEnabled: true, version: 'test', provider: 'mock', confirmationHeader: 'X-Acta-Control' }
        : (bodies.push(JSON.parse(init?.body as string)),
          { action: 'applied', status: 'active', pausedUntilUtc: null, nextRunAtUtc: null, version: 5 });
      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    })
  );
  return bodies;
}

// The server refuses a body member it does not declare, so the version the editor loaded must travel
// as expectedVersion: under any other name the save answers 400 and the override is never set.
describe('schedule overrides editor', () => {
  it('sends the loaded version as expectedVersion', async () => {
    const bodies = stubFetch();
    const user = userEvent.setup();
    render(ControlDateEntryHarness, { kind: 'schedule-editor' });

    await user.click(await screen.findByRole('button', { name: /Show overrides/ }));
    await user.type(screen.getByPlaceholderText('blank clears the override'), '0 */2 * * *');
    await user.click(screen.getByRole('button', { name: 'Save overrides' }));

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toEqual({ expectedVersion: 4, expression: '0 */2 * * *', timeZoneId: null, reasonMessage: null });
  });

  // The job-detail query polls, so the version prop can move while the draft is open. The save sends
  // both fields as the draft holds them, so it must claim the version they were read at: claiming the
  // newer one passes the version check and reverts a change the operator never saw.
  it('sends the version the draft was opened with after the prop moves', async () => {
    const bodies = stubFetch();
    const user = userEvent.setup();
    const { rerender } = render(ControlDateEntryHarness, { kind: 'schedule-editor' });

    await user.click(await screen.findByRole('button', { name: /Show overrides/ }));
    rerender({ version: 5 });
    await user.type(screen.getByPlaceholderText('blank clears the override'), '0 */2 * * *');
    await user.click(screen.getByRole('button', { name: 'Save overrides' }));

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ expectedVersion: 4 });
  });
});
