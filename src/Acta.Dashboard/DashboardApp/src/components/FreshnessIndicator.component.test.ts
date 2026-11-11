import { render, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DETAIL_REFETCH_MS, listRefetchDelay, livePaused } from '../polling.ts';
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

    expect(screen.getByText('Update failed, retrying')).toBeTruthy();
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

  it('counts a polled view down with a meter instead of words, and restarts it when data arrives', async () => {
    const first = Date.now();
    const { container, rerender } = render(FreshnessIndicator, { dataUpdatedAt: first });
    const level = container.querySelector<HTMLElement>('.draining')!;
    expect(level.style.animationDuration).toBe(listRefetchDelay(first) + 'ms');
    expect(container.querySelector('.freshness-label')).toBeNull();

    await rerender({ dataUpdatedAt: first + 10_001 });
    const restarted = container.querySelector<HTMLElement>('.draining')!;
    expect(restarted).not.toBe(level);
    expect(restarted.style.animationDuration).toBe(listRefetchDelay(first + 10_001) + 'ms');
  });

  it('runs the meter over the interval a detail page polls at', () => {
    const { container } = render(FreshnessIndicator, { dataUpdatedAt: Date.now(), intervalMs: DETAIL_REFETCH_MS });

    expect(container.querySelector<HTMLElement>('.draining')!.style.animationDuration).toBe(DETAIL_REFETCH_MS + 'ms');
  });

  it('holds the meter still while paused, failing, or not polling', () => {
    const dataUpdatedAt = Date.now();
    expect(render(FreshnessIndicator, { dataUpdatedAt, isError: true }).container.querySelector('.draining')).toBeNull();
    expect(render(FreshnessIndicator, { dataUpdatedAt, polling: false }).container.querySelector('.draining')).toBeNull();
    livePaused.set(true);
    expect(render(FreshnessIndicator, { dataUpdatedAt }).container.querySelector('.draining')).toBeNull();
  });

  it('states the age of a view that does not refresh itself to the minute', async () => {
    vi.useFakeTimers();
    vi.setSystemTime('2026-07-15T10:00:00Z');
    render(FreshnessIndicator, { dataUpdatedAt: Date.now(), polling: false });

    expect(screen.getByText('Updated just now')).toBeTruthy();
    await vi.advanceTimersByTimeAsync(30_000);
    expect(screen.getByText('Updated just now')).toBeTruthy();
    await vi.advanceTimersByTimeAsync(90_000);
    expect(screen.getByText('Updated 2m ago')).toBeTruthy();
  });
});
