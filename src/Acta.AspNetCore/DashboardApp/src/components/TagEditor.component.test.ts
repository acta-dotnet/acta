import { render, screen, waitFor } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import TagEditorHarness from '../test/TagEditorHarness.svelte';

interface StubOptions {
  controlsEnabled?: boolean;
  tags?: { name: string; value: string | null }[];
}

// Route fetch by path + method: capabilities gate, the tag GET, and record mutation calls.
function stubFetch(options: StubOptions = {}) {
  const calls: { url: string; method: string; body: unknown; confirm: string | null }[] = [];
  const fetchMock = vi.fn(async (input: URL | RequestInfo, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    calls.push({
      url,
      method,
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
      confirm: new Headers(init?.headers).get('x-acta-control')
    });
    if (url.includes('/capabilities')) {
      return new Response(
        JSON.stringify({ controlsEnabled: options.controlsEnabled ?? true, version: 'v', provider: 'sqlite', confirmationHeader: 'X-Acta-Control' }),
        { status: 200 }
      );
    }
    if (url.endsWith('/tags') && method === 'GET') {
      return new Response(JSON.stringify(options.tags ?? [{ name: 'env', value: 'prod' }, { name: 'team', value: null }]), { status: 200 });
    }
    return new Response(JSON.stringify({ action: 'applied', version: null }), { status: 200 });
  });
  vi.stubGlobal('fetch', fetchMock);
  return calls;
}

afterEach(() => vi.unstubAllGlobals());

describe('TagEditor', () => {
  it('renders the target tags as chips', async () => {
    stubFetch();
    render(TagEditorHarness, { path: 'jobs/job_1/tags' });

    expect(await screen.findByText('env: prod')).toBeTruthy();
    expect(screen.getByText('team')).toBeTruthy();
  });

  it('posts a parsed name:value tag with the confirmation header', async () => {
    const user = userEvent.setup();
    const calls = stubFetch();
    render(TagEditorHarness, { path: 'jobs/job_1/tags' });

    await screen.findByText('env: prod');
    await user.type(screen.getByLabelText('New tag'), 'region:eu');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    await waitFor(() => {
      const post = calls.find((c) => c.method === 'POST');
      expect(post).toBeTruthy();
      expect(post!.body).toEqual({ name: 'region', value: 'eu' });
      expect(post!.confirm).toBe('true');
    });
  });

  it('hides add and remove controls when controls are disabled', async () => {
    stubFetch({ controlsEnabled: false });
    render(TagEditorHarness, { path: 'jobs/job_1/tags' });

    await screen.findByText('env: prod');
    expect(screen.queryByLabelText('New tag')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove env' })).toBeNull();
  });

  it('renders nothing when read-only and empty', async () => {
    stubFetch({ controlsEnabled: false, tags: [] });
    const { container } = render(TagEditorHarness, { path: 'jobs/job_1/tags' });

    await waitFor(() => expect(screen.queryByText('Loading tags...')).toBeNull());
    expect(screen.queryByText('Tags')).toBeNull();
    expect(screen.queryByText('No tags.')).toBeNull();
    expect(container.textContent).toBe('');
  });
});
