import { render, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createAppQueryClient } from '../query.ts';
import QuickSearch from './QuickSearch.svelte';

function jsonResponse(payload: unknown): Response {
  return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function stubApi(): void {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    const path = url.pathname.split('/api/v1/')[1] ?? '';
    if (path === 'capabilities') {
      return jsonResponse({ provider: 'sqlite', schema: 'acta', controls: { enabled: false } });
    }
    if (path === 'definitions') {
      const items = 'mica-settle'.includes(url.searchParams.get('nameContains') ?? '')
        ? [{ jobNamespace: 'billing', jobName: 'mica-settle' }]
        : [];
      return jsonResponse({ items, hasMore: false, nextCursor: null });
    }
    if (path === 'namespaces') {
      const items = 'billing'.includes(url.searchParams.get('nameContains') ?? '')
        ? [{ jobNamespace: 'billing', status: 'active', ownerTeam: null, description: null, version: 1 }]
        : [];
      return jsonResponse({ items, hasMore: false, nextCursor: null });
    }
    if (path === 'tenants') {
      return jsonResponse({ items: [], hasMore: false, nextCursor: null });
    }
    return jsonResponse({ items: [], hasMore: false, nextCursor: null });
  }));
}

function renderPalette() {
  return render(QuickSearch, { props: { client: createAppQueryClient() } });
}

describe('QuickSearch', () => {
  beforeEach(() => {
    stubApi();
    localStorage.clear();
    location.hash = '';
  });
  afterEach(() => {
    location.hash = '';
  });

  it('opens on Ctrl+K, focuses the input, and closes on Escape restoring focus', async () => {
    const user = userEvent.setup();
    renderPalette();

    const outside = document.createElement('button');
    outside.textContent = 'somewhere else';
    document.body.append(outside);
    outside.focus();

    await user.keyboard('{Control>}k{/Control}');
    const input = screen.getByRole('combobox', { name: 'Quick search' });
    expect(document.activeElement).toBe(input);

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Quick search' })).toBeNull();
    expect(document.activeElement).toBe(outside);
    outside.remove();
  });

  it('does not open on / while typing in another input', async () => {
    const user = userEvent.setup();
    renderPalette();

    const field = document.createElement('input');
    document.body.append(field);
    field.focus();
    await user.keyboard('/');

    expect(screen.queryByRole('dialog', { name: 'Quick search' })).toBeNull();
    expect(field.value).toBe('/');
    field.remove();
  });

  it('jumps straight to a pasted job ref', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.keyboard('{Control>}k{/Control}');
    await user.keyboard('job_01k2zk03vf6fh0aeds62dscdvb');
    await user.keyboard('{Enter}');

    expect(location.hash).toContain('#/jobs/job_01k2zk03vf6fh0aeds62dscdvb');
    expect(screen.queryByRole('dialog', { name: 'Quick search' })).toBeNull();
  });

  it('finds definitions by fragment and navigates with the keyboard', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.keyboard('{Control>}k{/Control}');
    await user.keyboard('MICA');

    const hit = await screen.findByText('mica-settle');
    expect(hit).toBeTruthy();

    await user.keyboard('{Enter}');
    expect(location.hash).toContain('#/definitions/billing/mica-settle');
  });

  it('switches the namespace scope from a namespace hit and stays open', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.keyboard('{Control>}k{/Control}');
    await user.keyboard('bill');
    const hit = await screen.findByText('billing');
    expect(hit).toBeTruthy();
    await user.keyboard('{Enter}');

    expect(location.hash).toContain('ns=billing');
    const input = screen.getByRole('combobox', { name: 'Quick search' });
    expect((input as HTMLInputElement).value).toBe('');
    expect(document.activeElement).toBe(input);
  });

  it('lists recent selections when reopened empty', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.keyboard('{Control>}k{/Control}');
    await user.keyboard('MICA');
    await screen.findByText('mica-settle');
    await user.keyboard('{Enter}');

    await user.keyboard('{Control>}k{/Control}');
    expect(screen.getByText('Recent')).toBeTruthy();
    expect(screen.getByText('mica-settle')).toBeTruthy();
  });
});
