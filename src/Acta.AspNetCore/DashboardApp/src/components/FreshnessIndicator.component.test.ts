import { render, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { livePaused } from '../polling.ts';
import { online } from '../api.ts';
import FreshnessIndicator from './FreshnessIndicator.svelte';

describe('FreshnessIndicator', () => {
  afterEach(() => vi.useRealTimers());

  it('shows the paused state', () => {
    livePaused.set(true);
    render(FreshnessIndicator);

    expect(screen.getByText('Live updates paused')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Resume live updates' }) as HTMLButtonElement).ariaPressed).toBe('true');
  });

  it('shows an update error', () => {
    render(FreshnessIndicator, { isError: true });

    expect(screen.getByText('Update failed — retrying')).toBeTruthy();
  });

  it('calls the manual refresh callback', async () => {
    const user = userEvent.setup();
    const onRefresh = vi.fn();
    render(FreshnessIndicator, { onRefresh });

    await user.click(screen.getByRole('button', { name: 'Refresh now' }));
    expect(onRefresh).toHaveBeenCalledOnce();
  });

  it('announces connection transitions without making the visual timestamp a live region', async () => {
    render(FreshnessIndicator, { dataUpdatedAt: Date.now() });
    expect(screen.queryByRole('status')).toBeNull();

    online.set(false);
    expect(await screen.findByText('Backend connection lost.')).toBeTruthy();
    online.set(true);
    expect(await screen.findByText('Reconnected.')).toBeTruthy();
  });

  it('never describes a freshly observed response as being in the future', () => {
    render(FreshnessIndicator, { dataUpdatedAt: Date.now() + 20_000 });

    expect(screen.getByText('Updated now')).toBeTruthy();
  });

  it('advances the sub-minute timestamp between query refreshes', async () => {
    vi.useFakeTimers();
    vi.setSystemTime('2026-07-15T10:00:00Z');
    render(FreshnessIndicator, { dataUpdatedAt: Date.now() });

    expect(screen.getByText('Updated now')).toBeTruthy();
    await vi.advanceTimersByTimeAsync(5_000);
    expect(screen.getByText('Updated 5s ago')).toBeTruthy();
  });
});
